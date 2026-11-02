/* fire native platform layer, GPIO part in C on the ESP32: the GPIO driver of ESP-IDF (driver/gpio.h). The chip is called "gpio" and its lines are the GPIO
 * numbers of the chip. Edges are caught by an interrupt handler that notes the level in a small queue per pin; `fire_gpio_pollEdge` takes the next one (the fire
 * code is never called from the interrupt). (Not tried on a board yet.) The interface is the one of fire_gpio_c.h. */
#ifndef FIRE_GPIO_ESP32_H
#define FIRE_GPIO_ESP32_H

#include <driver/gpio.h>
#include <esp_err.h>
#include <esp_timer.h>

#include "fire_gpio_c.h"

#define FIRE_GPIO_QUEUE 16

struct FireGpioLine {
    int number;
    int direction;
    int handler;
    /* written by the interrupt handler (the only producer), read by pollEdge (the only consumer) */
    volatile uint8_t events[FIRE_GPIO_QUEUE];
    volatile int64_t times[FIRE_GPIO_QUEUE];
    volatile uint32_t head;   /* the next slot to write */
    volatile uint32_t tail;   /* the next slot to read */
};

static int fire_gpio_supported(void) { return 1; }

static int64_t fire_gpio_nowMicros(void) { return (int64_t)esp_timer_get_time(); }

static int fire_gpio_chips(FireGpioList* names, FireGpioStatus* st) {
    fire_gpio_listFree(names);
    fire_gpio_listAdd(names, "gpio");
    return fire_gpio_success(st);
}

static FireGpioLine* fire_gpio_openLine(const char* chip, int line, FireGpioStatus* st) {
    FireGpioLine* l;
    if (strcmp(chip, "gpio") != 0) { fire_gpio_fail(st, FIRE_GPIO_E_NOT_FOUND, "There is no GPIO chip '%s' (the chip of the ESP32 is \"gpio\").", chip); return NULL; }
    if (line < 0 || !GPIO_IS_VALID_GPIO(line)) { fire_gpio_fail(st, FIRE_GPIO_E_NOT_FOUND, "GPIO %d does not exist on this chip.", line); return NULL; }
    l = (FireGpioLine*)calloc(1, sizeof(FireGpioLine));
    if (l == NULL) { fire_gpio_fail(st, FIRE_GPIO_E_OTHER, "Out of memory."); return NULL; }
    l->number = line;
    fire_gpio_success(st);
    return l;
}

static void IRAM_ATTR fire_gpio_edgeHandler(void* arg) {
    FireGpioLine* l = (FireGpioLine*)arg;
    uint32_t next = (l->head + 1) % FIRE_GPIO_QUEUE;
    if (next == l->tail) return;   /* full: the newest edge is dropped */
    l->events[l->head] = gpio_get_level((gpio_num_t)l->number) ? 1 : 2;
    l->times[l->head] = esp_timer_get_time();
    l->head = next;
}

static int fire_gpio_configure(FireGpioLine* l, int direction, int pull, int edge, int value, FireGpioStatus* st) {
    gpio_num_t pin;
    gpio_config_t cfg;
    esp_err_t err;
    if (direction < 0 || direction > 1 || pull < 0 || pull > 2 || edge < 0 || edge > 3) return fire_gpio_fail(st, FIRE_GPIO_E_INVALID_ARGUMENT, "Invalid pin setup.");
    if (direction == 1 && edge != 0) return fire_gpio_fail(st, FIRE_GPIO_E_INVALID_ARGUMENT, "Edges can only be watched on an input.");
    pin = (gpio_num_t)l->number;
    if (direction == 1 && !GPIO_IS_VALID_OUTPUT_GPIO(l->number)) return fire_gpio_fail(st, FIRE_GPIO_E_INVALID_ARGUMENT, "GPIO %d cannot be an output.", l->number);
    if (l->handler) { gpio_isr_handler_remove(pin); l->handler = 0; }
    memset(&cfg, 0, sizeof cfg);
    cfg.pin_bit_mask = 1ULL << l->number;
    cfg.mode = direction == 1 ? GPIO_MODE_INPUT_OUTPUT : GPIO_MODE_INPUT;   /* (an output can be read back) */
    cfg.pull_up_en = pull == 1 ? GPIO_PULLUP_ENABLE : GPIO_PULLUP_DISABLE;
    cfg.pull_down_en = pull == 2 ? GPIO_PULLDOWN_ENABLE : GPIO_PULLDOWN_DISABLE;
    cfg.intr_type = edge == 0 ? GPIO_INTR_DISABLE : edge == 1 ? GPIO_INTR_POSEDGE : edge == 2 ? GPIO_INTR_NEGEDGE : GPIO_INTR_ANYEDGE;
    if (direction == 1) gpio_set_level(pin, value ? 1 : 0);
    err = gpio_config(&cfg);
    if (err != ESP_OK) return fire_gpio_fail(st, FIRE_GPIO_E_OTHER, "gpio_config: %s", esp_err_to_name(err));
    if (direction == 1) gpio_set_level(pin, value ? 1 : 0);
    l->direction = direction;
    l->head = l->tail = 0;
    if (edge != 0) {
        err = gpio_install_isr_service(0);   /* once for all pins (an error that says "already installed" is fine) */
        if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) return fire_gpio_fail(st, FIRE_GPIO_E_OTHER, "gpio_install_isr_service: %s", esp_err_to_name(err));
        err = gpio_isr_handler_add(pin, fire_gpio_edgeHandler, l);
        if (err != ESP_OK) return fire_gpio_fail(st, FIRE_GPIO_E_OTHER, "gpio_isr_handler_add: %s", esp_err_to_name(err));
        l->handler = 1;
    }
    return fire_gpio_success(st);
}

static int fire_gpio_read(FireGpioLine* l, int* value, FireGpioStatus* st) {
    *value = gpio_get_level((gpio_num_t)l->number) ? 1 : 0;
    return fire_gpio_success(st);
}

static int fire_gpio_write(FireGpioLine* l, int value, FireGpioStatus* st) {
    if (l->direction != 1) return fire_gpio_fail(st, FIRE_GPIO_E_INVALID_ARGUMENT, "The pin is an input.");
    gpio_set_level((gpio_num_t)l->number, value ? 1 : 0);
    return fire_gpio_success(st);
}

static int fire_gpio_pollEdge(FireGpioLine* l, int* event, int64_t* micros, FireGpioStatus* st) {
    *event = 0;
    *micros = 0;
    if (l->tail == l->head) return fire_gpio_success(st);
    *event = l->events[l->tail];
    *micros = l->times[l->tail];
    l->tail = (l->tail + 1) % FIRE_GPIO_QUEUE;
    return fire_gpio_success(st);
}

static void fire_gpio_closeLine(FireGpioLine* l) {
    if (!l) return;
    if (l->handler) gpio_isr_handler_remove((gpio_num_t)l->number);
    gpio_reset_pin((gpio_num_t)l->number);
    free(l);
}

#endif
