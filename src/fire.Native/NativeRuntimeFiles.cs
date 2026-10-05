using System.Reflection;

namespace fire.Native
{
    /// <summary>The C++ runtime files the generated code needs (embedded in this assembly).</summary>
    public static class NativeRuntimeFiles
    {
        /// <summary>Writes <c>fire_rt.hpp</c> into <paramref name="directory"/> (existing file is replaced).</summary>
        public static void WriteTo(string directory)
        {
            Directory.CreateDirectory(directory);
            var asm = typeof(NativeRuntimeFiles).Assembly;
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".hpp")))
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                using var file = File.Create(Path.Combine(directory, name));
                stream.CopyTo(file);
            }
        }
    }
}
