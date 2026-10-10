# Tracker modules (`#import "tracker"`, the package `fire-tracker`)

A player for **tracker modules**: ProTracker `.mod` files with 4 to 32 channels - the music of the Amiga demos, games and the whole scene that followed. A module carries its own recorded instruments (8 bit
samples) and a score; the player only mixes them. That is why it sounds the same on every machine, unlike MIDI, which leaves the voices to the sound card (and most sound cards and default synthesizers are
bad at it). Like every bridge it is a **package** (`ember`, docs/PACKAGES.md): a prelude in fire plus C++ in `native/bridges/fire_bridge_tracker.hpp`. It plays through the `audio` package
(docs/AUDIO.md) and so needs it, and `time`.

```
program        #import "tracker"        Tracker.Song, Tracker.Player, Tracker.Note                (fire, package fire-tracker)
bridge         fire_bridge_tracker.hpp  module reader, replayer, mixer (C++, the same code in the VM and in a native build)
audio          #import "audio"          Audio.Output / Audio.Sound: the sound device
```

The replayer and the mixer are native code: mixing a song is some thousand additions per output frame, which a virtual machine would not do in real time. The VM calls the C++ through the package
ABI (docs/PACKAGE_NATIVES.md), a native build compiles it into the program; **both render exactly the same samples** (all arithmetic is integer).

## Quick start

```
#import "io"
#import "tracker"

var song = Tracker.Song.Load(IO.File.ReadAllBytes("tune.mod"))     // 44100 Hz; Load(bytes, 48000) for another rate
print(song.Title + ": " + song.Channels + " channels, " + song.Milliseconds / 1000 + " s")
var player = Tracker.Player.Open(song)                             // the default sound device, stereo
player.Play()                                                      // returns when the song is over
```

`Play()` is `while (Pump()) { Sleep(5) }`. `Pump()` mixes and hands over as much as the sound device takes at that moment and returns at once, so a program with other things to do (a window, a game) calls it
from its own loop:

```
song.Loop = true                                // for a game: the music goes on
while (running) {
    player.Pump()                               // every few milliseconds is enough: the output holds half a second
    ...
    Sleep(10)
}
player.Stop()                                   // cuts the music
```

## What can be played

