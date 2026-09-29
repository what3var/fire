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
                    throw new Exception($"Falsche Argumente für 'name'-Direktive.");

                assemblyInfo.ProductName = args[0].AsString();
                return null;
            });
            registry.Register("codename", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'codename'-Direktive.");

                assemblyInfo.InternalName = args[0].AsString();
                return null;
            });
            registry.Register("description", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'description'-Direktive.");

                assemblyInfo.FileDescription = args[0].AsString();
                return null;
            });
            registry.Register("author", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'author'-Direktive.");

                assemblyInfo.CompanyName = args[0].AsString();
                return null;
            });
            registry.Register("comments", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'comments'-Direktive.");

                assemblyInfo.Comments = args[0].AsString();
                return null;
            });
            registry.Register("icon", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'icon'-Direktive.");

                assemblyInfo.IconPath = args[0].AsString();
                return null;
            });
            registry.Register("version", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'version'-Direktive.");

                assemblyInfo.ProductVersion = args[0].AsString();
                return null;
            });
            registry.Register("fileversion", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'fileversion'-Direktive.");

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

        public LinkedProgram CompileAndLink(IReadOnlyList<string> sources, Func<Value[], Value>? debugWriter = null, string? outname = null)
        {
            var assemblyInfo = new AssemblyInfo();
            var natives = new NativeRegistry();
            var nativeImports = new HashSet<string>();
            var alreadyIncluded = new HashSet<string>();
            var firstUserSource = 1;

            natives.Register("print", args => Value.MakeUndefined());

            nativeImports.Add(NativeImports.Print);

            var processedSources = new List<ProcessedSource>();
            var inputSources = new List<string>() { fire.Standard.Prelude.Source };

            inputSources.AddRange(sources);

            var registry = DirectiveRegistry.CreateDefault(); // komplett leer, NICHT CreateDefault()
            registry.Register("import", 1, (ctx, args, line) =>
            {
                if (args[0].Kind == ValueKind.String)
                {
                    switch (args[0].AsString().ToLower())
                    {
                        case "graphics":
                            nativeImports.Add(NativeImports.Graphics);
                            firstUserSource++;
                            return null;
                        case "devices":
                            nativeImports.Add(NativeImports.Devices);
                            firstUserSource++;
                            return null;
                        default:
                            throw new Exception($"'{args[0].AsString()}' ist keine bekannte Erweiterung.");
                    }
                }
                throw new Exception($"Falsche Argumente für 'import'-Direktive.");
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
                    throw new Exception($"Falsche Argumente für 'name'-Direktive.");

                assemblyInfo.ProductName = args[0].AsString();
                return null;
            });
            registry.Register("codename", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'codename'-Direktive.");

                assemblyInfo.InternalName = args[0].AsString();
                return null;
            });
            registry.Register("description", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'description'-Direktive.");

                assemblyInfo.FileDescription = args[0].AsString();
                return null;
            });
            registry.Register("author", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'author'-Direktive.");

                assemblyInfo.CompanyName = args[0].AsString();
                return null;
            });
            registry.Register("comments", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'comments'-Direktive.");

                assemblyInfo.Comments = args[0].AsString();
                return null;
            });
            registry.Register("icon", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'icon'-Direktive.");

                assemblyInfo.IconPath = args[0].AsString();
                return null;
            });
            registry.Register("version", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'version'-Direktive.");

                assemblyInfo.ProductVersion = args[0].AsString();
                return null;
            });
            registry.Register("fileversion", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new Exception($"Falsche Argumente für 'fileversion'-Direktive.");

                assemblyInfo.FileVersion = args[0].AsString();
                return null;
            });



            foreach (var source in inputSources)
            {
                var processed = Preprocessor.Process(source, Directory.GetCurrentDirectory(), alreadyIncluded, registry);
                processedSources.Add(processed);
            }

            if (nativeImports.Contains(NativeImports.Graphics))
            {
                var processed = Preprocessor.Process(GraphicsBridge.PreludeSource, Directory.GetCurrentDirectory(), alreadyIncluded, registry);
                processedSources.Insert(1, processed);

                GraphicsBridge.RegisterStubs(natives);
            }

            if (nativeImports.Contains(NativeImports.Devices))
            {
                var processed = Preprocessor.Process(DeviceBridge.PreludeSource, Directory.GetCurrentDirectory(), alreadyIncluded, registry);
                processedSources.Insert(1, processed);

                DeviceBridge.RegisterStubs(natives);
            }


            // Kein activeUsings/usingsByStmt mehr nötig (SPEC "Namespaces") -
            // jede Typ-Referenz im AST trägt ihren eigenen Namespace-Kontext
            // direkt an sich selbst (siehe Ast.TypeRef.Namespaces), vom
            // Parser beim Parsen jeder einzelnen ProcessedSource gesetzt.
            var program = Parser.ParseMultiple(processedSources);
            var resolveResult = Resolver.Resolve(program, natives.Names);
            var compiled = Compiler.Compile(program, resolveResult, natives);

            var linkedProgram = new LinkedProgram(compiled, nativeImports, firstUserSource);

            var outdir = Path.GetDirectoryName(Environment.ProcessPath);

            if (!string.IsNullOrEmpty(outname) && !string.IsNullOrEmpty(outdir))
            {
                var tempfile = Path.Combine(outdir, "tempout.a");
                Packer.PackProgram(linkedProgram, tempfile);

                var verInfo = assemblyInfo.ToVersionInfo();

                PeResourceEditor.SetVersionInfo(tempfile, verInfo);

                if (File.Exists(assemblyInfo.IconPath))
                    PeResourceEditor.SetIcon(tempfile, assemblyInfo.IconPath);

                File.Copy(tempfile, outname, true);
            }
            return linkedProgram;
        }
    }
}
