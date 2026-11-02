/* fire native platform layer, audio part in C on the ESP32: sound as PWM on a GPIO pin of your choice. The name of the device is "pwm:<gpio>" (for example "pwm:25";
 * any pin that can be an output). The pin is driven by a LEDC channel with a fast carrier (78 kHz, 8 bit) whose duty cycle follows the samples: a timer interrupt
 * (gptimer) sets the duty once per sample, taking the samples from a ring buffer that `write` fills. Connect the pin through a small RC low-pass filter (for example
 * 1 kOhm + 100 nF) to an amplifier, a piezo or a small speaker (a transistor or an amplifier for anything bigger than a piezo - never a speaker directly on the pin).
 * One output at a time (one LEDC timer is used).
 *
 * Samples are 16 bit signed like everywhere; 8 bits of them reach the pin (the PWM has 256 steps), 0 is the middle (duty 128). A stereo input is mixed down to one
 * channel. Rates from 2000 to 48000 Hz; 8000 to 22050 Hz are plenty for beeps, speech and simple melodies. While nothing is queued the pin rests at the middle (use
 * `close` to let it fall to 0). The interface is described in fire_audio_c.h. (Not tried on a board yet. Add the components `driver` (IDF 4.x) or `esp_driver_ledc` and
 * `esp_driver_gptimer` (IDF 5.x) to the project; set CONFIG_LEDC_CTRL_FUNC_IN_IRAM and CONFIG_GPTIMER_ISR_IRAM_SAFE so that the interrupt is not held up while flash
 * is written.) */
#ifndef FIRE_AUDIO_ESP32_H
#define FIRE_AUDIO_ESP32_H

#include "fire_audio_c.h"

#include <stdbool.h>

#include <driver/gpio.h>
#include <driver/gptimer.h>
#include <driver/ledc.h>
#include <esp_attr.h>
#include <esp_err.h>
#include <freertos/FreeRTOS.h>
#include <freertos/task.h>

#define FIRE_AUDIO_CARRIER_HZ 78125
#define FIRE_AUDIO_LEDC_MODE LEDC_LOW_SPEED_MODE
#define FIRE_AUDIO_LEDC_TIMER LEDC_TIMER_0
#define FIRE_AUDIO_LEDC_CHANNEL LEDC_CHANNEL_0

struct FireAudioOutput {
    int pin;
    int channels;
    int frameBytes;
    int bytesPerSecond;
    uint8_t* ring;
    uint32_t capacity;                 /* bytes, a multiple of the frame size */
    volatile uint32_t head;            /* the next byte `write` fills (written by the program only) */
    volatile uint32_t tail;            /* the next byte the interrupt plays (written by the interrupt only, and by flush) */
    volatile bool flush;
    bool idle;
    bool discard;
    gptimer_handle_t timer;
};

static bool g_fireAudioInUse = false;

static int fire_audio_supported(void) { return 1; }

static int fire_audio_devices(FireAudioList* names, FireAudioStatus* st) {
    fire_audio_listClear(names);
    fire_audio_listAdd(names, "pwm");   /* (open it as "pwm:<gpio>") */
    return fire_audio_success(st);
}

static bool IRAM_ATTR fire_audio_onTick(gptimer_handle_t timer, const gptimer_alarm_event_data_t* event, void* arg) {
    FireAudioOutput* o = (FireAudioOutput*)arg;
    uint32_t t;
    int32_t sum = 0, duty;
    int c;
    (void)timer; (void)event;
    if (o->flush) { o->tail = o->head; o->flush = false; }
    t = o->tail;
    if (t == o->head) {
        if (!o->idle) { ledc_set_duty_and_update(FIRE_AUDIO_LEDC_MODE, FIRE_AUDIO_LEDC_CHANNEL, 128, 0); o->idle = true; }
        return false;
    }
    for (c = 0; c < o->channels; c++) {
        uint32_t i = (t + (uint32_t)(2 * c)) % o->capacity;
        int16_t s = (int16_t)((uint16_t)o->ring[i] | ((uint16_t)o->ring[(i + 1) % o->capacity] << 8));
        sum += s;
    }
    o->tail = (t + (uint32_t)o->frameBytes) % o->capacity;
    duty = 128 + ((sum / o->channels) >> 8);
    if (duty < 0) duty = 0;
    if (duty > 255) duty = 255;
    ledc_set_duty_and_update(FIRE_AUDIO_LEDC_MODE, FIRE_AUDIO_LEDC_CHANNEL, (uint32_t)duty, 0);
    o->idle = false;
    return false;
}

