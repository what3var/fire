using System.Reflection;
using System.Text;
using fire.Native;
using fire.Package.Manager;
using fire.Runtime;

namespace fire.Compiler
{
    /// <summary>The measures of the builder's tools in C (docs/spec/FORGE_C.md, Phase D 2; native/forge/build/tools.c prints the same): `pkglibplan <import>`
    /// (what PackageLibrary would build for the VM: the file, the keys, the toolchain, the command, the files of the folder of the build; `graphics+windows`: the
    /// library of both), `bootimage <exe>` (WatcomToolchain.AfterLink of bios16 on a file of one's own: the image next to it), `peres <file.fire>` (the PE resources a
    /// native build for Windows writes - PeResourceEditor's, read through reflection) and `toolchaintest <toolchain>` (NativeBuilder.TestToolchain). Measuring only.</summary>
    public static class BuilderToolsDump
    {
        public static bool Handles(string[] args) => args.Length == 2 && args[0] is "pkglibplan" or "bootimage" or "peres" or "toolchaintest";

        public static int Run(string[] args)
        {
            var o = new StringBuilder();
            int result = 0;
            try
            {
                switch (args[0])
                {
                    case "pkglibplan": PackageLibraryPlan(args[1], o); break;
                    case "bootimage": o.Append(ToolchainDrivers.AfterLink(ToolchainDef.BuiltIn["watcom"], TargetProfile.Bios16, args[1]) ?? "none").Append('\n'); break;
                    case "peres": PeResources(args[1], o); break;
                    default: ToolchainTest(args[1], o); break;
                }
            }
            catch (Exception ex) when (CommandLineRunner.IsCompileError(ex))
            {
                o.Clear().Append("error\n").Append(CommandLineRunner.DescribeErrors(ex).Replace("\r\n", "\n")).Append('\n');
                result = 1;
            }
            var bytes = new UTF8Encoding(false).GetBytes(o.ToString());
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            return result;
        }

        private static string Sha(byte[] data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

        private static void PackageLibraryPlan(string name, StringBuilder o)
        {
            bool gfx = name == "graphics+windows";
            var import = PackageStore.Default.FindImport(gfx ? "graphics" : name);
            var windows = gfx ? PackageStore.Default.FindImport("windows") : null;
            if (import == null || (gfx && windows == null)) { o.Append($"error: the import '{(gfx ? "graphics" : name)}' is not installed\n"); return; }
            if (!gfx && import.Import.Native == null) { o.Append($"error: The import '{import.Name}' has no natives.\n"); return; }
            string file = gfx ? PackageLibrary.GraphicsFileName : PackageLibrary.FileNameFor(import);
            bool isC = gfx || import.Import.Native!.IsC;
            o.Append("rid ").Append(PackageLibrary.Rid).Append('\n').Append("file ").Append(file).Append('\n');
            if (!gfx)
            {
                var newest = (DateTime)typeof(PackageLibrary).GetMethod("NewestSource", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { import })!;
                o.Append("supported ").Append(import.Import.Native!.SupportsAny(PackageLibrary.HostPlatformKeys) ? "yes" : "no").Append('\n');
                o.Append("newest ").Append(newest.Ticks).Append('\n');
                o.Append("hash ").Append(StandardSources.NativeHashOf(import)).Append('\n');
            }
            else o.Append("hash ").Append(PackageLibrary.GraphicsSourceHash(import, windows!)).Append('\n');
            string wrapper = gfx ? PackageLibrary.GenerateGraphicsWrapper(import, windows!) : isC ? PackageLibrary.GenerateCWrapper(import) : PackageLibrary.GenerateWrapper(import);
            var target = TargetProfile.Host;
            o.Append("fingerprint ").Append(NativeRuntimeFiles.Fingerprint(target.Native.Platform)).Append('\n');
            o.Append("runtime ").Append(gfx ? PackageLibrary.GraphicsRuntimeKey(import, windows!) : PackageLibrary.RuntimeKey(import)).Append('\n');
            var toolchain = PackageLibrary.OptimizingToolchain(new NativeConfig(), target);
            o.Append("toolchain ").Append(toolchain.EffectiveKind).Append('\n');
            string work = Path.Combine(Path.GetTempPath(), "fire-pkgbuild-" + Guid.NewGuid().ToString("N"));
            try
            {
                NativeRuntimeFiles.WriteTo(work, target.Native.Platform);
                string source = Path.Combine(work, isC ? "wrapper.c" : "wrapper.cpp");
                File.WriteAllText(source, wrapper);
                var (exe, arguments) = NativeBuilder.CompilerCommand(toolchain, target, source, work, Path.Combine(work, file), sharedLibrary: true, extraIncludeDirs: new[] { Path.Combine(work, "abi") }, language: isC ? "c" : "c++");
                o.Append("command ").Append(exe).Append(' ').Append(arguments.Replace(work, "<build>", StringComparison.Ordinal)).Append('\n');
                foreach (var f in Directory.EnumerateFiles(work, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(work, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal))
                    o.Append("file ").Append(f).Append(' ').Append(Sha(File.ReadAllBytes(Path.Combine(work, f)))).Append('\n');
            }
            finally { try { Directory.Delete(work, true); } catch (IOException) { } }
        }

        private static void Hex(StringBuilder o, string what, byte[] data) => o.Append(what).Append(' ').Append(data.Length).Append(' ').Append(Convert.ToHexString(data).ToLowerInvariant()).Append('\n');

        private static void PeResources(string path, StringBuilder o)
        {
            string full = Path.GetFullPath(path);
            var program = NativeBuilder.GenerateLinked(new[] { File.ReadAllText(path) }, TargetProfile.Windows, null, null, Path.GetDirectoryName(full), null, null, new string?[] { full }, toolchainKind: "gcc");
            var info = program.VersionInfo!;
            var editor = typeof(fire.Utilities.PeResourceEditor);
            o.Append("subsystem ").Append((int)info.Subsystem).Append('\n');
            Hex(o, "version", (byte[])editor.GetMethod("BuildVersionInfoResource", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { info })!);
            if (string.IsNullOrEmpty(program.IconPath)) return;
            o.Append("icon ").Append(program.IconPath).Append('\n');
            if (!File.Exists(program.IconPath)) { o.Append("icon not found\n"); return; }
            object images;
            try { images = editor.GetMethod("ReadIconImages", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { program.IconPath })!; }
            catch (TargetInvocationException ex) { o.Append("error ").Append(ex.InnerException!.Message).Append('\n'); return; }
            var list = ((System.Collections.IList)images).Cast<object>().ToList();
            for (int i = 0; i < list.Count; i++) Hex(o, $"image {i + 1}", (byte[])list[i].GetType().GetProperty("Data")!.GetValue(list[i])!);
            var ids = Enumerable.Range(1, list.Count).Select(i => (ushort)i).ToList();
            Hex(o, "group", (byte[])editor.GetMethod("BuildGroupIconResource", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { images, ids })!);
        }

        private static void ToolchainTest(string name, StringBuilder o)
        {
            var config = File.Exists(NativeConfig.FileName) ? NativeConfig.Load(NativeConfig.FileName) : new NativeConfig();
            var result = NativeBuilder.TestToolchain(config.ResolveToolchain(TargetProfile.Host, name));
            string? work = result.Output.Length > 0 ? Path.GetDirectoryName(result.Output) : null;
            o.Append(result.Ok ? "ok" : "failed").Append('\n').Append(work != null ? result.Log.Replace(work, "<work>", StringComparison.Ordinal) : result.Log).Append('\n');
        }
    }
}
