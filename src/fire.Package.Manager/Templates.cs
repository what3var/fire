namespace fire.Package.Manager
{
    /// <summary>`ember create` and `ember blank`: the file that `ember forge` reads.</summary>
    public static class Templates
    {
        /// <summary>A description with example values (and the example files it names, next to it, where they do not exist yet): `ember forge` works on it as it is.</summary>
        public static PackageManifest Example(string forgeJsonPath, bool writeFiles)
        {
            string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(forgeJsonPath))!, "mathkit");
            string prelude = Path.Combine(dir, "mathkit.fire");
            string native = Path.Combine(dir, "mathkit.hpp");
            if (writeFiles)
            {
                Directory.CreateDirectory(dir);
                if (!File.Exists(prelude)) File.WriteAllText(prelude, ExamplePrelude);
                if (!File.Exists(native)) File.WriteAllText(native, ExampleNative);
            }
            return new PackageManifest
            {
                Name = "mathkit",
                Version = "1.0.0",
                Author = "Your Name",
                Description = "Example: a class in fire and a C++ native (hypot) for the native backend.",
                License = "MIT",
                Homepage = "https://example.com/mathkit",
                Imports =
                {
                    new PackageImport
                    {
                        Name = "mathkit",
                        Prelude = prelude,
                        Native = new PackageNative
                        {
                            Sources = { native },
                            Functions = { new PackageNativeFunction { Name = "__mathkit_hypot", Arguments = 2, Cpp = "mk_hypot" } },
                        },
                    },
                },
            };
        }

        /// <summary>All fields there, nothing in them.</summary>
        public static PackageManifest Blank() => new()
        {
            Name = "", Version = "", Author = "", Description = "", License = "", Homepage = "",
            Imports = { new PackageImport { Name = "", Prelude = "", Native = new PackageNative { Sources = { "" }, Platforms = { }, Functions = { new PackageNativeFunction { Name = "", Arguments = 0, Cpp = "" } } } } },
        };

        public const string ExamplePrelude = """
            // The prelude of the import "mathkit": fire source that is added to the program by `#import "mathkit"`.
            class MathKit {
                static Square(x) { return x * x }
                // __mathkit_hypot is a native written in C++ (mathkit.hpp, see docs/PACKAGE_NATIVES.md): native builds include it, the virtual machine runs it from a shared library.
                static Hypot(a, b) { return __mathkit_hypot(a, b) }
            }
            """;

        public const string ExampleNative = """
            // C++ source of the natives of "mathkit" (docs/PACKAGE_NATIVES.md). It is put into the generated file after the runtime (fire_rt.hpp), outside of any namespace, and built into a shared library for the virtual machine.
            #include <cmath>

            namespace fire {

            // Value mk_hypot(Value a, Value b): arguments and result are Values (Int(...), Float(...), toR(v) reads a number as a real).
            inline Value mk_hypot(Value a, Value b) { return Float((Real)std::hypot((double)toR(a), (double)toR(b))); }

            }  // namespace fire
            """;
    }
}
