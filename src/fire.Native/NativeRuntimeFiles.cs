using System.Reflection;
using System.Text.RegularExpressions;

namespace fire.Native
{
    /// <summary>The C++ files the generated code needs (embedded in this assembly): the runtime <c>fire_rt.hpp</c> and the platform package it was generated for.</summary>
    public static class NativeRuntimeFiles
    {
        private static readonly Assembly Asm = typeof(NativeRuntimeFiles).Assembly;

        /// <summary>Writes <c>fire_rt.hpp</c> and - with <paramref name="platform"/> - the platform package <c>platform/&lt;platform&gt;/</c> (and the packages it
        /// includes, like <c>std</c> under <c>posix</c>) into <paramref name="directory"/>; existing files are replaced. Without a platform name every package is written.</summary>
        public static void WriteTo(string directory, string? platform = null)
        {
            Directory.CreateDirectory(directory);
            WriteResource("fire_rt.hpp", directory);
            foreach (string name in Asm.GetManifestResourceNames().Where(n => n.StartsWith("bridges/", StringComparison.Ordinal) || n.StartsWith("abi/", StringComparison.Ordinal)))
                WriteResource(name, directory);
            var wanted = platform == null ? AllPackages() : PackageClosure(platform);
            foreach (string package in wanted)
                foreach (string name in Asm.GetManifestResourceNames().Where(n => n.StartsWith($"platform/{package}/", StringComparison.Ordinal)))
                    WriteResource(name, directory);
        }

        /// <summary>Writes the FreeRTOS simulator (<c>FreeRTOS.h</c>, <c>task.h</c>, <c>semphr.h</c>, also under <c>freertos/</c>) into <paramref name="directory"/>: tasks and semaphores on
        /// top of pthreads, so that a FreeRTOS build can be tried on a PC.</summary>
        public static void WriteSimulatorTo(string directory)
        {
            Directory.CreateDirectory(directory);
            foreach (string name in Asm.GetManifestResourceNames().Where(n => n.StartsWith("sim/", StringComparison.Ordinal)))
            {
                string target = Path.Combine(directory, name.Substring("sim/".Length).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var stream = Asm.GetManifestResourceStream(name)!;
                using var file = File.Create(target);
                stream.CopyTo(file);
            }
        }

        /// <summary>The names of the platform packages the runtime carries.</summary>
        public static IReadOnlyList<string> AllPackages() =>
            Asm.GetManifestResourceNames().Where(n => n.StartsWith("platform/", StringComparison.Ordinal))
                .Select(n => n.Split('/')[1]).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

        /// <summary>The package and the packages its header includes (`#include "../std/..."`), recursively.</summary>
        private static IEnumerable<string> PackageClosure(string platform)
        {
            var result = new List<string>();
            var work = new Stack<string>();
            work.Push(platform);
            while (work.Count > 0)
            {
                string package = work.Pop();
                if (result.Contains(package)) continue;
                var names = Asm.GetManifestResourceNames().Where(n => n.StartsWith($"platform/{package}/", StringComparison.Ordinal)).ToList();
                if (names.Count == 0) continue;   // a package of your own, supplied by the target configuration
                result.Add(package);
                foreach (string name in names)
                {
                    using var reader = new StreamReader(Asm.GetManifestResourceStream(name)!);
                    foreach (Match m in Regex.Matches(reader.ReadToEnd(), @"#include\s+""\.\./([A-Za-z0-9_\-]+)/"))
                        work.Push(m.Groups[1].Value);
                }
            }
            return result;
        }

        /// <summary>The resource names (`bridges/...`, `platform/...`) that start with <paramref name="prefix"/>.</summary>
        public static IReadOnlyList<string> ResourceNames(string prefix) =>
            Asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).ToList();

        /// <summary>The text of an embedded runtime file (e.g. `bridges/fire_bridge_time.hpp`), or null.</summary>
        public static string? ReadText(string resourceName)
        {
            using var stream = Asm.GetManifestResourceStream(resourceName);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static void WriteResource(string name, string directory)
        {
            string target = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var stream = Asm.GetManifestResourceStream(name)!;
            using var file = File.Create(target);
            stream.CopyTo(file);
        }
    }
}
