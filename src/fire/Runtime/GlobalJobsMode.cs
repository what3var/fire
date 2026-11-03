namespace fire.Runtime
{
    /// <summary>
    /// When the main program processes its queue by itself - the `fire global` jobs, the sections of the fire threads and the host callbacks
    /// (docs/THREADING_DESIGN.md section 7). The same in the VM and in a native build. `sync globals` and the end of the program always process it.
    /// The values are the operand of `SetAutoSync` (0 is what `#nosync` always emitted).
    /// </summary>
    public enum GlobalJobsMode : byte
    {
        /// <summary>Only at `sync globals` (and at the end of the program): `#globaljobs sync`, the old `#nosync`.</summary>
        Sync = 0,
        /// <summary>The default: at every safe point - a loop's jump back, `Sleep` and the wait functions, `sync globals`, the end of the program.</summary>
        Safepoints = 1,
        /// <summary>Only while waiting - `Sleep` and the wait functions - and at `sync globals` (and at the end of the program).</summary>
        Sleep = 2,
    }

    public static class GlobalJobsModes
    {
        /// <summary>The names in the order of the setting's documentation.</summary>
        public static readonly IReadOnlyList<string> Names = new[] { "safepoints", "sleep", "sync" };

        /// <summary>A name (any case) or null.</summary>
        public static GlobalJobsMode? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
        {
            "safepoints" => GlobalJobsMode.Safepoints,
            "sleep" => GlobalJobsMode.Sleep,
            "sync" => GlobalJobsMode.Sync,
            _ => null,
        };

        public static string Name(GlobalJobsMode mode) => mode switch
        {
            GlobalJobsMode.Sleep => "sleep",
            GlobalJobsMode.Sync => "sync",
            _ => "safepoints",
        };
    }
}