static int fire_audio_open(const char* name, int rate, int channels, FireAudioOutput** out, FireAudioStatus* st) {
    int pin;
    ledc_timer_config_t timer;
    ledc_channel_config_t channel;
    gptimer_config_t tc;
    esp_err_t err;
    FireAudioOutput* o;
    *out = NULL;
    if (strcmp(name, "pwm") == 0) return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "Say which pin the speaker is on: \"pwm:<gpio>\", for example \"pwm:25\".");
    if (strncmp(name, "pwm:", 4) != 0 || !fire_audio_isNumber(name + 4))
        return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "There is no sound device '%s' (the ESP32 has \"pwm:<gpio>\").", name);
    pin = atoi(name + 4);
    if (pin < 0 || !GPIO_IS_VALID_OUTPUT_GPIO(pin)) return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "GPIO %d cannot be an output.", pin);
    if (rate < 2000 || rate > 48000) return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "The PWM output plays 2000 to 48000 Hz, not %d.", rate);
    if (channels < 1 || channels > 2) return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "The PWM output has one channel (a stereo input is mixed down).");
    if (g_fireAudioInUse) return fire_audio_fail(st, FIRE_AUDIO_E_BUSY, "The PWM output is in use already (there is one).");

    memset(&timer, 0, sizeof timer);
    timer.speed_mode = FIRE_AUDIO_LEDC_MODE;
    timer.duty_resolution = LEDC_TIMER_8_BIT;
    timer.timer_num = FIRE_AUDIO_LEDC_TIMER;
    timer.freq_hz = FIRE_AUDIO_CARRIER_HZ;
    timer.clk_cfg = LEDC_AUTO_CLK;
    err = ledc_timer_config(&timer);
    if (err != ESP_OK) return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "ledc_timer_config: %s", esp_err_to_name(err));
    memset(&channel, 0, sizeof channel);
    channel.gpio_num = pin;
    channel.speed_mode = FIRE_AUDIO_LEDC_MODE;
    channel.channel = FIRE_AUDIO_LEDC_CHANNEL;
    channel.timer_sel = FIRE_AUDIO_LEDC_TIMER;
    channel.duty = 128;
    channel.hpoint = 0;
    err = ledc_channel_config(&channel);
    if (err != ESP_OK) return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "ledc_channel_config: %s", esp_err_to_name(err));

    o = (FireAudioOutput*)calloc(1, sizeof(FireAudioOutput));
    if (o == NULL) { ledc_stop(FIRE_AUDIO_LEDC_MODE, FIRE_AUDIO_LEDC_CHANNEL, 0); return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory for the sound buffer."); }
    o->idle = true;
    o->pin = pin;
    o->channels = channels;
    o->frameBytes = 2 * channels;
    o->bytesPerSecond = rate * o->frameBytes;
    o->capacity = (uint32_t)(rate / 4) * (uint32_t)o->frameBytes + (uint32_t)o->frameBytes;   /* a quarter of a second */
    o->ring = (uint8_t*)malloc(o->capacity);
    if (!o->ring) { free(o); ledc_stop(FIRE_AUDIO_LEDC_MODE, FIRE_AUDIO_LEDC_CHANNEL, 0); return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "Out of memory for the sound buffer."); }

    memset(&tc, 0, sizeof tc);
    tc.clk_src = GPTIMER_CLK_SRC_DEFAULT;
    tc.direction = GPTIMER_COUNT_UP;
    tc.resolution_hz = 8000000;
    err = gptimer_new_timer(&tc, &o->timer);
    if (err == ESP_OK) {
        gptimer_event_callbacks_t cbs;
        gptimer_alarm_config_t alarm;
        memset(&cbs, 0, sizeof cbs);
        cbs.on_alarm = fire_audio_onTick;
        memset(&alarm, 0, sizeof alarm);
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
        free(o->ring);
        free(o);
        ledc_stop(FIRE_AUDIO_LEDC_MODE, FIRE_AUDIO_LEDC_CHANNEL, 0);
        return fire_audio_fail(st, FIRE_AUDIO_E_OTHER, "gptimer: %s", esp_err_to_name(err));
    }
    g_fireAudioInUse = true;
    *out = o;
    return fire_audio_success(st);
}

