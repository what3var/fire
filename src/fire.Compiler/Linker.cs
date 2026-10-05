using fire.Bytecode;
using fire.Compiler.Assembly;
using fire.Device.Bridge;
using fire.Runtime;
using fire.Terminal;
using fire.Terminal.Bridge;
using fire.Terminal.Windows;
using fire.Utilities;
using fire.Values;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Compiler
{
    public class Linker
    {
        /// <summary>Basisverzeichnis für relative `#include`/`#extern`-Pfade.
        /// null = aktuelles Arbeitsverzeichnis. Der Editor setzt hier das
        /// Verzeichnis der aktiven Datei.</summary>
        public string? BasePath { get; set; }

        public static AssemblyInfo ExtractAssemblyInfo(IReadOnlyList<string> sources)
        {
            var assemblyInfo = new AssemblyInfo();
            var alreadyIncluded = new HashSet<string>();
            
            var processedSources = new List<ProcessedSource>();
            var inputSources = new List<string>() { fire.Standard.Prelude.Source };

            inputSources.AddRange(sources);

            var registry = DirectiveRegistry.CreateDefault(); // komplett leer, NICHT CreateDefault()
            registry.Register("import", 1, (ctx, args, line) =>
            {
                return null;
            });

            assemblyInfo.Subsystem = Utilities.SubsystemType.Console;
            assemblyInfo.ExecutionMode = VmExecutionMode.Release;

            registry.Register("noconsole", 0, (ctx, args, line) =>
            {
                assemblyInfo.Subsystem = Utilities.SubsystemType.GUI;
                return null;
            });

            registry.Register("debug", 0, (ctx, args, line) =>
            {
                assemblyInfo.ExecutionMode = VmExecutionMode.Debug;
                return null;
            });
            registry.Register("performance", 0, (ctx, args, line) =>
            {
                assemblyInfo.ExecutionMode = VmExecutionMode.Performance;
                return null;
            });

            assemblyInfo.FileVersion = "0.0.0.0";
            assemblyInfo.ProductVersion = "0.0.0.0";


            registry.Register("name", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'name' directive.");

                assemblyInfo.ProductName = args[0].AsString();
                return null;
            });
            registry.Register("codename", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'codename' directive.");

                assemblyInfo.InternalName = args[0].AsString();
                return null;
            });
            registry.Register("description", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'description' directive.");

                assemblyInfo.FileDescription = args[0].AsString();
                return null;
            });
            registry.Register("author", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'author' directive.");

                assemblyInfo.CompanyName = args[0].AsString();
                return null;
            });
            registry.Register("comments", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'comments' directive.");

                assemblyInfo.Comments = args[0].AsString();
                return null;
            });
            registry.Register("icon", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'icon' directive.");

                assemblyInfo.IconPath = args[0].AsString();
                return null;
            });
            registry.Register("version", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'version' directive.");

                assemblyInfo.ProductVersion = args[0].AsString();
                return null;
            });
            registry.Register("fileversion", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'fileversion' directive.");

                assemblyInfo.FileVersion = args[0].AsString();
                return null;
            });

            foreach (var source in inputSources)
            {
                var processed = Preprocessor.Process(source, Directory.GetCurrentDirectory(), alreadyIncluded, registry);
                processedSources.Add(processed);
            }

            return assemblyInfo;
        }

        public LinkedProgram CompileAndLink(IReadOnlyList<string> sources, Func<Value[], Value>? debugWriter = null, string? outname = null, VmExecutionMode? executionModeOverride = null)
        {
            var assemblyInfo = new AssemblyInfo();
            var natives = new NativeRegistry();
            var nativeImports = new HashSet<string>();
            var alreadyIncluded = new HashSet<string>();
            var firstUserSource = 1;

            natives.Register("print", args => Value.MakeUndefined());
            natives.RegisterBaseTypeNatives();

            nativeImports.Add(NativeImports.Print);

            var processedSources = new List<ProcessedSource>();
            var inputSources = new List<string>() { fire.Standard.Prelude.Source };

            inputSources.AddRange(sources);

            var registry = DirectiveRegistry.CreateDefault(); // komplett leer, NICHT CreateDefault()
            registry.Register("import", 1, (ctx, args, line) =>
            {
                if (args[0].Kind == ValueKind.String)
                {
                    foreach (var key in ImportedPreludes.WithDependencies(ImportedPreludes.ParseImportName(args[0].AsString()))) nativeImports.Add(key);
                    return null;
                }
                throw new Exception($"Wrong arguments for the 'import' directive.");
            });

            assemblyInfo.Subsystem = Utilities.SubsystemType.Console;
            assemblyInfo.ExecutionMode = VmExecutionMode.Release;

            registry.Register("noconsole", 0, (ctx, args, line) =>
            {
                assemblyInfo.Subsystem = Utilities.SubsystemType.GUI;
                return null;
            });

            registry.Register("debug", 0, (ctx, args, line) =>
            {
                assemblyInfo.ExecutionMode = VmExecutionMode.Debug;
                return null;
            });
            registry.Register("performance", 0, (ctx, args, line) =>
            {
                assemblyInfo.ExecutionMode = VmExecutionMode.Performance;
                return null;
            });

            assemblyInfo.FileVersion = "0.0.0.0";
            assemblyInfo.ProductVersion = "0.0.0.0";
            assemblyInfo.OriginalFilename = outname;


            registry.Register("name", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'name' directive.");

                assemblyInfo.ProductName = args[0].AsString();
                return null;
            });
            registry.Register("codename", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'codename' directive.");

                assemblyInfo.InternalName = args[0].AsString();
                return null;
            });
            registry.Register("description", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'description' directive.");

                assemblyInfo.FileDescription = args[0].AsString();
                return null;
            });
            registry.Register("author", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'author' directive.");

                assemblyInfo.CompanyName = args[0].AsString();
                return null;
            });
            registry.Register("comments", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'comments' directive.");

                assemblyInfo.Comments = args[0].AsString();
                return null;
            });
            registry.Register("icon", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'icon' directive.");

                assemblyInfo.IconPath = args[0].AsString();
                return null;
            });
            registry.Register("version", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'version' directive.");

                assemblyInfo.ProductVersion = args[0].AsString();
                return null;
            });
            registry.Register("fileversion", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Wrong arguments for the 'fileversion' directive.");

                assemblyInfo.FileVersion = args[0].AsString();
                return null;
            });



            foreach (var source in inputSources)
            {
                var processed = Preprocessor.Process(source, (BasePath ?? Directory.GetCurrentDirectory()), alreadyIncluded, registry);
                processedSources.Add(processed);
            }

            // Preludes (und native Platzhalter) der per `#import` zugeschalteten
            // Erweiterungen - dieselbe Logik nutzt die Live-Diagnostik des
            // Editors (siehe ImportedPreludes). Jede eingefügte Prelude
            // verschiebt den Start des Nutzer-Codes um eine Quelle.
            firstUserSource += ImportedPreludes.Insert(
                nativeImports, natives, processedSources,
                preludeSource => Preprocessor.Process(preludeSource, (BasePath ?? Directory.GetCurrentDirectory()), alreadyIncluded, registry));

            // Kein activeUsings/usingsByStmt mehr nötig (SPEC "Namespaces") -
            // jede Typ-Referenz im AST trägt ihren eigenen Namespace-Kontext
            // direkt an sich selbst (siehe Ast.TypeRef.Namespaces), vom
            // Parser beim Parsen jeder einzelnen ProcessedSource gesetzt.
            var program = Parser.ParseMultiple(processedSources);
            var resolveResult = Resolver.Resolve(program, natives.Names);
            var compiled = Compiler.Compile(program, resolveResult, natives);

            var linkedProgram = new LinkedProgram(compiled, nativeImports, firstUserSource, executionModeOverride ?? assemblyInfo.ExecutionMode) { NativeNames = natives.Names.ToList() };

            if (!string.IsNullOrEmpty(outname))
            {
                // Icon und Versionsinfo gehören in den apphost, BEVOR er zum Bundle wird (siehe Packer.PackProgram).
                var verInfo = assemblyInfo.ToVersionInfo();
                var tempfile = outname + ".tmp";
                try
                {
                    Packer.PackProgram(linkedProgram, tempfile, apphost =>
                    {
                        if (!OperatingSystem.IsWindows()) return; // PeResourceEditor nutzt Win32-APIs

                        PeResourceEditor.SetVersionInfo(apphost, verInfo);

                        if (File.Exists(assemblyInfo.IconPath))
                            PeResourceEditor.SetIcon(apphost, assemblyInfo.IconPath);
                    });
                    File.Move(tempfile, outname, true);
                }
                finally
                {
                    if (File.Exists(tempfile)) File.Delete(tempfile);
                }
            }
            return linkedProgram;
        }
    }
}
