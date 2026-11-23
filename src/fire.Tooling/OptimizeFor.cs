namespace fire.Runtime
{
    /// <summary>
    /// The direction a native build is optimised in (docs/spec/NATIVE_BACKEND.md "Optimierungsrichtung"): the flags of the C compiler and the choices of the generator and
    /// the runtime that trade one for the other (`FIRE_OPTIMIZE_SIZE`). The command line (`--optimize`), then the project (`settings.optimize`), then fire.native.json
    /// (`"optimize"`), then the target (`"optimize"` of a target; the 16-bit PC targets: size), else speed.
    /// </summary>
    public enum OptimizeFor : byte
    {
        Speed = 0,
        Size = 1,
    }

    public static class OptimizeFors
    {
        public static readonly IReadOnlyList<string> Names = new[] { "speed", "size" };

        /// <summary>A name (any case) or null.</summary>
        public static OptimizeFor? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
        {
            "speed" => OptimizeFor.Speed,
            "size" => OptimizeFor.Size,
            _ => null,
        };

        public static string Name(OptimizeFor value) => value == OptimizeFor.Size ? "size" : "speed";
    }
}