static uint32_t fire_audio_used(FireAudioOutput* o) { return (o->head + o->capacity - o->tail) % o->capacity; }

static int fire_audio_write(FireAudioOutput* o, const uint8_t* data, int bytes, int* accepted, FireAudioStatus* st) {
    uint32_t space, n, h, i;
    *accepted = 0;
    o->discard = false;
    space = o->capacity - (uint32_t)o->frameBytes - fire_audio_used(o);
    n = (uint32_t)bytes < space ? (uint32_t)bytes : space;
    n -= n % (uint32_t)o->frameBytes;
    h = o->head;
    for (i = 0; i < n; i++) o->ring[(h + i) % o->capacity] = data[i];
    o->head = (h + n) % o->capacity;
    *accepted = (int)n;
    return fire_audio_success(st);
}

static int64_t fire_audio_queued(FireAudioOutput* o) { return fire_audio_used(o); }

static void fire_audio_stop(FireAudioOutput* o) {
    o->discard = true;
    o->flush = true;   /* (the interrupt throws the queue away at its next tick) */
}

static void fire_audio_close(FireAudioOutput* o) {
    if (!o) return;
    if (!o->discard) {
        /* plays what was accepted (at most a quarter of a second) */
        int waited;
        for (waited = 0; waited < 600 && fire_audio_used(o) > 0; waited += 10) vTaskDelay(pdMS_TO_TICKS(10));
    }
    gptimer_stop(o->timer);
    gptimer_disable(o->timer);
    gptimer_del_timer(o->timer);
    ledc_stop(FIRE_AUDIO_LEDC_MODE, FIRE_AUDIO_LEDC_CHANNEL, 0);
    free(o->ring);
    g_fireAudioInUse = false;
    free(o);
}


/* ---- sound input ---------------------------------------------------------------------------------------------------------------------- */
/* The input side of the interface: an opened input captures 16 bit signed samples (little endian, interleaved) into its buffer; `read` takes what was captured (whole
 * frames) and returns at once, `available` says how much waits. A buffer that is not read in time loses its oldest sound. */
struct FireAudioInput { int unused; };
static int fire_audio_inputSupported(void) { return 0; }
static int fire_audio_inputDevices(FireAudioList* names, FireAudioStatus* st) { fire_audio_listClear(names); return fire_audio_success(st); }
static int fire_audio_openInput(const char* name, int rate, int channels, FireAudioInput** in, FireAudioStatus* st) {
    (void)name; (void)rate; (void)channels;
    *in = NULL;
    return fire_audio_fail(st, FIRE_AUDIO_E_NOT_FOUND, "This platform has no sound input (the simulated device \"sim\" works everywhere).");
}
static int fire_audio_read(FireAudioInput* in, uint8_t* data, int bytes, int* got, FireAudioStatus* st) {
    (void)in; (void)data; (void)bytes;
    *got = 0;
    return fire_audio_fail(st, FIRE_AUDIO_E_UNSUPPORTED, "This platform has no sound input.");
}
static int64_t fire_audio_available(FireAudioInput* in) { (void)in; return 0; }
static void fire_audio_closeInput(FireAudioInput* in) { (void)in; }

#endif
