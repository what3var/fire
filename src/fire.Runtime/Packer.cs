using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace fire.Runtime
{
    /// <summary>
    /// Creates the executable file of a fire program - a single, self-contained file:
    ///
    ///   1. Start piece: the apphost (runtime.exe) together with fire.Runtime.dll and runtimeconfig.json as a .NET bundle
    ///      (see BundleWriter). That is all the .NET host needs to start, and deliberately tiny.
    ///   2. Payload behind it (see PayloadFile, individually Brotli-packed): the program itself, the core (fire.dll,
    ///      MemoryPack) and - only if the program needs them via `#import` - the respective bridge DLLs together with
    ///      dependencies and native libraries (see PackagePlan).
    ///
    /// At start the runtime reads its NativeImports and loads the DLLs as needed from its own file (see
    /// PayloadLoader); formerly Costura/Fody did that, but everything was always embedded then.
    ///
    /// The packer itself runs in the compiler/editor, whose folder contains the finished DLLs and the apphost.
    /// </summary>
    public class Packer
    {
        /// <summary>File name of the apphost next to the compiler (Windows: runtime.exe, otherwise without extension).</summary>
        private static string StubFileName => OperatingSystem.IsWindows() ? "runtime.exe" : "runtime";

        /// <summary>Packs `program` into a self-contained file `outName`.
        /// `customizeApphost`: called before bundling with the path of a copy of the apphost, in order to e.g. set icon and
        /// version info. That must happen BEFORE bundling, because changing the PE resources
        /// shifts the file and would thereby invalidate the header offset of the bundle.
        /// Returns the pack planning (which DLLs were included).</summary>
        public static PackagePlan PackProgram(LinkedProgram program, string outName, Action<string>? customizeApphost = null, string? baseDir = null)
        {
            baseDir ??= AppContext.BaseDirectory;

            var stubPath = Path.Combine(baseDir, StubFileName);
            var runtimeDll = Path.Combine(baseDir, "runtime.dll");
            var runtimeConfig = Path.Combine(baseDir, "runtime.runtimeconfig.json");
            foreach (var required in new[] { stubPath, runtimeDll, runtimeConfig })
                if (!File.Exists(required))
                    throw new FileNotFoundException($"Zum Packen fehlt '{Path.GetFileName(required)}' im Ordner des Compilers ({baseDir}).", required);

            var plan = PackagePlan.Create(program.NativeImports, baseDir, program.PackageLibraryFiles);
            if (plan.Unresolved.Count > 0)
                throw new InvalidOperationException("Dependencies required for packing are missing in the compiler folder: " + string.Join(", ", plan.Unresolved));

            // copy the apphost and adapt it if necessary (icon/version).
            var tempStub = outName + "." + Guid.NewGuid().ToString("N") + ".stub";
            try
            {
                File.Copy(stubPath, tempStub, true);
                customizeApphost?.Invoke(tempStub);
                var apphost = File.ReadAllBytes(tempStub);

                var bundle = new List<BundleWriter.BundleFile>
                {
                    new("runtime.dll", BundleWriter.FileType.Assembly, File.ReadAllBytes(runtimeDll)),
                    new("runtime.runtimeconfig.json", BundleWriter.FileType.RuntimeConfigJson, File.ReadAllBytes(runtimeConfig)),
                };
                BundleWriter.Write(apphost, bundle, outName);
            }
            finally
            {
                try { File.Delete(tempStub); } catch (IOException) { }
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(outName, File.GetUnixFileMode(outName) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);

            var items = new List<(PayloadKind, string, byte[])>
            {
                (PayloadKind.Program, "program", MemoryPack.MemoryPackSerializer.Serialize(program)),
            };
            foreach (var (name, path) in plan.Assemblies)
                items.Add((PayloadKind.Assembly, name, File.ReadAllBytes(path)));
            foreach (var (name, path) in plan.Natives)
                items.Add((PayloadKind.Native, name, File.ReadAllBytes(path)));

            // The program is small and changes with every build: pack it quickly. The DLLs are the same on every build:
            // pack them once with the highest level and afterwards take them from the cache.
            using (var fs = new FileStream(outName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                PayloadFile.Append(fs, items, (kind, data) =>
                    kind == PayloadKind.Program
                        ? PayloadFile.Compress(data, CompressionLevel.Optimal)
                        : CompressCached(data));

            return plan;
        }

        /// <summary>Packs `data` with the highest Brotli level (several seconds for a large DLL) and remembers the
        /// result under its SHA-256 checksum in a cache folder: every further build with the same DLL only reads
        /// the finished file. If the cache does not work (no write permission), it is packed quickly (`Optimal`) instead of losing seconds on every
        /// build.</summary>
        private static byte[] CompressCached(byte[] data)
        {
            string file;
            try
            {
                var dir = Path.Combine(Path.GetTempPath(), "fire-pack-cache");
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)) + ".br");
                if (File.Exists(file))
                    return File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return PayloadFile.Compress(data, CompressionLevel.Optimal);
            }

            var packed = PayloadFile.Compress(data, CompressionLevel.SmallestSize);
            try
            {
                var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, packed);
                File.Move(tmp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return packed;
        }

        /// <summary>Reads the program from a packed file (for tests/tools; the runtime itself uses the
        /// PayloadLoader). Requires that fire.dll and MemoryPack can be loaded.</summary>
        public static LinkedProgram? UnpackProgram(string fileName)
        {
            var reader = PayloadFile.Open(fileName);
            var entry = reader?.Find(PayloadKind.Program, "program");
            var bin = entry == null ? null : reader!.Read(entry);
            return bin == null ? null : Deserialize(bin);
        }

        /// <summary>Converts the program bytes from the payload back (a separate method, so that fire.dll/MemoryPack are only
        /// loaded on the call, not already when the packer is loaded).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static LinkedProgram? Deserialize(byte[] bin)
        {
            var program = MemoryPack.MemoryPackSerializer.Deserialize<LinkedProgram>(bin);
            if (program == null) return null;
            program.Program.RelinkAfterDeserialize();
            return program;
        }
    }
}
