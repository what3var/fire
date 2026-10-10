// fire native platform layer, audio part on the ESP32: sound as PWM on a GPIO pin of your choice. The name of the device is "pwm:<gpio>" (for example "pwm:25"; any pin that can be an output).
// The pin is driven by a LEDC channel with a fast carrier (78 kHz, 8 bit) whose duty cycle follows the samples: a timer interrupt (gptimer) sets the duty once per sample, taking the samples from a
// ring buffer that `write` fills. Connect the pin through a small RC low-pass filter (for example 1 kOhm + 100 nF) to an amplifier, a piezo or a small speaker (a transistor or an amplifier for
// anything bigger than a piezo - never a speaker directly on the pin). One output at a time (one LEDC timer is used).
//
// Samples are 16 bit signed like everywhere; 8 bits of them reach the pin (the PWM has 256 steps), 0 is the middle (duty 128). A stereo input is mixed down to one channel. Rates from 2000 to 48000 Hz;
// 8000 to 22050 Hz are plenty for beeps, speech and simple melodies. While nothing is queued the pin rests at the middle (use `close` to let it fall to 0). The interface is the one of
// fire_audio_none.hpp. (Not tried on a board yet. Add the components `driver` (IDF 4.x) or `esp_driver_ledc` and `esp_driver_gptimer` (IDF 5.x) to the project; set CONFIG_LEDC_CTRL_FUNC_IN_IRAM
// and CONFIG_GPTIMER_ISR_IRAM_SAFE so that the interrupt is not held up while flash is written.)
#pragma once

#include <driver/gpio.h>
#include <driver/gptimer.h>
#include <driver/ledc.h>
#include <esp_attr.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

