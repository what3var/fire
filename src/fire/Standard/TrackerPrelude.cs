namespace fire.Standard
{
    /// <summary>
    /// `#import "tracker"`: a player for tracker modules (docs/TRACKER.md) - ProTracker `.mod` files with 4 to 32 channels, the music of the Amiga demos and games. A module carries its own recorded
    /// instruments (samples) and a score, so unlike MIDI it sounds the same on every machine. The replayer and the mixer are C++ (native/bridges/fire_bridge_tracker.hpp), in the VM as well as in a
    /// native build; this fire source wraps them in classes in `namespace Tracker`. It plays through the `audio` package and so needs it (and `time`).
    ///
    ///   var song = Tracker.Song.Load(IO.File.ReadAllBytes("tune.mod"))     // needs #import "io"; 44100 Hz
    ///   print(song.Title + ": " + song.Channels + " channels, " + song.Milliseconds / 1000 + " s")
    ///   var player = Tracker.Player.Open(song)                             // the default sound device
    ///   player.Play()                                                      // plays to the end (or until Stop())
    ///
    ///   // in a loop of its own, next to other work:
    ///   song.Loop = true
    ///   while (running) { player.Pump(); /* ... */ Sleep(10) }
    ///
    /// `song.Render(buffer, offset, frames)` mixes into a buffer instead (16 bit stereo, or mono), `song.ToSound()` renders the whole song into an `Audio.Sound`. `song.Seek(order, row)`, `Mute(channel)`,
    /// `Separation` (the Amiga's hard left/right panning is 100), `Interpolate`, `Gain`, `Order`/`Row`/`Speed`/`Tempo`, the names of the samples and the notes of the patterns are available for players with a display.
    /// Errors are exceptions: `Tracker.TrackerException` (with a `code`) and `Tracker.BadFormatException` for a file that is not a module.
    /// </summary>
    public static class TrackerPrelude
    {
        public const string Source = """
            namespace Tracker {
                class TrackerException : Exception {
                    int code

                    construct(string message, int code = 7) : base(message) {
                        this.code = code
                    }
                }

                // The file is not a module (code 8), or one of a kind that is not supported.
                class BadFormatException : TrackerException {
                    construct(string message) : base(message, 8) { }
                }

                // This kind of module is not supported (code 6): FLT8, ...
                class UnsupportedException : TrackerException {
                    construct(string message) : base(message, 6) { }
                }

                class Errors {
                    static Throw() {
                        var code = __TrackerLastError()
                        var message = __TrackerLastErrorMessage()
                        if (code == 8) { throw new BadFormatException(message) }
                        if (code == 6) { throw new UnsupportedException(message) }
                        throw new TrackerException(message, code)
                    }

                    static int Handle(int handle) {
                        if (handle < 0) { Throw() }
                        return handle
                    }
                }

                // Names of notes: Tracker.Note.Name(428) is "C-2" (the period of a note as the pattern stores it).
                class Note {
                    static Name(int period) {
                        if (period <= 0) { return "---" }
                        var periods = [856, 808, 762, 720, 678, 640, 604, 570, 538, 508, 480, 453, 428, 404, 381, 360, 339, 320, 302, 285, 269, 254, 240, 226, 214, 202, 190, 180, 170, 160, 151, 143, 135, 127, 120, 113]
                        var names = ["C-", "C#", "D-", "D#", "E-", "F-", "F#", "G-", "G#", "A-", "A#", "B-"]
                        var best = 0
                        var bestDistance = 100000
                        for (var i = 0; i < 36; i++) {
                            var d = period - periods[i]
                            if (d < 0) { d = -d }
                            if (d < bestDistance) { bestDistance = d; best = i }
                        }
                        return names[best % 12] + (1 + best / 12)
                    }
                }

                // A module being played: the score, the instruments and the state of the replayer.
                class Song {
                    int handle
                    bool closed

                    // `bytes`: the file; `rate` the sample rate of the sound it is mixed to; `offset` and `length` select part of the buffer.
                    construct(bytes, int rate = 44100, int offset = 0, int length = -1) {
                        this.closed = false
                        this.handle = -1
                        if (length < 0) { length = bytes.length - offset }
                        this.handle = Tracker.Errors.Handle(__TrackerLoad(bytes, offset, length, rate))
                    }

                    // Reads a module from the bytes of a file.
                    static Load(bytes, int rate = 44100) { return new Song(bytes, rate) }

                    destruct() { try { this.Close() } catch (TrackerException e) { } }

                    Check() {
                        if (this.closed) { throw new TrackerException("The song is closed.", 2) }
                    }

                    int Get(int what) {
                        this.Check()
                        var v = __TrackerGet(this.handle, what)
                        if (v < 0) { Tracker.Errors.Throw() }
                        return v
                    }

                    SetValue(int what, int value) {
                        this.Check()
                        if (!__TrackerSet(this.handle, what, value)) { Tracker.Errors.Throw() }
                    }

                    string Title {
                        get {
                            this.Check()
                            return __TrackerText(this.handle, 0, 0)
                        }
                    }
                    int Channels { get { return this.Get(0) } }
                    // The length of the song in patterns (the order list).
                    int Orders { get { return this.Get(1) } }
                    int Patterns { get { return this.Get(2) } }
                    // Where the song is: the position in the order list and the row (0..63) of its pattern.
                    int Order { get { return this.Get(3) } }
                    int Row { get { return this.Get(4) } }
                    // Ticks per row, and beats per minute (the speed of a tick).
                    int Speed { get { return this.Get(5) } }
                    int Tempo { get { return this.Get(6) } }
                    // true when the song has played to its end (a song with Loop never finishes).
                    bool Finished { get { return this.Get(7) == 1 } }
                    // How long the song plays, from the start to the end, in milliseconds (a dry run of the replayer).
                    int Milliseconds { get { return this.Get(8) } }
                    int Rate {
                        get { return this.Get(9) }
                        set { this.SetValue(5, value) }
                    }
                    // true: go back to the start at the end of the song (default false).
                    bool Loop {
                        get { return this.Get(10) == 1 }
                        set {
                            var v = 0
                            if (value) { v = 1 }
                            this.SetValue(0, v)
                        }
                    }
                    // How much the left and the right ear hear of each other's channels: 0 is mono, 100 the hard panning of the Amiga (channels 0 and 3 left, 1 and 2 right); default 50.
                    int Separation {
                        get { return this.Get(11) }
                        set { this.SetValue(1, value) }
                    }
                    // true (default): linear interpolation between the bytes of a sample; false: the rough sound of the Amiga.
                    bool Interpolate {
                        get { return this.Get(12) == 1 }
                        set {
                            var v = 0
                            if (value) { v = 1 }
                            this.SetValue(2, v)
                        }
                    }
                    // The volume of the whole mix in percent (0 .. 400, default 100).
                    int Gain {
                        get { return this.Get(13) }
                        set { this.SetValue(3, value) }
                    }

                    // Silences a channel (or lets it play again).
                    Mute(int channel, bool muted = true) {
                        var mask = this.Get(14)
                        var bit = 1 << channel
                        if (muted) { mask = mask | bit } else { mask = mask & (4294967295 # bit) }
                        this.SetValue(4, mask)
                    }

                    bool IsMuted(int channel) { return (this.Get(14) & (1 << channel)) != 0 }

                    // The name of an instrument (0 .. 30), its length in bytes, its volume (0 .. 64), whether it loops.
                    string SampleName(int index) {
                        this.Check()
                        return __TrackerText(this.handle, 1, index)
                    }
                    int SampleLength(int index) { return this.SampleInfo(index, 0) }
                    int SampleVolume(int index) { return this.SampleInfo(index, 2) }
                    int SampleFinetune(int index) { return this.SampleInfo(index, 1) }
                    bool SampleLoops(int index) { return this.SampleInfo(index, 4) > 0 }

                    int SampleInfo(int index, int what) {
                        this.Check()
                        var v = __TrackerSampleInfo(this.handle, index, what)
                        if (v < 0) { Tracker.Errors.Throw() }
                        return v
                    }

                    // The score: what is written at `order`, `row` for `channel`: the sample number (1 .. 31, 0: none), the note's period (see Tracker.Note.Name), the effect (0 .. 15) and its parameter.
                    int CellSample(int order, int row, int channel) { return this.Cell(order, row, channel, 0) }
                    int CellPeriod(int order, int row, int channel) { return this.Cell(order, row, channel, 1) }
                    int CellEffect(int order, int row, int channel) { return this.Cell(order, row, channel, 2) }
                    int CellParam(int order, int row, int channel) { return this.Cell(order, row, channel, 3) }

                    // The pattern cell as text, like a tracker shows it: "C-2 01 C20".
                    string CellText(int order, int row, int channel) {
                        var sample = this.CellSample(order, row, channel)
                        var effect = this.CellEffect(order, row, channel)
                        var param = this.CellParam(order, row, channel)
                        var hex = "0123456789ABCDEF"
                        var s = ".."
                        if (sample > 0) { s = hex.Substring(sample / 16, 1) + hex.Substring(sample % 16, 1) }
                        var e = "..."
                        if (effect != 0 || param != 0) { e = hex.Substring(effect, 1) + hex.Substring(param / 16, 1) + hex.Substring(param % 16, 1) }
                        return Tracker.Note.Name(this.CellPeriod(order, row, channel)) + " " + s + " " + e
                    }

                    int Cell(int order, int row, int channel, int what) {
                        this.Check()
                        var v = __TrackerPatternCell(this.handle, order, row, channel, what)
                        if (v < 0) { Tracker.Errors.Throw() }
                        return v
                    }

                    // The state of a channel right now, for a display: its volume (0 .. 64), its period, its instrument, and whether a sample is sounding.
                    int ChannelVolume(int channel) { return this.Cell(0, 0, channel, 4) }
                    int ChannelPeriod(int channel) { return this.Cell(0, 0, channel, 5) }
                    int ChannelSample(int channel) { return this.Cell(0, 0, channel, 6) }
                    bool ChannelPlaying(int channel) { return this.Cell(0, 0, channel, 7) == 1 }

                    // Goes back to the start.
                    Restart() {
                        this.Check()
                        __TrackerRestart(this.handle)
                    }

                    // Goes to a position without sound (speed, tempo and volumes are those of the song there). Returns false if the song never gets there.
                    bool Seek(int order, int row = 0) {
                        this.Check()
                        var r = __TrackerSeek(this.handle, order, row)
                        return r
                    }

                    // Mixes `frames` frames into the buffer from `offset` on (16 bit samples, little endian: 2 bytes per frame and channel; `channels` 2 is stereo, 1 mono). Returns how many frames it mixed:
                    // fewer than asked only at the end of the song, 0 when it is over.
                    int Render(buffer, int offset, int frames, int channels = 2) {
                        this.Check()
                        var n = __TrackerRender(this.handle, buffer, offset, frames, channels)
                        if (n < 0) { Tracker.Errors.Throw() }
                        return n
                    }

                    // The whole song as a sound in memory (from the start, without looping; at most `maxMilliseconds`).
                    ToSound(int maxMilliseconds = 600000, int channels = 2) {
                        var wasLoop = this.Loop
                        this.Loop = false
                        this.Restart()
                        var ms = this.Milliseconds
                        if (ms > maxMilliseconds) { ms = maxMilliseconds }
                        var rate = this.Rate
                        var frames = rate * ms / 1000 + 4096
                        var data = new byte[frames * 2 * channels]
                        var done = 0
                        while (done < frames) {
                            var n = this.Render(data, done * 2 * channels, frames - done, channels)
                            if (n == 0) { break }
                            done = done + n
                        }
                        this.Loop = wasLoop
                        return new Audio.Sound(rate, channels, data, 0, done * 2 * channels)
                    }

                    Close() {
                        if (this.closed) { return }
                        this.closed = true
                        if (this.handle >= 0) { __TrackerClose(this.handle) }
                    }
                }

                // Plays a song on an audio output. `Pump()` keeps the output fed without waiting - call it from the loop of the program; `Play()` does that until the song is over.
                class Player {
                    var song
                    var output
                    var buffer
                    int filled
                    int sent
                    bool ended
                    bool stopped
                    int channels

                    construct(song, output) {
                        if (output.Rate != song.Rate) {
                            throw new TrackerException("The output plays " + output.Rate + " Hz, the song is mixed to " + song.Rate + ": open the output with Tracker.Player.Open(song).", 1)
                        }
                        this.song = song
                        this.output = output
                        this.channels = output.Channels
                        this.buffer = new byte[4096 * 2 * this.channels]
                        this.filled = 0
                        this.sent = 0
                        this.ended = false
                        this.stopped = false
                    }

                    // An output on `device` that fits the song (its rate, stereo), and the player on it.
                    static Open(song, device = "default") {
                        var output = new Audio.Output(device, song.Rate, 2)
                        return new Player(song, output)
                    }

                    Output { get { return this.output } }
                    // The song that is played.
                    Track { get { return this.song } }
                    bool Ended { get { return this.ended } }

                    // Mixes and hands over as much as the output takes at the moment, without waiting. Returns true while there is more to play (or to be heard).
                    bool Pump() {
                        if (this.stopped) { return false }
                        while (true) {
                            if (this.sent >= this.filled) {
                                if (this.ended) { break }
                                var n = this.song.Render(this.buffer, 0, 4096, this.channels)
                                if (n == 0) {
                                    this.ended = true
                                    break
                                }
                                this.filled = n * 2 * this.channels
                                this.sent = 0
                            }
                            var taken = this.output.Offer(this.buffer, this.sent, this.filled - this.sent)
                            if (taken == 0) { return true }
                            this.sent = this.sent + taken
                        }
                        return this.output.Queued > 0
                    }

                    // Plays to the end (a song with Loop = true never ends: stop it from another place with Stop()).
                    Play() {
                        while (this.Pump()) { Sleep(5) }
                    }

                    // Stops at once: what was mixed and not yet played is thrown away.
                    Stop() {
                        this.stopped = true
                        this.output.Stop()
                    }
                }
            }
            """;
    }
}
