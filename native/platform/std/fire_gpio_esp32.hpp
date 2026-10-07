// fire native platform layer, GPIO part on the ESP32: the GPIO driver of ESP-IDF (driver/gpio.h). The chip is called "gpio" and its lines are the GPIO numbers of the chip. Edges are caught
// by an interrupt handler that notes the level in a small queue per pin; `pollEdge` takes the next one (the fire code is never called from the interrupt). (Not tried on a board yet.)
// The interface is the one of fire_gpio_none.hpp.
#pragma once

#include <driver/gpio.h>
#include <esp_timer.h>

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace gpio {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

constexpr int kQueue = 16;

struct Line {
    int number = 0;
    int direction = 0;
    bool handler = false;
    // written by the interrupt handler (the only producer), read by pollEdge (the only consumer)
    volatile uint8_t events[kQueue] = {0};
    volatile int64_t times[kQueue] = {0};
    volatile uint32_t head = 0;   // the next slot to write
    volatile uint32_t tail = 0;   // the next slot to read
};

inline bool supported() { return true; }

inline Status chips(std::vector<std::string>& names) {
    names.clear();
    names.push_back("gpio");
    return success();
}

inline Status openLine(const std::string& chip, int line, Line*& out) {
    out = nullptr;
    if (chip != "gpio") return fail(E_NotFound, "There is no GPIO chip '" + chip + "' (the chip of the ESP32 is \"gpio\").");
    if (line < 0 || !GPIO_IS_VALID_GPIO(line)) return fail(E_NotFound, "GPIO " + std::to_string(line) + " does not exist on this chip.");
    Line* l = new Line();
    l->number = line;
    out = l;
    return success();
}

inline void IRAM_ATTR edgeHandler(void* arg) {
    Line* l = static_cast<Line*>(arg);
    uint32_t next = (l->head + 1) % kQueue;
    if (next == l->tail) return;   // full: the newest edge is dropped
    l->events[l->head] = gpio_get_level((gpio_num_t)l->number) ? 1 : 2;
    l->times[l->head] = esp_timer_get_time();
    l->head = next;
}

inline Status configure(Line* l, int direction, int pull, int edge, int value) {
    if (direction < 0 || direction > 1 || pull < 0 || pull > 2 || edge < 0 || edge > 3) return fail(E_InvalidArgument, "Invalid pin setup.");
    if (direction == 1 && edge != 0) return fail(E_InvalidArgument, "Edges can only be watched on an input.");
    gpio_num_t pin = (gpio_num_t)l->number;
    if (direction == 1 && !GPIO_IS_VALID_OUTPUT_GPIO(l->number)) return fail(E_InvalidArgument, "GPIO " + std::to_string(l->number) + " cannot be an output.");
    if (l->handler) { gpio_isr_handler_remove(pin); l->handler = false; }
    gpio_config_t cfg;
    std::memset(&cfg, 0, sizeof cfg);
    cfg.pin_bit_mask = 1ULL << l->number;
    cfg.mode = direction == 1 ? GPIO_MODE_INPUT_OUTPUT : GPIO_MODE_INPUT;   // (an output can be read back)
    cfg.pull_up_en = pull == 1 ? GPIO_PULLUP_ENABLE : GPIO_PULLUP_DISABLE;
    cfg.pull_down_en = pull == 2 ? GPIO_PULLDOWN_ENABLE : GPIO_PULLDOWN_DISABLE;
    cfg.intr_type = edge == 0 ? GPIO_INTR_DISABLE : edge == 1 ? GPIO_INTR_POSEDGE : edge == 2 ? GPIO_INTR_NEGEDGE : GPIO_INTR_ANYEDGE;
    if (direction == 1) gpio_set_level(pin, value ? 1 : 0);
    esp_err_t err = gpio_config(&cfg);
    if (err != ESP_OK) return fail(E_Other, std::string("gpio_config: ") + esp_err_to_name(err));
    if (direction == 1) gpio_set_level(pin, value ? 1 : 0);
    l->direction = direction;
    l->head = l->tail = 0;
    if (edge != 0) {
        err = gpio_install_isr_service(0);   // once for all pins (an error that says "already installed" is fine)
        if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) return fail(E_Other, std::string("gpio_install_isr_service: ") + esp_err_to_name(err));
        err = gpio_isr_handler_add(pin, edgeHandler, l);
        if (err != ESP_OK) return fail(E_Other, std::string("gpio_isr_handler_add: ") + esp_err_to_name(err));
        l->handler = true;
    }
    return success();
}

inline Status read(Line* l, int& value) {
    value = gpio_get_level((gpio_num_t)l->number) ? 1 : 0;
    return success();
}

inline Status write(Line* l, int value) {
    if (l->direction != 1) return fail(E_InvalidArgument, "The pin is an input.");
    gpio_set_level((gpio_num_t)l->number, value ? 1 : 0);
    return success();
}

inline Status pollEdge(Line* l, int& event, int64_t& micros) {
    event = 0;
    micros = 0;
    if (l->tail == l->head) return success();
    event = l->events[l->tail];
    micros = l->times[l->tail];
    l->tail = (l->tail + 1) % kQueue;
    return success();
}

inline void closeLine(Line* l) {
    if (!l) return;
    if (l->handler) gpio_isr_handler_remove((gpio_num_t)l->number);
    gpio_reset_pin((gpio_num_t)l->number);
    delete l;
}

}  // namespace gpio
}  // namespace plat
}  // namespace fire
