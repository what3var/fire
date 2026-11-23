namespace fire.Tooling
{
    /// <summary>The files of the runtime as far as the dialogs of spark need them: the platform packages (`native/platform/&lt;name&gt;`) a target can name. forge has the
    /// runtime (native/ next to it - the folder of `forge package-dist` -, or native/ of the repository a forge built from its sources reads).</summary>
    public static class NativeRuntimeFiles
    {
        /// <summary>The folder native/ forge reads (null: not found).</summary>
        public static string? NativeRoot()
        {
            if (Forge.Environment().TryGetValue("FIRE_NATIVE_ROOT", out var root)) return root;
            if (Environment.GetEnvironmentVariable("FIRE_NATIVE_ROOT") is { Length: > 0 } env) return env;
            if (Forge.Locate(out _) is { } forge && Path.Combine(Path.GetDirectoryName(forge)!, "native") is var beside && Directory.Exists(beside)) return beside;
            string here = Path.Combine(AppContext.BaseDirectory, "native");
            return Directory.Exists(here) ? here : null;
        }

        /// <summary>The names of the platform packages the runtime carries.</summary>
        public static IReadOnlyList<string> AllPackages()
        {
            string? root = NativeRoot();
            if (root == null || !Directory.Exists(Path.Combine(root, "platform"))) return Array.Empty<string>();
            return Directory.GetDirectories(Path.Combine(root, "platform")).Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
    }
}
