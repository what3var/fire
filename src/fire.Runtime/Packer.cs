using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace fire.Runtime
{
    /// <summary>
    /// Erzeugt die ausführbare Datei eines Fire-Programms - eine einzelne, eigenständige Datei:
    ///
    ///   1. Start-Stück: der apphost (fire.Runtime.exe) samt fire.Runtime.dll und runtimeconfig.json als .NET-Bundle
    ///      (siehe BundleWriter). Das ist alles, was der .NET-Host zum Starten braucht, und bewusst winzig.
    ///   2. Payload dahinter (siehe PayloadFile, einzeln Brotli-gepackt): das Programm selbst, der Kern (fire.dll,
    ///      MemoryPack) und - nur wenn das Programm sie per `#import` braucht - die jeweiligen Bridge-DLLs samt
    ///      Abhängigkeiten und nativen Bibliotheken (siehe PackagePlan).
    ///
    /// Beim Start liest die Runtime ihre NativeImports und lädt die DLLs bei Bedarf aus der eigenen Datei nach (siehe
    /// PayloadLoader); früher erledigte das Costura/Fody, eingebettet wurde dabei aber immer alles.
    ///
    /// Der Packer selbst läuft im Compiler/Editor, dessen Ordner die fertigen DLLs und den apphost enthält.
    /// </summary>
    public class Packer
    {
        /// <summary>Dateiname des apphost neben dem Compiler (Windows: fire.Runtime.exe, sonst ohne Endung).</summary>
        private static string StubFileName => OperatingSystem.IsWindows() ? "fire.Runtime.exe" : "fire.Runtime";

        /// <summary>Packt `program` zu einer eigenständigen Datei `outName`.
        /// `customizeApphost`: wird vor dem Bündeln mit dem Pfad einer Kopie des apphost aufgerufen, um z.B. Icon und
        /// Versionsinfo zu setzen. Das muss VOR dem Bündeln geschehen, weil das Ändern der PE-Ressourcen die Datei
        /// verschiebt und damit den Header-Offset des Bundles ungültig machen würde.
        /// Gibt die Pack-Planung zurück (welche DLLs eingebunden wurden).</summary>
        public static PackagePlan PackProgram(LinkedProgram program, string outName, Action<string>? customizeApphost = null, string? baseDir = null)
        {
            baseDir ??= AppContext.BaseDirectory;

            var stubPath = Path.Combine(baseDir, StubFileName);
            var runtimeDll = Path.Combine(baseDir, "fire.Runtime.dll");
            var runtimeConfig = Path.Combine(baseDir, "fire.Runtime.runtimeconfig.json");
            foreach (var required in new[] { stubPath, runtimeDll, runtimeConfig })
                if (!File.Exists(required))
                    throw new FileNotFoundException($"Zum Packen fehlt '{Path.GetFileName(required)}' im Ordner des Compilers ({baseDir}).", required);

            var plan = PackagePlan.Create(program.NativeImports, baseDir);
            if (plan.Unresolved.Count > 0)
                throw new InvalidOperationException("Zum Packen fehlen Abhängigkeiten im Ordner des Compilers: " + string.Join(", ", plan.Unresolved));

            // apphost kopieren und ggf. anpassen (Icon/Version).
            var tempStub = outName + "." + Guid.NewGuid().ToString("N") + ".stub";
            try
            {
                File.Copy(stubPath, tempStub, true);
                customizeApphost?.Invoke(tempStub);
                var apphost = File.ReadAllBytes(tempStub);

                var bundle = new List<BundleWriter.BundleFile>
                {
                    new("fire.Runtime.dll", BundleWriter.FileType.Assembly, File.ReadAllBytes(runtimeDll)),
                    new("fire.Runtime.runtimeconfig.json", BundleWriter.FileType.RuntimeConfigJson, File.ReadAllBytes(runtimeConfig)),
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

            using (var fs = new FileStream(outName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                PayloadFile.Append(fs, items);

            return plan;
        }

        /// <summary>Liest das Programm aus einer gepackten Datei (für Tests/Werkzeuge; die Runtime selbst nutzt den
        /// PayloadLoader). Setzt voraus, dass fire.dll und MemoryPack geladen werden können.</summary>
        public static LinkedProgram? UnpackProgram(string fileName)
        {
            var reader = PayloadFile.Open(fileName);
            var entry = reader?.Find(PayloadKind.Program, "program");
            var bin = entry == null ? null : reader!.Read(entry);
            return bin == null ? null : Deserialize(bin);
        }

        /// <summary>Wandelt die Programm-Bytes aus dem Payload zurück (eigene Methode, damit fire.dll/MemoryPack erst
        /// beim Aufruf geladen werden, nicht schon beim Laden des Packers).</summary>
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