namespace fire {
namespace plat {
namespace audio {

enum Err { E_None = 0, E_InvalidArgument = 1, E_InvalidHandle = 2, E_NotFound = 3, E_Busy = 4, E_Permission = 5, E_Unsupported = 6, E_Other = 7 };

struct Status {
    int code = 0;
    std::string message;
    bool ok() const { return code == 0; }
};

inline Status fail(int code, const std::string& message) { Status s; s.code = code; s.message = message; return s; }
inline Status success() { return Status(); }

constexpr int kCarrierHz = 78125;
constexpr ledc_mode_t kMode = LEDC_LOW_SPEED_MODE;
constexpr ledc_timer_t kTimer = LEDC_TIMER_0;
constexpr ledc_channel_t kChannel = LEDC_CHANNEL_0;

struct Output {
    int pin = 0;
    int channels = 1;
    int frameBytes = 2;
    int bytesPerSecond = 0;
    uint8_t* ring = nullptr;
    uint32_t capacity = 0;                 // bytes, a multiple of the frame size
    volatile uint32_t head = 0;            // the next byte `write` fills (written by the program only)
    volatile uint32_t tail = 0;            // the next byte the interrupt plays (written by the interrupt only, and by flush)
    volatile bool flush = false;
    bool idle = true;
    bool discard = false;
    gptimer_handle_t timer = nullptr;
};

inline bool& inUse() { static bool used = false; return used; }

inline bool supported() { return true; }

inline Status devices(std::vector<std::string>& names) {
    names.clear();
    names.push_back("pwm");   // (open it as "pwm:<gpio>")
    return success();
}

inline bool IRAM_ATTR onTick(gptimer_handle_t, const gptimer_alarm_event_data_t*, void* arg) {
    Output* o = static_cast<Output*>(arg);
    if (o->flush) { o->tail = o->head; o->flush = false; }
    uint32_t t = o->tail;
    if (t == o->head) {
        if (!o->idle) { ledc_set_duty_and_update(kMode, kChannel, 128, 0); o->idle = true; }
        return false;
    }
    int32_t sum = 0;
    for (int c = 0; c < o->channels; c++) {
        uint32_t i = (t + (uint32_t)(2 * c)) % o->capacity;
        int16_t s = (int16_t)((uint16_t)o->ring[i] | ((uint16_t)o->ring[(i + 1) % o->capacity] << 8));
        sum += s;
    }
    o->tail = (t + (uint32_t)o->frameBytes) % o->capacity;
    int32_t duty = 128 + ((sum / o->channels) >> 8);
    if (duty < 0) duty = 0;
    if (duty > 255) duty = 255;
    ledc_set_duty_and_update(kMode, kChannel, (uint32_t)duty, 0);
    o->idle = false;
    return false;
}

inline Status open(const std::string& name, int rate, int channels, Output*& out) {
    out = nullptr;
    if (name == "pwm") return fail(E_NotFound, "Say which pin the speaker is on: \"pwm:<gpio>\", for example \"pwm:25\".");
    if (name.compare(0, 4, "pwm:") != 0 || name.size() == 4 || name.find_first_not_of("0123456789", 4) != std::string::npos)
        return fail(E_NotFound, "There is no sound device '" + name + "' (the ESP32 has \"pwm:<gpio>\").");
    int pin = std::atoi(name.c_str() + 4);
    if (pin < 0 || !GPIO_IS_VALID_OUTPUT_GPIO(pin)) return fail(E_NotFound, "GPIO " + std::to_string(pin) + " cannot be an output.");
    if (rate < 2000 || rate > 48000) return fail(E_Unsupported, "The PWM output plays 2000 to 48000 Hz, not " + std::to_string(rate) + ".");
    if (channels < 1 || channels > 2) return fail(E_Unsupported, "The PWM output has one channel (a stereo input is mixed down).");
    if (inUse()) return fail(E_Busy, "The PWM output is in use already (there is one).");

    ledc_timer_config_t timer;
    std::memset(&timer, 0, sizeof timer);
    timer.speed_mode = kMode;
    timer.duty_resolution = LEDC_TIMER_8_BIT;
    timer.timer_num = kTimer;
    timer.freq_hz = kCarrierHz;
    timer.clk_cfg = LEDC_AUTO_CLK;
    esp_err_t err = ledc_timer_config(&timer);
    if (err != ESP_OK) return fail(E_Other, std::string("ledc_timer_config: ") + esp_err_to_name(err));
    ledc_channel_config_t channel;
    std::memset(&channel, 0, sizeof channel);
    channel.gpio_num = pin;
    channel.speed_mode = kMode;
    channel.channel = kChannel;
    channel.timer_sel = kTimer;
    channel.duty = 128;
    channel.hpoint = 0;
    err = ledc_channel_config(&channel);
    if (err != ESP_OK) return fail(E_Other, std::string("ledc_channel_config: ") + esp_err_to_name(err));

    Output* o = new Output();
    o->pin = pin;
    o->channels = channels;
    o->frameBytes = 2 * channels;
    o->bytesPerSecond = rate * o->frameBytes;
    o->capacity = (uint32_t)(rate / 4) * (uint32_t)o->frameBytes + (uint32_t)o->frameBytes;   // a quarter of a second
    o->ring = static_cast<uint8_t*>(std::malloc(o->capacity));
    if (!o->ring) { delete o; ledc_stop(kMode, kChannel, 0); return fail(E_Other, "Out of memory for the sound buffer."); }

    gptimer_config_t tc;
    std::memset(&tc, 0, sizeof tc);
    tc.clk_src = GPTIMER_CLK_SRC_DEFAULT;
    tc.direction = GPTIMER_COUNT_UP;
    tc.resolution_hz = 8000000;
    err = gptimer_new_timer(&tc, &o->timer);
    if (err == ESP_OK) {
        gptimer_event_callbacks_t cbs;
        std::memset(&cbs, 0, sizeof cbs);
        cbs.on_alarm = onTick;
        gptimer_alarm_config_t alarm;
        std::memset(&alarm, 0, sizeof alarm);
        alarm.alarm_count = (uint64_t)(8000000 / rate);
        alarm.reload_count = 0;
        alarm.flags.auto_reload_on_alarm = true;
        err = gptimer_register_event_callbacks(o->timer, &cbs, o);
        if (err == ESP_OK) err = gptimer_set_alarm_action(o->timer, &alarm);
        if (err == ESP_OK) err = gptimer_enable(o->timer);
        if (err == ESP_OK) err = gptimer_start(o->timer);
    }
    if (err != ESP_OK) {
        if (o->timer) gptimer_del_timer(o->timer);
        std::free(o->ring);
        delete o;
        ledc_stop(kMode, kChannel, 0);
        return fail(E_Other, std::string("gptimer: ") + esp_err_to_name(err));
    }
    inUse() = true;
    out = o;
    return success();
}

inline uint32_t used(Output* o) { return (o->head + o->capacity - o->tail) % o->capacity; }

inline Status write(Output* o, const uint8_t* data, int bytes, int& accepted) {
    accepted = 0;
    o->discard = false;
    uint32_t space = o->capacity - (uint32_t)o->frameBytes - used(o);
    uint32_t n = (uint32_t)bytes < space ? (uint32_t)bytes : space;
    n -= n % (uint32_t)o->frameBytes;
    uint32_t h = o->head;
    for (uint32_t i = 0; i < n; i++) o->ring[(h + i) % o->capacity] = data[i];
    o->head = (h + n) % o->capacity;
    accepted = (int)n;
    return success();
}

inline int64_t queued(Output* o) { return used(o); }

inline void stop(Output* o) {
    o->discard = true;
    o->flush = true;   // (the interrupt throws the queue away at its next tick)
}

inline void close(Output* o) {
    if (!o) return;
    if (!o->discard) {
        // plays what was accepted (at most a quarter of a second)
        for (int waited = 0; waited < 600 && used(o) > 0; waited += 10) vTaskDelay(pdMS_TO_TICKS(10));
    }
    gptimer_stop(o->timer);
    gptimer_disable(o->timer);
    gptimer_del_timer(o->timer);
    ledc_stop(kMode, kChannel, 0);
    std::free(o->ring);
    inUse() = false;
    delete o;
}

}  // namespace audio
}  // namespace plat
}  // namespace fire
