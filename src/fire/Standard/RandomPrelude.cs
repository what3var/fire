namespace fire.Standard
{
    /// <summary>
    /// `#import "random"`: <c>PseudoRandom</c>, a pseudo random number generator written in fire (no natives, so it behaves the same in the virtual machine and in a native build).
    /// The generator is xoshiro128** (four 32-bit words, computed in the 64-bit `int` with masks, so nothing ever overflows); a number as seed is spread over the four words
    /// by a 32-bit mixer. Without a seed the generator starts from the clock (`time`, which this import brings along), so every run differs. Same seed, same sequence - on every platform.
    ///
    ///   var rnd = new PseudoRandom()          // from the clock
    ///   rnd.Next()                            // 0 .. 2147483646
    ///   rnd.Next(6)                           // 0 .. 5
    ///   rnd.Next(1, 7)                        // 1 .. 6 (a die)
    ///   rnd.NextFloat()                       // 0.0 .. below 1.0
    ///   new PseudoRandom(42).Next()           // always the same
    ///
    /// Not for secrets: the sequence can be predicted from a few outputs.
    /// </summary>
    public static class RandomPrelude
    {
        public const string Source = """
            class RandomException : Exception {
                string message
                construct(string message) { this.message = message }
            }

            class PseudoRandom {
                int s0
                int s1
                int s2
                int s3

                // From the clock.
                construct() { this.Seed(DateTime.UtcNow().Ticks) }

                // From a number: the same number gives the same sequence.
                construct(int seed) { this.Seed(seed) }

                // 32-bit multiplication modulo 2^32 without leaving the 64-bit `int` (a and b are below 2^32).
                static Mul32(int a, int b) {
                    return (((a & 65535) * b) + ((((a >> 16) * b) & 65535) << 16)) & 4294967295
                }

                // The finalizer of MurmurHash3: spreads every bit of x over the whole word.
                static Mix(int x) {
                    x = (x # (x >> 16)) & 4294967295
                    x = PseudoRandom.Mul32(x, 2246822507)
                    x = (x # (x >> 13)) & 4294967295
                    x = PseudoRandom.Mul32(x, 3266489909)
                    return (x # (x >> 16)) & 4294967295
                }

                // Starts the sequence again from `seed`.
                Seed(int seed) {
                    var lo = seed & 4294967295
                    var hi = (seed >> 32) & 4294967295
                    this.s0 = PseudoRandom.Mix((lo + 2654435769) & 4294967295)
                    this.s1 = PseudoRandom.Mix((hi + this.s0 + 2654435769) & 4294967295)
                    this.s2 = PseudoRandom.Mix(((lo # hi # this.s1) + 1013904242) & 4294967295)
                    this.s3 = PseudoRandom.Mix((this.s2 + this.s1 + 1640531527) & 4294967295)
                    if (this.s0 == 0 && this.s1 == 0 && this.s2 == 0 && this.s3 == 0) { this.s0 = 1 }
                }

                // The next 32 random bits as a number 0 .. 4294967295.
                NextUInt32() {
                    var m = (this.s1 * 5) & 4294967295
                    m = ((m << 7) | (m >> 25)) & 4294967295
                    var result = (m * 9) & 4294967295
                    var t = (this.s1 << 9) & 4294967295
                    this.s2 = this.s2 # this.s0
                    this.s3 = this.s3 # this.s1
                    this.s1 = this.s1 # this.s2
                    this.s0 = this.s0 # this.s3
                    this.s2 = this.s2 # t
                    this.s3 = ((this.s3 << 11) | (this.s3 >> 21)) & 4294967295
                    return result
                }

                // 0 .. 2147483646 (like Random.Next() of .NET).
                Next() {
                    var v = this.NextUInt32() >> 1
                    if (v == 2147483647) { return 0 }
                    return v
                }

                // 0 .. max - 1 (max must be above 0; a bigger max than 2^31 uses 62 random bits).
                Next(int max) {
                    if (max <= 0) { throw new RandomException("max must be above 0") }
                    if (max <= 4294967296) { return this.Bounded(max, 4294967296, false) }
                    return this.Bounded(max, 4611686018427387904, true)
                }

                // min .. max - 1.
                Next(int min, int max) {
                    if (max <= min) { throw new RandomException("max must be above min") }
                    return min + this.Next(max - min)
                }

                // An unbiased value below `max`: the few values at the top of the range that would make some results likelier are drawn again.
                Bounded(int max, int range, bool wide) {
                    var limit = range - (range % max)
                    while (true) {
                        var v = this.NextUInt32()
                        if (wide) { v = (v << 30) | (this.NextUInt32() >> 2) }
                        if (v < limit) { return v % max }
                    }
                }

                // A number in the whole range of `int` (63 bits and the sign).
                NextInt() {
                    var hi = this.NextUInt32()
                    var lo = this.NextUInt32()
                    var v = ((hi & 2147483647) << 32) | lo
                    if ((hi >> 31) == 1) { return ~v }
                    return v
                }

                // 0.0 .. below 1.0 with 53 random bits.
                NextFloat() {
                    var a = this.NextUInt32() >> 5
                    var b = this.NextUInt32() >> 6
                    return (a * 67108864 + b) / 9007199254740992.0
                }

                // true or false, each half of the time.
                NextBool() { return (this.NextUInt32() & 1) == 1 }

                // A random element of the list (undefined if it is empty).
                Pick(list) {
                    if (list.count == 0) { return undefined }
                    return list[this.Next(list.count)]
                }

                // Mixes the elements of the list in place (Fisher-Yates) and returns it.
                Shuffle(list) {
                    var i = list.count - 1
                    while (i > 0) {
                        var j = this.Next(i + 1)
                        var tmp = list[i]
                        list[i] = list[j]
                        list[j] = tmp
                        i = i - 1
                    }
                    return list
                }
            }
            """;
    }
}
