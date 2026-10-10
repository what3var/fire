# Sound (`#import "audio"`, the package `fire-audio`)

Sound output: play samples, generate tones, play WAV files. Like every bridge it is a **package** (`ember`, docs/PACKAGES.md): a prelude in fire plus C++ in `native/bridges/fire_bridge_audio.hpp` over the platform layer `plat::audio`
(`native/platform/std/fire_audio_*.hpp`), so the same code runs in the virtual machine (through the package ABI, docs/PACKAGE_NATIVES.md) and in a native build. It needs `time`.

```
program        #import "audio"        Audio.Output, Audio.Sound, Audio.Board, Audio.Sim          (fire, package fire-audio)
bridge         fire_bridge_audio.hpp  handles, volume, waves, errors, the simulated device       (C++, shared by VM and native)
platform       plat::audio            open / write / queued / stop / close                       (Linux: PulseAudio or ALSA, Windows: winmm, ESP32: PWM, else: stub)
```

## Quick start

```
#import "audio"

var out = new Audio.Output()                       // the default device, 44100 Hz, mono
out.Beep(440, 200)                                 // a 440 Hz sine for 200 ms, returns when it has been played
out.Tone(523.25, 500, Audio.Wave.Square, 30)       // queue a tone (Hz, ms, wave, volume in percent); returns when it is queued
out.Drain()                                        // wait until everything queued has been played

var sound = Audio.Sound.FromWav(IO.File.ReadAllBytes("door.wav"))     // needs #import "io"
var player = sound.Open()                          // an output that fits the sound (its rate and its channels)
player.Play(sound)
player.Drain()

var speaker = new Audio.Output(25, 8000)           // ESP32: PWM on GPIO 25 at 8000 Hz
```

## The device

`new Audio.Output(device = "default", rate = 44100, channels = 1)`:

| Device | Where | Meaning |
|---|---|---|
| `"default"` | Linux | PulseAudio (or PipeWire, which speaks its protocol) when a server answers, ALSA otherwise |
| `"pulse"`, `"pulse:<sink>"` | Linux | PulseAudio, optionally a named sink |
| `"alsa"`, `"alsa:<pcm>"` | Linux | ALSA, the default PCM or a named one (`"alsa:plughw:1,0"`) |
| `"default"`, a device name, or its number | Windows | the default device of the user (the wave mapper), or one that `Audio.Board.Devices()` lists |
| `"pwm:<gpio>"`, or just the number | ESP32 | PWM on that GPIO pin: `new Audio.Output(25)` is `new Audio.Output("pwm:25")` |
| `"sim"` | everywhere | the simulated device (see below) |

`Audio.Board.Devices()` lists the names ("sim" first), `Audio.Board.Available()` says whether the machine has a sound output at all. An unknown name is `Audio.NotFoundException`.

**Linux** needs nothing to *build* a program: PulseAudio (`libpulse-simple.so.0`) and ALSA (`libasound.so.2`) are loaded with `dlopen` when they are first needed, so one binary runs on a machine with either, both or none (then
`Audio.UnsupportedException`). The package asks for `-ldl` only. **Windows** links `winmm` (part of Windows). **macOS** has no backend yet (only `"sim"`).

**ESP32 (PWM):** a LEDC channel with a 78 kHz carrier and 8 bit resolution drives the pin; a timer interrupt sets the duty cycle once per sample (rates 2000..48000 Hz; 8000..22050 are plenty for beeps, speech and
melodies). Connect the pin through a small RC low-pass filter (for example 1 kOhm + 100 nF) to an amplifier, or drive a piezo; a speaker needs a transistor or an amplifier - never connect it to the pin directly. One output
at a time. A stereo input is mixed down. Add the components `esp_driver_ledc` and `esp_driver_gptimer` (IDF 5.x) or `driver` to the project, and set `CONFIG_LEDC_CTRL_FUNC_IN_IRAM` and `CONFIG_GPTIMER_ISR_IRAM_SAFE`.
(Written against the IDF 5.x API and checked for syntax against a stand-in of its headers; not built with ESP-IDF or tried on a board yet - like the other ESP32 parts.)

