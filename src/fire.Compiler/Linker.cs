using fire.Bytecode;
using fire.Compiler.Assembly;
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
    /// <summary>A library project has statements at the top level.</summary>
    public sealed class LibraryEntryPointException : Exception
    {
        public LibraryEntryPointException(string message) : base(message) { }
    }

    public class Linker
    {
        /// <summary>Basisverzeichnis für relative `#include`/`#extern`-Pfade.
        /// null = aktuelles Arbeitsverzeichnis. Der Editor setzt hier das
        /// Verzeichnis der aktiven Datei.</summary>
        public string? BasePath { get; set; }

        /// <summary>The engine the program is built for, a symbol of `#if` (`vm` or `native`).</summary>
        public string Engine { get; set; } = ConditionalSymbols.DefaultEngine;

        /// <summary>Further symbols of `#if` (`-D name` on the command line).</summary>
        public IReadOnlyList<string>? Defines { get; set; }

        /// <summary>The project that is built (docs/PROJECTS.md): its settings go before the tags in the source, its libraries can be imported with `#import "Name"`, its package references are
        /// checked, and a library project must not have statements at the top level. Null: single sources without a project.</summary>
        public fire.Projects.BuildPlan? Plan { get; set; }

        /// <summary>The files the `sources` come from, in the same order (null entries: no file): relative paths of `#include`/`#extern` in a source are taken from its own folder, and an error
        /// says which file it is in. With a <see cref="Plan"/> these are the files of the project.</summary>
        public IReadOnlyList<string?>? SourcePaths { get; set; }

        /// <summary>The symbols of `#if`: the ones asked for and those of the project's settings.</summary>
        private IReadOnlyList<string>? EffectiveDefines()
        {
            var fromProject = Plan?.Settings.Defines;
            if (fromProject == null || fromProject.Count == 0) return Defines;
            return (Defines ?? Array.Empty<string>()).Concat(fromProject).Distinct().ToList();
        }

        /// <summary>The settings of a project over what the tags of the source said (the order of docs/PROJECTS.md: project, solution, tags, defaults).</summary>
        public static void ApplySettings(AssemblyInfo info, fire.Projects.ProjectSettings? settings, ref int? floatWidth)
        {
            if (settings == null) return;
            if (settings.Subsystem != null) info.Subsystem = settings.Subsystem == "gui" ? Utilities.SubsystemType.GUI : Utilities.SubsystemType.Console;
            if (settings.Mode != null) info.ExecutionMode = settings.Mode switch { "debug" => VmExecutionMode.Debug, "performance" => VmExecutionMode.Performance, _ => VmExecutionMode.Release };
            if (settings.FloatWidth != null) floatWidth = settings.FloatWidth;
            if (settings.Name != null) info.ProductName = settings.Name;
            if (settings.Codename != null) info.InternalName = settings.Codename;
            if (settings.Description != null) info.FileDescription = settings.Description;
            if (settings.Author != null) info.CompanyName = settings.Author;
            if (settings.Comments != null) info.Comments = settings.Comments;
            if (settings.Icon != null) info.IconPath = settings.Icon;
            if (settings.Version != null) info.ProductVersion = settings.Version;
            if (settings.FileVersion != null) info.FileVersion = settings.FileVersion;
        }

        public static AssemblyInfo ExtractAssemblyInfo(IReadOnlyList<string> sources, IEnumerable<string>? defines = null, fire.Projects.ProjectSettings? settings = null)
        {
            var assemblyInfo = new AssemblyInfo();
            var alreadyIncluded = new HashSet<string>();
            
            var processedSources = new List<ProcessedSource>();
            var inputSources = new List<string>() { fire.Standard.Prelude.Source };

            inputSources.AddRange(sources);

            var registry = DirectiveRegistry.CreateDefault(); // komplett leer, NICHT CreateDefault()
            foreach (var symbol in ConditionalSymbols.For(null, ConditionalSymbols.DefaultEngine, null, defines)) registry.Symbols.Add(symbol);
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
            registry.Register("floatwidth", 1, (ctx, args, line) =>
            {
                ParseFloatWidth(args[0]); // validated here, applied by CompileAndLink
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

            int? ignoredFloatWidth = null;
            ApplySettings(assemblyInfo, settings, ref ignoredFloatWidth);   // what the project fixes is shown as it is
            return assemblyInfo;
        }

        /// <summary>The value of `#floatwidth n` (32 or 64).</summary>
        public static int ParseFloatWidth(Value arg)
        {
            if (arg.Kind == ValueKind.Int && arg.AsInt() is 32 or 64) return (int)arg.AsInt();
            throw new Exception("The 'floatwidth' directive expects 32 or 64.");
        }

        /// <summary>The file of each source of the program (see LinkedProgram.SourceFiles): the prelude and the preludes of imports have none, then the files of the libraries, then the files of the program.</summary>
        private IReadOnlyList<string?> SourceFilesOf(int total, List<string> libraryPaths, int userCount)
        {
            var files = new string?[total];
            for (int i = 0; i < libraryPaths.Count && 1 + i < total; i++) files[1 + i] = libraryPaths[i];
            for (int i = 0; i < userCount; i++)
            {
                string? path = SourcePaths != null && i < SourcePaths.Count ? SourcePaths[i] : null;
                files[total - userCount + i] = path != null ? Path.GetFullPath(path) : null;
            }
            return files;
        }

        /// <summary>A package that the project references has to be installed (`ember install`); the version is a minimum.</summary>
        private void CheckPackageReferences()
        {
            if (Plan == null) return;
            foreach (var package in Plan.Packages)
            {
                var installed = Package.Manager.PackageStore.Default.Find(package.Package!);
                if (installed == null)
                    throw new Exception($"The project '{Plan.Name}' references the package '{package.Package}', which is not installed (`ember install {package.Package}`).");
                if (package.Version != null && Package.Manager.SemanticVersion.TryParse(package.Version, out var wanted) && Package.Manager.SemanticVersion.TryParse(installed.Version, out var have) && have.CompareTo(wanted) < 0)
                    throw new Exception($"The project '{Plan.Name}' needs the package '{package.Package}' {package.Version} or newer, {installed.Version} is installed.");
            }
        }

        /// <summary>A library has no entry point: declarations only (classes, interfaces, enums, functions, namespaces, extensions), nothing that runs at the top level.</summary>
        private void CheckLibraryHasNoEntryPoint(IReadOnlyList<Ast.Stmt> program, int firstUserSource)
        {
            var errors = new List<string>();
            int userCount = Plan!.Sources.Count;
            foreach (var stmt in program)
            {
                if (stmt.Source < firstUserSource || stmt.Source >= firstUserSource + userCount) continue;
                if (stmt is Ast.ClassDecl or Ast.InterfaceDecl or Ast.EnumDecl or Ast.ExternDecl or Ast.ClassExtensionDecl or Ast.NamespaceDecl or Ast.MethodDecl or Ast.NoOpStmt or Ast.NoShadowDirective or Ast.NoSyncDirective or Ast.TimeoutDirective) continue;
                var file = Plan.Sources[stmt.Source - firstUserSource];
                errors.Add($"{Path.GetFileName(file.Path)}:{stmt.Line}: a library has no entry point - this statement would run at the top level (put it in a function or a class).");
                if (errors.Count >= 20) break;
            }
            if (errors.Count > 0) throw new LibraryEntryPointException(string.Join(Environment.NewLine, errors));
        }

        /// <param name="floatWidthOverride">32 or 64: precision of `float` for this build (command line); overrides `#floatwidth` and the target.</param>
        /// <param name="target">The target the program is built for: its `#import` libraries are checked and its float precision is the
        /// default (`#floatwidth` and <paramref name="floatWidthOverride"/> win). Null = no restriction, 64 bits.</param>
        public LinkedProgram CompileAndLink(IReadOnlyList<string> sources, Func<Value[], Value>? debugWriter = null, string? outname = null, VmExecutionMode? executionModeOverride = null, int? floatWidthOverride = null, TargetProfile? target = null)
        {
            int? directiveFloatWidth = null;
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
            var resources = new ResourceTable();
            registry.Resources = resources;
            foreach (var symbol in ConditionalSymbols.For(target, Engine, floatWidthOverride, EffectiveDefines())) registry.Symbols.Add(symbol);
            var projectImports = new List<string>();   // libraries of the project that a source imports
            registry.Register("import", 1, (ctx, args, line) =>
            {
                if (args[0].Kind == ValueKind.String)
                {
                    string importName = args[0].AsString();
                    if (ProjectLibraries.TryImport(Plan, importName, projectImports)) return null;   // a library of the project: its files come with it
                    foreach (var key in ImportedPreludes.WithDependencies(ImportedPreludes.ParseImportName(importName))) nativeImports.Add(key);
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
            registry.Register("floatwidth", 1, (ctx, args, line) =>
            {
                directiveFloatWidth = ParseFloatWidth(args[0]);
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



            for (int i = 0; i < inputSources.Count; i++)
            {
                // a source of a file is processed from the folder of that file (an include in it is relative to it)
                string? file = i >= 1 && SourcePaths != null && i - 1 < SourcePaths.Count ? SourcePaths[i - 1] : null;
                string folder = file != null ? (Path.GetDirectoryName(Path.GetFullPath(file)) ?? Directory.GetCurrentDirectory()) : (BasePath ?? Directory.GetCurrentDirectory());
                var processed = Preprocessor.Process(inputSources[i], folder, alreadyIncluded, registry);
                if (file != null) processed = processed with { Name = Plan != null ? Path.GetRelativePath(Plan.Project.Directory, file).Replace('\\', '/') : Path.GetFileName(file) };
                processedSources.Add(processed);
            }

            // The libraries that the sources import (and those they need): their files are processed like the others - they may import more libraries and standard imports themselves.
            var libraryFiles = Plan != null && projectImports.Count > 0
                ? ProjectLibraries.Process(Plan, projectImports, file => Preprocessor.Process(file.Text, file.Directory, alreadyIncluded, registry))
                : new List<(ProcessedSource Source, string Path)>();

            // Preludes (und native Platzhalter) der per `#import` zugeschalteten
            // Erweiterungen - dieselbe Logik nutzt die Live-Diagnostik des
            // Editors (siehe ImportedPreludes). Jede eingefügte Prelude
            // verschiebt den Start des Nutzer-Codes um eine Quelle.
            firstUserSource += ImportedPreludes.Insert(
                nativeImports, natives, processedSources,
                preludeSource => Preprocessor.Process(preludeSource, (BasePath ?? Directory.GetCurrentDirectory()), alreadyIncluded, registry));

            // the libraries of the project come before the code of the project, the ones that others need first
            if (libraryFiles.Count > 0)
            {
                processedSources.InsertRange(1, libraryFiles.Select(l => l.Source));
                firstUserSource += libraryFiles.Count;
            }
            CheckPackageReferences();

            // Kein activeUsings/usingsByStmt mehr nötig (SPEC "Namespaces") -
            // jede Typ-Referenz im AST trägt ihren eigenen Namespace-Kontext
            // direkt an sich selbst (siehe Ast.TypeRef.Namespaces), vom
            // Parser beim Parsen jeder einzelnen ProcessedSource gesetzt.
            var program = Parser.ParseMultiple(processedSources);
            if (Plan != null && Plan.Type == fire.Projects.OutputType.Library) CheckLibraryHasNoEntryPoint(program, firstUserSource);
            var resolveResult = Resolver.Resolve(program, natives.Names);
            var compiled = Compiler.Compile(program, resolveResult, natives);
            compiled.Resources = resources.Entries;   // the files of `new Resource("path")` travel with the program (a packed file, a native build)

            if (target != null)
                foreach (var import in nativeImports)
                    if (!target.HasImport(import))
                        throw new NotSupportedException($"The library '{import}' is not available on the target '{target.Name}' (available: {string.Join(", ", target.Imports!)}).");
            ApplySettings(assemblyInfo, Plan?.Settings, ref directiveFloatWidth);   // the project's settings before the tags of the source
            int floatWidth = floatWidthOverride ?? directiveFloatWidth ?? target?.FloatWidth ?? 64;
            if (floatWidth != 32 && floatWidth != 64) throw new ArgumentOutOfRangeException(nameof(floatWidthOverride), "The float width must be 32 or 64.");
            if (floatWidth == 32) FloatNarrowing.Apply(compiled);

            var packageNatives = PackageImports.NativesOf(nativeImports);
            // a program that is packed carries the libraries of its packages: they are built now (a native build does not need them: it takes the C++ source)
            List<string>? packageLibraryFiles = !string.IsNullOrEmpty(outname) && Engine != "native" ? PackageImports.EnsureLibraries(nativeImports) : null;
            var linkedProgram = new LinkedProgram(compiled, nativeImports, firstUserSource, executionModeOverride ?? assemblyInfo.ExecutionMode) { NativeNames = natives.Names.ToList(), FloatWidth = floatWidth, PackageNatives = packageNatives.Names, PackageNativeLibraries = packageNatives.Libraries, PackageLibraryFiles = packageLibraryFiles, SourceFiles = SourceFilesOf(processedSources.Count, libraryFiles.Select(l => l.Path).ToList(), sources.Count) };

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