`.mod` files of the ProTracker family, recognised by the signature at byte 1080: `M.K.`, `M!K!`, `FLT4`, `4CHN` (4 channels), `6CHN`, `8CHN`, `CD81`, `OCTA` (8), `5CHN` ... `9CHN`, and `10CH` ... `32CH` / `10CN` ... `32CN`.
31 samples, 64 rows per pattern, up to 128 positions. **Not supported:** `FLT8` (Startrekker's 8 channel variant) - `Tracker.UnsupportedException`; the old 15 sample Soundtracker modules without a signature
(`Tracker.BadFormatException`); XM, S3M, IT (a later step could add readers that fill the same replayer). A truncated file plays as far as it goes (a sample that is cut off is played as far as it is there); a file
whose patterns are not all there is a `BadFormatException`.

The replayer does what ProTracker 2.3 does: the period table with the 16 finetunes; the effects `0` arpeggio, `1`/`2` slides, `3` tone portamento, `4` vibrato, `5`/`6` the combinations with a volume slide, `7` tremolo, `9`
sample offset, `A` volume slide, `B` position jump, `C` volume, `D` pattern break, `E1`/`E2` fine slides, `E3` glissando, `E4`/`E7` waveforms (sine, ramp, square), `E5` finetune, `E6` pattern loop, `E9` retrigger,
`EA`/`EB` fine volume slides, `EC` note cut, `ED` note delay, `EE` pattern delay, `F` speed (below 32) and tempo (32 and up). Not done: `E0` (the Amiga's filter), `E8`/`8xx` (panning - ProTracker has none), `EF` (funk repeat),
the "random" vibrato waveform. Channels 0 and 3 (and so on, in fours) are on the left, 1 and 2 on the right, like on the Amiga.

It was checked against libopenmpt (`openmpt123`): modules built with every effect and with random notes agree in length to a few milliseconds, in pitch and, for single notes, nearly sample by sample; the only deviations
found are the cases where players disagree anyway (a tone portamento with a note on a silent channel, a note delay longer than the row).

## The sound

* **Sample rate:** the one given to `Load` (default 44100). The Amiga played its samples at `3546895 / period` Hz; the mixer resamples them with **linear interpolation** (`song.Interpolate = false` gives the rough
  sound of the original hardware).
* **Stereo separation:** `song.Separation` from 0 (mono) to 100 (the hard left/right panning of the Amiga, tiring on headphones); the default is 50.
* **No clicks:** volume changes move over about 1.5 ms and a sample that is cut off by the next note fades out instead of stopping.
* **Gain:** `song.Gain` (percent, default 100). Four channels at full volume with full-scale samples fill the range; the mix is limited, not wrapped.
* `song.Mute(channel)` silences a channel (to listen to one instrument, or to turn off the drums).

## Reference

`Tracker.Song` (a module and the state of its replayer):

| Member | |
|---|---|
| `Song.Load(bytes, rate = 44100)`, `new Song(bytes, rate, offset, length)` | reads a module from the bytes of a file (or a part of a buffer) |
| `Title`, `Channels`, `Orders`, `Patterns` | what the file says (`Orders` is the length of the song in patterns) |
| `Order`, `Row`, `Speed`, `Tempo`, `Finished` | where the replayer is |
| `Milliseconds` | the length of the song (a dry run of the replayer, no sound is mixed) |
| `Rate`, `Loop`, `Separation`, `Interpolate`, `Gain` | settings; `Mute(channel, muted = true)`, `IsMuted(channel)` |
| `Render(buffer, offset, frames, channels = 2)` | mixes frames of 16 bit samples (little endian, interleaved) into a buffer; fewer than asked only at the end, 0 when it is over |
| `ToSound(maxMilliseconds = 600000, channels = 2)` | the whole song as an `Audio.Sound` (play it with `sound.Open().Play(sound)`, or save it as a WAV file) |
| `Restart()`, `Seek(order, row = 0)` | back to the start / to a position without sound (speed, tempo and volumes are those of the song there) |
| `SampleName(i)`, `SampleLength(i)`, `SampleVolume(i)`, `SampleFinetune(i)`, `SampleLoops(i)` | the instruments (0 .. 30) |
| `CellSample/CellPeriod/CellEffect/CellParam(order, row, channel)`, `CellText(order, row, channel)` | the score (`"C-2 01 C20"`); `Tracker.Note.Name(period)` |
| `ChannelVolume(c)`, `ChannelPeriod(c)`, `ChannelSample(c)`, `ChannelPlaying(c)` | what a channel does right now, for a display (VU meters, a pattern view that follows the song) |
| `Close()` | frees the module |

`Tracker.Player` (a song on an output): `Tracker.Player.Open(song, device = "default")` opens an `Audio.Output` that fits the song, `new Tracker.Player(song, output)` uses one you made (same rate; stereo or
mono); `Pump()`, `Play()`, `Stop()`, `Ended`, `Output`, `Track`.

Errors: `Tracker.TrackerException` (with a `code`), `Tracker.BadFormatException` (8: not a module), `Tracker.UnsupportedException` (6).

## On a small board

The ESP32 has the audio of docs/AUDIO.md (PWM on a pin) and enough computing power for four channels at 8000 to 16000 Hz: `Tracker.Song.Load(bytes, 11025)`, `Tracker.Player.Open(song, 25)` - the output takes
the pin number; the PWM is mono, so open the player on a mono output: `new Tracker.Player(song, new Audio.Output(25, 11025, 1))`. (Not tried on a board yet.)

## How it is built

`fire_bridge_tracker.hpp` has two parts. `fire::tracker::Song` is plain C++ that knows nothing of fire (`#define FIRE_TRACKER_ENGINE_ONLY` before including the header gives only that part, which is how it was compared
with other players): the module reader, the replayer (`processTick`: rows, effects) and the mixer (`render`: 32.32 fixed point sample positions, integer volumes). The rest are the natives (`__TrackerLoad`,
`__TrackerRender`, ...) with a table of handles, in the style of the other bridges. The ProTracker period table and the vibrato table are the standard ones.