## Samples and waiting

Samples are **16 bit signed, little endian, interleaved** (a *frame* is one sample per channel); the rate is any rate the device takes (PulseAudio, ALSA's `plug` and Windows convert, the ESP32 plays it as it is).

* `Write(buffer, offset = 0, count = -1)` plays the bytes. It returns when the device has **taken** them all - it sleeps (5 ms at a time) while the device's buffer is full, so a program can always be aborted and nothing blocks inside
  the native code. `Offer(buffer, offset, count)` takes only what fits and returns how many bytes it took (0: full).
* `Queued` is the number of bytes taken and not yet played, `Playing` is `Queued > 0`. `Drain(timeoutMs = -1)` waits until the queue is empty (true) or the time ran out (false). `Stop()` throws the queue away.
* `Close()` plays what was taken to its end, then closes (call `Stop()` first to cut it off). The destructor closes, too - but **call `Drain()` before the program ends** if the last sound must be heard.
* `Volume` (0..100) scales the samples written afterwards.
* `Tone(frequency, milliseconds, wave = Audio.Wave.Sine, volume = 100)` generates and queues a tone (the frequency may be a float); the waves are `Sine`, `Square`, `Triangle`, `Saw` and `Noise`. The generation is native (a
  fast loop in C++, the same in the VM and in a native build). `Beep(frequency = 440, milliseconds = 200, volume = 100)` is a tone that is played to its end.

`Audio.Sound` is PCM in memory: `new Audio.Sound(rate, channels, buffer, offset = 0, length = -1)`; `Audio.Sound.FromWav(bytes)` reads PCM WAV files (8 or 16 bit, mono or stereo, any rate; other formats are
`Audio.UnsupportedException`); `Audio.Sound.Tone(frequency, milliseconds, wave, volume, rate = 22050)` generates one. `sound.Open(device)` opens an output with the rate and channels of the sound, and `output.Play(sound)` queues it
(a sound whose rate or channels differ from the output's is an error - open the output with `sound.Open()`).

## Errors

`Audio.AudioException` with a `code` (1 invalid argument, 2 closed/invalid handle, 7 other) and its subclasses `NotFoundException` (3), `BusyException` (4: the device is used by somebody else), `PermissionException` (5),
`UnsupportedException` (6: no sound system, a rate or channel count the device does not take). A failure of the sound system reports its own text ("ALSA 'default': No such file or directory", "PulseAudio: Connection refused").

## The simulated device

Every platform has the device `"sim"`: it records what is played. `Audio.Sim.Data()` is the bytes (a buffer; at most 16 MB are kept), `Played()` their number, `Rate()` and `Channels()` those of the output opened last. `Hold()`
makes it slow: it takes only 4096 bytes and keeps them (`Waiting()`), as a device does that plays in real time - `Hold(false)` plays them. `Reset()` forgets everything. A program written against `"sim"` runs on the board
with the real device name; the test suite uses it (VM and native build must agree).

## How the platform layers work

`plat::audio` (`native/platform/std/fire_audio_none.hpp` describes the interface): `open(name, rate, channels)`, `write` (takes what fits, never blocks), `queued`, `stop`, `close`. The bridge keeps the handle table, applies
the volume and generates the waves. Linux: ALSA runs non-blocking (`snd_pcm_writei`, `-EAGAIN` means full, an underrun is recovered); PulseAudio's simple API only blocks, so each output has a thread that plays a ring buffer
(all calls into PulseAudio are made by that thread, with a 200 ms buffer on the server and playback starting at once). Windows: blocks of 50 ms handed to `waveOutWrite`, free again when the driver sets the DONE flag.
