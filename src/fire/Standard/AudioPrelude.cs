namespace fire.Standard
{
    /// <summary>
    /// `#import "audio"`: sound output (docs/AUDIO.md). The natives are C++ (native/bridges/fire_bridge_audio.hpp over plat::audio: PulseAudio or ALSA on Linux - whichever is there -, winmm on Windows,
    /// PWM on a GPIO pin of an ESP32); this fire source wraps them in classes in `namespace Audio`. It needs `time` (waiting for the sound to be played).
    ///
    ///   var out = new Audio.Output()                              // the default device, 44100 Hz, mono
    ///   out.Beep(440, 200)                                        // a 440 Hz sine for 200 ms, and wait until it has been played
    ///   out.Tone(523.25, 500, Audio.Wave.Square, 30)               // queue a tone: frequency (Hz), milliseconds, wave, volume in percent - returns at once
    ///   out.Drain()                                               // wait until everything queued has been played
    ///
    ///   var sound = Audio.Sound.FromWav(IO.File.ReadAllBytes("door.wav"))   // 16 or 8 bit PCM, mono or stereo, any rate
    ///   var player = sound.Open()                                 // an output that fits the sound (its rate and channels)
    ///   player.Play(sound)
    ///   player.Drain()
    ///
    ///   var speaker = new Audio.Output(25, 8000)                  // an ESP32: PWM on GPIO 25 (the same as "pwm:25"), 8000 Hz
    ///
    /// The device is "default" (PulseAudio when a server answers, ALSA otherwise; on Windows the default device), a name that `Audio.Board.Devices()` lists ("pulse", "alsa", "alsa:plughw:1,0", the
    /// name of a Windows device), "pwm:<gpio>" (ESP32) or "sim". A number is a GPIO pin: `new Audio.Output(25)` is `new Audio.Output("pwm:25")`.
    /// Samples are 16 bit signed, little endian, interleaved (a frame is one sample per channel). Writing never blocks the program for good: `Write` waits for room by sleeping, so the program stays
    /// abortable; `Offer` takes only what fits. Every platform has the simulated device "sim" that records what is played: `Audio.Sim.Data()`. A program tried on the PC with "sim" runs on the board.
    /// Errors are exceptions: `Audio.AudioException` (with a `code`) and its subclasses.
    /// </summary>
    public static class AudioPrelude
    {
        public const string Source = """
            namespace Audio {
                enum Wave { Sine, Square, Triangle, Saw, Noise }

                class AudioException : Exception {
                    int code

                    construct(string message, int code = 7) : base(message) {
                        this.code = code
                    }
                }

                // No such device (code 3).
                class NotFoundException : AudioException {
                    construct(string message) : base(message, 3) { }
                }

                // The device is used by somebody else (code 4).
                class BusyException : AudioException {
                    construct(string message) : base(message, 4) { }
                }

                // No permission to use the device (code 5).
                class PermissionException : AudioException {
                    construct(string message) : base(message, 5) { }
                }

                // This platform cannot do it (code 6): no sound system, a rate or a format the device does not take.
                class UnsupportedException : AudioException {
                    construct(string message) : base(message, 6) { }
                }

                class Errors {
                    // Throws the exception that fits the last error of a native function.
                    static Throw() {
                        var code = __AudioLastError()
                        var message = __AudioLastErrorMessage()
                        if (code == 3) { throw new NotFoundException(message) }
                        if (code == 4) { throw new BusyException(message) }
                        if (code == 5) { throw new PermissionException(message) }
                        if (code == 6) { throw new UnsupportedException(message) }
                        throw new AudioException(message, code)
                    }

                    static int Handle(int handle) {
                        if (handle < 0) { Throw() }
                        return handle
                    }
                }

                // The sound devices of this machine.
                class Board {
                    // true if this machine has a sound output (the device "sim" is always there).
                    static bool Available() { return __AudioSupported() == 1 }

                    // The names of the devices: "sim" first, then the real ones ("default", "pulse", "alsa" on Linux, "default" and the names of the devices on Windows, "pwm" on an ESP32 - there with a pin: "pwm:25").
                    static Devices() {
                        var all = __AudioDevices()
                        if (all == undefined) { Audio.Errors.Throw() }
                        var list = new List()
                        for (var i = 0; i < all.length; i++) { list.Add(all[i]) }
                        return list
                    }
                }

                // A sound in memory: 16 bit samples (little endian, interleaved) in a buffer, from `Offset` on, `Length` bytes.
                class Sound {
                    int rate
                    int channels
                    data
                    int offset
                    int length

                    construct(int rate, int channels, data, int offset = 0, int length = -1) {
                        if (rate < 1000 || rate > 192000) { throw new AudioException("The rate must be between 1000 and 192000 Hz.", 1) }
                        if (channels < 1 || channels > 2) { throw new AudioException("A sound has one (mono) or two (stereo) channels.", 1) }
                        if (length < 0) { length = data.length - offset }
                        if (offset < 0 || offset + length > data.length) { throw new AudioException("offset/length are outside of the buffer.", 1) }
                        this.rate = rate
                        this.channels = channels
                        this.data = data
                        this.offset = offset
                        this.length = length - (length % (channels * 2))
                    }

                    int Rate { get { return this.rate } }
                    int Channels { get { return this.channels } }
                    // The samples (a buffer of bytes), starting at Offset.
                    Data { get { return this.data } }
                    int Offset { get { return this.offset } }
                    // The number of bytes of the sound.
                    int Length { get { return this.length } }
                    // The number of frames (a frame is one sample per channel).
                    int Frames { get { return this.length / (this.channels * 2) } }
                    // How long it plays, in milliseconds.
                    int Milliseconds { get { return this.Frames * 1000 / this.rate } }

                    // An output that fits the sound (its rate and channels) on the device.
                    Open(device = "default") {
                        return new Output(device, this.rate, this.channels)
                    }

                    // A generated tone: the frequency in Hz (a float is fine), the length in milliseconds, the wave and the volume in percent.
                    static Tone(frequency, int milliseconds, int wave = 0, int volume = 100, int rate = 22050) {
                        var frames = rate * milliseconds / 1000
                        var data = new byte[frames * 2]
                        if (__AudioWave(data, 0, frames, wave, frequency, volume, rate, 1, 0) < 0) { Audio.Errors.Throw() }
                        return new Sound(rate, 1, data)
                    }

                    // Reads a WAV file (the bytes of it): PCM with 16 or 8 bits, mono or stereo, any rate.
                    static FromWav(bytes) {
                        if (bytes.length < 44 || bytes[0] != 82 || bytes[1] != 73 || bytes[2] != 70 || bytes[3] != 70 || bytes[8] != 87 || bytes[9] != 65 || bytes[10] != 86 || bytes[11] != 69) {
                            throw new AudioException("This is not a WAV file (RIFF/WAVE expected).", 1)
                        }
                        var rate = 0
                        var channels = 0
                        var bits = 0
                        var format = 0
                        var dataStart = -1
                        var dataLength = 0
                        var pos = 12
                        while (pos + 8 <= bytes.length && dataStart < 0) {
                            var size = bytes[pos + 4] + (bytes[pos + 5] << 8) + (bytes[pos + 6] << 16) + (bytes[pos + 7] << 24)
                            var isFmt = bytes[pos] == 102 && bytes[pos + 1] == 109 && bytes[pos + 2] == 116 && bytes[pos + 3] == 32
                            var isData = bytes[pos] == 100 && bytes[pos + 1] == 97 && bytes[pos + 2] == 116 && bytes[pos + 3] == 97
                            if (isFmt && size >= 16 && pos + 8 + 16 <= bytes.length) {
                                format = bytes[pos + 8] + (bytes[pos + 9] << 8)
                                channels = bytes[pos + 10] + (bytes[pos + 11] << 8)
                                rate = bytes[pos + 12] + (bytes[pos + 13] << 8) + (bytes[pos + 14] << 16) + (bytes[pos + 15] << 24)
                                bits = bytes[pos + 22] + (bytes[pos + 23] << 8)
                            }
                            if (isData) {
                                dataStart = pos + 8
                                dataLength = size
                                if (dataStart + dataLength > bytes.length) { dataLength = bytes.length - dataStart }
                            }
                            pos = pos + 8 + size + (size % 2)
                        }
                        if (rate == 0) { throw new AudioException("The WAV file has no format chunk.", 1) }
                        if (dataStart < 0) { throw new AudioException("The WAV file has no data chunk.", 1) }
                        if (format != 1 && format != 65534) { throw new UnsupportedException("Only PCM WAV files are supported (format " + format + ").") }
                        if (channels < 1 || channels > 2) { throw new UnsupportedException("A WAV file with " + channels + " channels is not supported (mono or stereo).") }
                        if (bits == 16) { return new Sound(rate, channels, bytes, dataStart, dataLength) }
                        if (bits == 8) {
                            // unsigned 8 bit -> signed 16 bit
                            var n = dataLength
                            var converted = new byte[n * 2]
                            for (var i = 0; i < n; i++) {
                                var s = (bytes[dataStart + i] - 128) * 256
                                converted[i * 2] = s & 255
                                converted[i * 2 + 1] = (s >> 8) & 255
                            }
                            return new Sound(rate, channels, converted)
                        }
                        throw new UnsupportedException("A WAV file with " + bits + " bits per sample is not supported (8 or 16).")
                    }
                }

                // One output: a device that plays 16 bit samples.
                class Output {
                    int handle
                    bool closed
                    string name
                    int rate
                    int channels
                    int volume

                    // `device`: a name ("default", "pulse", "alsa", "sim", ...) or a number - the GPIO pin of a PWM speaker on an ESP32; `rate` in Hz; `channels` 1 (mono) or 2 (stereo).
                    construct(device = "default", int rate = 44100, int channels = 1) {
                        this.handle = -1
                        this.closed = false
                        this.rate = rate
                        this.channels = channels
                        this.volume = 100
                        var text = "" + device
                        if (text != "" && text.Length > 0) {
                            var digits = true
                            for (var i = 0; i < text.Length; i++) {
                                var c = text.CharAt(i).ToInt()
                                if (c < 48 || c > 57) { digits = false }
                            }
                            if (digits) { text = "pwm:" + text }
                        }
                        this.name = text
                        this.handle = Audio.Errors.Handle(__AudioOpen(text, rate, channels))
                    }

                    destruct() { try { this.Close() } catch (AudioException e) { } }

                    string Name { get { return this.name } }
                    int Rate { get { return this.rate } }
                    int Channels { get { return this.channels } }
                    bool IsClosed { get { return this.closed } }

                    // The volume in percent (0 .. 100) of what is written from now on.
                    int Volume {
                        get { return this.volume }
                        set {
                            this.Check()
                            if (!__AudioSetVolume(this.handle, value)) { Audio.Errors.Throw() }
                            this.volume = value
                        }
                    }

                    // The bytes that were taken and have not been played yet.
                    int Queued {
                        get {
                            this.Check()
                            var q = __AudioQueued(this.handle)
                            if (q < 0) { Audio.Errors.Throw() }
                            return q
                        }
                    }

                    // true while something is being played.
                    bool Playing { get { return this.Queued > 0 } }

                    Check() {
                        if (this.closed) { throw new AudioException("The output is closed.", 2) }
                    }

                    // Takes as much of the bytes as fits into the buffer of the device (whole frames) and returns how many it took (0: full) - without waiting.
                    int Offer(buffer, int offset = 0, int count = -1) {
                        this.Check()
                        if (count < 0) { count = buffer.length - offset }
                        var n = __AudioWrite(this.handle, buffer, offset, count)
                        if (n < 0) { Audio.Errors.Throw() }
                        return n
                    }

                    // Plays `count` bytes of the buffer (all of it from `offset` on without a count): returns when the device has taken them all (that is, when the last part is queued, not played).
                    Write(buffer, int offset = 0, int count = -1) {
                        this.Check()
                        if (count < 0) { count = buffer.length - offset }
                        while (count > 0) {
                            var n = __AudioWrite(this.handle, buffer, offset, count)
                            if (n < 0) { Audio.Errors.Throw() }
                            if (n == 0) { Sleep(5) }
                            offset = offset + n
                            count = count - n
                        }
                    }

                    // Plays a sound (queued behind what is playing). Its rate and channels must be those of the output: open it with `sound.Open()`.
                    Play(sound) {
                        if (sound.Rate != this.rate || sound.Channels != this.channels) {
                            throw new AudioException("The sound has " + sound.Rate + " Hz and " + sound.Channels + " channel(s), the output " + this.rate + " Hz and " + this.channels + ": open the output with sound.Open().", 1)
                        }
                        this.Write(sound.Data, sound.Offset, sound.Length)
                    }

                    // Queues a tone and returns: the frequency in Hz (a float is fine), the length in milliseconds, the wave (Audio.Wave.Sine, Square, Triangle, Saw, Noise) and the volume in percent.
                    Tone(frequency, int milliseconds, int wave = 0, int volume = 100) {
                        this.Check()
                        var left = this.rate * milliseconds / 1000
                        var chunk = 2048
                        if (left < chunk) { chunk = left }
                        if (chunk <= 0) { return }
                        var frameBytes = this.channels * 2
                        var buffer = new byte[chunk * frameBytes]
                        var phase = 0
                        while (left > 0) {
                            var frames = chunk
                            if (left < frames) { frames = left }
                            phase = __AudioWave(buffer, 0, frames, wave, frequency, volume, this.rate, this.channels, phase)
                            if (phase < 0) { Audio.Errors.Throw() }
                            this.Write(buffer, 0, frames * frameBytes)
                            left = left - frames
                        }
                    }

                    // A tone that is played to its end before Beep returns (440 Hz, 200 ms without arguments).
                    Beep(frequency = 440, int milliseconds = 200, int volume = 100) {
                        this.Tone(frequency, milliseconds, 0, volume)
                        this.Drain()
                    }

                    // Waits until everything that was taken has been played; `timeoutMs` (-1: no limit) gives up earlier. Returns true if the queue is empty.
                    bool Drain(int timeoutMs = -1) {
                        this.Check()
                        var waited = 0
                        while (true) {
                            var q = __AudioQueued(this.handle)
                            if (q < 0) { Audio.Errors.Throw() }
                            if (q == 0) { return true }
                            if (timeoutMs >= 0 && waited >= timeoutMs) { return false }
                            var ms = q * 1000 / (this.rate * this.channels * 2) / 4
                            if (ms < 2) { ms = 2 }
                            if (ms > 50) { ms = 50 }
                            Sleep(ms)
                            waited = waited + ms
                        }
                    }

                    // Throws away what has not been played yet (the output stays open).
                    Stop() {
                        this.Check()
                        if (!__AudioStop(this.handle)) { Audio.Errors.Throw() }
                    }

                    // Closes the output; what was taken is played to its end first (call Stop() before to cut it off).
                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.handle >= 0) { __AudioClose(this.handle) }
                    }
                }

                // The simulated device "sim" from the outside: what was played to it (for tests, and to try a program without sound).
                class Sim {
                    // Forgets what was played; the device plays again (it is not held).
                    static Reset() { __AudioSimReset() }

                    // Holds the device (true): it takes only 4096 bytes and keeps them, as a slow device does; releases it (false): what it kept is played.
                    static Hold(bool hold = true) { __AudioSimHold(hold) }

                    // The number of bytes that were played.
                    static int Played() { return __AudioSimInfo(0) }
                    // The rate and the channels of the output that was opened last.
                    static int Rate() { return __AudioSimInfo(1) }
                    static int Channels() { return __AudioSimInfo(2) }
                    // The bytes that wait while the device is held.
                    static int Waiting() { return __AudioSimInfo(3) }
                    // The bytes that were played, as a buffer.
                    static Data() { return __AudioSimData() }
                }
            }
            """;
    }
}
