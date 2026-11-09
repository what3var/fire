namespace fire.Package.Manager
{
    /// <summary>The user-wide cache of the static libraries of native builds with linkage "static" (ROADMAP 9.2, docs/spec/NATIVE_BACKEND.md "Getrennte Übersetzung"):
    /// `fire/cache/native/&lt;target&gt;/&lt;key&gt;/` below the folder for application data of the user (LocalApplicationData: `~/.local/share` on Linux, `%LOCALAPPDATA%` on
    /// Windows, `~/Library/Application Support` on macOS); the environment variable FIRE_NATIVE_CACHE moves it. The key is the content hash of everything the C of a
    /// library depends on - a stale entry is never used, only left behind. `ember cache clean` empties it.</summary>
    public static class NativeLibraryCache
    {
        /// <summary>The file that says a library is complete (its interface, ROADMAP 9.7).</summary>
        public const string InterfaceExtension = ".fireiface";

        public static string Root =>
            Environment.GetEnvironmentVariable("FIRE_NATIVE_CACHE") is { Length: > 0 } dir ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "fire", "cache", "native");

        /// <summary>The libraries in the cache and the bytes they take.</summary>
        public static (int Libraries, long Bytes) Size()
        {
            string root = Root;
            if (!Directory.Exists(root)) return (0, 0);
            int count = 0;
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { bytes += new FileInfo(file).Length; } catch (IOException) { }
                if (file.EndsWith(".a", StringComparison.Ordinal) || file.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)) count++;
            }
            return (count, bytes);
        }

        /// <summary>Empties the cache; what it held.</summary>
        public static (int Libraries, long Bytes) Clean()
        {
            var size = Size();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return size;
        }
    }
}
