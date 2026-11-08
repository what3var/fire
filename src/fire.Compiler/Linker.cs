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
        /// <summary>Base directory for relative `#include`/`#extern` paths.
        /// null = current working directory. The editor sets here the
        /// directory of the active file.</summary>
        public string? BasePath { get; set; }

        /// <summary>How a type conflict is reported (docs/spec/TYPES.md): a warning (lax, the default) or an error (strict); a `#lax` / `#strict` in a file decides for that file.</summary>
        public TypeCheckMode? TypeChecking { get; set; }

        /// <summary>The mode the command line asked for (`--types lax|strict`); used by every linker that has no mode of its own set.</summary>
        public static TypeCheckMode? ProcessDefaultTypeChecking { get; set; }
        /// <summary>The width of `int` the command line sets for every program of this process (`-i 32`); wins against `#intwidth`, the project and the target.</summary>
        public static int? ProcessIntWidth { get; set; }

        /// <summary>Also check the preludes and libraries (not only the code of the user): used by the tests that keep the declared types of the standard library honest (`FIRE_TYPECHECK_ALL=1` does it for a build).</summary>
        public bool TypeCheckLibraries { get; set; } = Environment.GetEnvironmentVariable("FIRE_TYPECHECK_ALL") == "1";

        private TypeCheckMode EffectiveTypeChecking => TypeChecking ?? ProcessDefaultTypeChecking
            ?? (string.Equals(Plan?.Settings?.TypeChecking, "strict", StringComparison.OrdinalIgnoreCase) ? TypeCheckMode.Strict : TypeCheckMode.Lax);

        /// <summary>When the main program processes its queue by itself (`--global-jobs`, docs/spec/THREADING_DESIGN.md section 7): goes before the project, `#globaljobs` and
        /// fire.native.json. Process-wide, like <see cref="ProcessDefaultTypeChecking"/>.</summary>
        public static GlobalJobsMode? ProcessGlobalJobs { get; set; }

        /// <summary>`"globalJobs"` of fire.native.json (a native build): used when neither the command line, the project nor `#globaljobs`/`#nosync` say anything.</summary>
        public GlobalJobsMode? GlobalJobsDefault { get; set; }

        /// <summary>The direction of a native build (NativeBuilder.OptimizeOf): carried by the linked program for the generator.</summary>
        public OptimizeFor Optimize { get; set; }

        /// <summary>The command line, then the project (`settings.globalJobs`); null: the source decides (`#globaljobs`, then <see cref="GlobalJobsDefault"/>).</summary>
        private GlobalJobsMode? ForcedGlobalJobs => ProcessGlobalJobs ?? GlobalJobsModes.Parse(Plan?.Settings?.GlobalJobs);

        /// <summary>The engine the program is built for, a symbol of `#if` (`vm` or `native`).</summary>
        public string Engine { get; set; } = ConditionalSymbols.DefaultEngine;

        /// <summary>The kind of the C toolchain of a native build (`gcc`, `clang`, `msvc`, `tcc`, `emscripten`, `watcom`, ...): a symbol of `#if`, so that a program
        /// can choose the C of its `inline c` functions for the compiler (ROADMAP 13.2). Null: no native build, or not known.</summary>
        public string? ToolchainKind { get; set; }

        /// <summary>Further symbols of `#if` (`-D name` on the command line).</summary>
        public IReadOnlyList<string>? Defines { get; set; }

        /// <summary>The project that is built (docs/tools/projects.md): its settings go before the tags in the source, its libraries can be imported with `#import "Name"`, its package references are
        /// checked, and a library project must not have statements at the top level. Null: single sources without a project.</summary>
        public fire.Projects.BuildPlan? Plan { get; set; }

        /// <summary>The files the `sources` come from, in the same order (null entries: no file): relative paths of `#include`/`#extern` in a source are taken from its own folder, and an error
        /// says which file it is in. With a <see cref="Plan"/> these are the files of the project.</summary>
        public IReadOnlyList<string?>? SourcePaths { get; set; }

        /// <summary>Write the reflection metadata of the classes (ClassMeta) even when the program does not import `reflection`: a plugin (`--linkage plugin`),
        /// whose classes the program that loads it reaches by reflection (Reflect.Get/Set, `Type.Of(x).Members`).</summary>
        public bool ClassMeta { get; set; }

        /// <summary>Imports of packages (their keys, `pkg:usb`) whose fire code is the program's own source (a plugin made of a package's prelude, PackageDriverAgent):
        /// their natives are the program's, their prelude is not inserted again.</summary>
        public IReadOnlyCollection<string>? OwnImports { get; set; }

        /// <summary>The symbols of `#if`: the ones asked for and those of the project's settings.</summary>
        private IReadOnlyList<string>? EffectiveDefines()
        {
            var fromProject = Plan?.Settings.Defines;
            if (fromProject == null || fromProject.Count == 0) return Defines;
            return (Defines ?? Array.Empty<string>()).Concat(fromProject).Distinct().ToList();
        }

        /// <summary>The settings of a project over what the tags of the source said (the order of docs/tools/projects.md: project, solution, tags, defaults).</summary>
        public static void ApplySettings(AssemblyInfo info, fire.Projects.ProjectSettings? settings, ref int? floatWidth)
        {
            int? intWidth = null;
            ApplySettings(info, settings, ref floatWidth, ref intWidth);
        }

        public static void ApplySettings(AssemblyInfo info, fire.Projects.ProjectSettings? settings, ref int? floatWidth, ref int? intWidth)
        {
            if (settings == null) return;
            if (settings.IntWidth != null) intWidth = settings.IntWidth;
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

        public static AssemblyInfo ExtractAssemblyInfo(IReadOnlyList<string> sources, IEnumerable<string>? defines = null, fire.Projects.ProjectSettings? settings = null, string? basePath = null)
        {
            var assemblyInfo = new AssemblyInfo();
            var alreadyIncluded = new HashSet<string>();
            
            var processedSources = new List<ProcessedSource>();
            var inputSources = new List<string>() { fire.Standard.Prelude.Source };

            inputSources.AddRange(sources);

            var registry = DirectiveRegistry.CreateDefault(); // completely empty, NOT CreateDefault()
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
            registry.Register("intwidth", 1, (ctx, args, line) =>
            {
                ParseIntWidth(args[0]);
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
                // (an `#include` is relative to the file that has it: here the folder of the script, `basePath`)
                var processed = Preprocessor.Process(source, basePath ?? Directory.GetCurrentDirectory(), alreadyIncluded, registry);
                processedSources.Add(processed);
            }

            int? ignoredFloatWidth = null;
            ApplySettings(assemblyInfo, settings, ref ignoredFloatWidth);   // what the project fixes is shown as it is
            return assemblyInfo;
        }

        /// <summary>The value of `#intwidth n` (16, 32 or 64).</summary>
        public static int ParseIntWidth(Value arg)
        {
            if (arg.Kind == ValueKind.Int && arg.AsInt() is 16 or 32 or 64) return (int)arg.AsInt();
            throw new Exception("The 'intwidth' directive expects 16, 32 or 64.");
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

        /// <summary>The SHA-256 (hex, lower case) of the text of each source of the program as the compiler got it, by source index (see LinkedProgram.SourceHashes): the sources
        /// of the program have one, the prelude and the libraries none.</summary>
        private static IReadOnlyList<string?> SourceHashesOf(int total, IReadOnlyList<string> sources)
        {
            var hashes = new string?[total];
            for (int i = 0; i < sources.Count && total - sources.Count + i >= 0; i++)
                hashes[total - sources.Count + i] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sources[i]))).ToLowerInvariant();
            return hashes;
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

        /// <summary>The key of an import the compiler knows itself (`graphics`, `windows`, `ui`, `linq`, `reflection`), else null.</summary>
        /// <summary>The key of an import whose natives are part of the virtual machine (its fire code is a standard package); null for any other.</summary>
        public static string? BuiltInKey(string importName) => importName.ToLowerInvariant() switch
        {
            "graphics" => NativeImports.Graphics, "windows" => NativeImports.Windows, "ui" => NativeImports.Ui, "linq" => NativeImports.Linq, "reflection" => NativeImports.Reflection,
            _ => null,
        };

        /// <summary>A statement at the top level that only declares (runs nothing): a class, interface, enum, function, namespace, extension or a directive.</summary>
        private static bool IsDeclaration(Ast.Stmt stmt) =>
            stmt is Ast.ClassDecl or Ast.InterfaceDecl or Ast.EnumDecl or Ast.ExternDecl or Ast.ClassExtensionDecl or Ast.NamespaceDecl or Ast.MethodDecl or Ast.NoOpStmt or Ast.NoShadowDirective or Ast.GlobalJobsDirective or Ast.TypeCheckDirective or Ast.TimeoutDirective;

        /// <summary>`forge test` (docs/tools/testing.md): makes the source of the test driver from the parsed program and the index of its first own source. While it is set (on this thread), the
        /// statements at the top level of the program's files are left out and the driver is the last source.</summary>
        [ThreadStatic] public static Func<IReadOnlyList<Ast.Stmt>, int, string>? TestDriver;

        /// <summary>A library has no entry point: declarations only (classes, interfaces, enums, functions, namespaces, extensions), nothing that runs at the top level.</summary>
        private void CheckLibraryHasNoEntryPoint(IReadOnlyList<Ast.Stmt> program, int firstUserSource)
        {
            var errors = new List<string>();
            int userCount = Plan!.Sources.Count;
            foreach (var stmt in program)
            {
                if (stmt.Source < firstUserSource || stmt.Source >= firstUserSource + userCount) continue;
                if (IsDeclaration(stmt)) continue;
                var file = Plan.Sources[stmt.Source - firstUserSource];
                errors.Add($"{Path.GetFileName(file.Path)}:{stmt.Line}: a library has no entry point - this statement would run at the top level (put it in a function or a class).");
                if (errors.Count >= 20) break;
            }
            if (errors.Count > 0) throw new LibraryEntryPointException(string.Join(Environment.NewLine, errors));
        }

        /// <summary>The modules each module of the program needs (ROADMAP 9): the imports its sources name (with what they bring along) and, for a library of the project,
        /// the libraries it references. `core` (the prelude) is needed by all and not listed.</summary>
        private IReadOnlyDictionary<string, IReadOnlyList<string>> ModuleDependencies(IReadOnlyList<ProcessedSource> processed)
        {
            var deps = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var modules = processed.Skip(1).Select(p => p.Module).Where(m => m != null).Select(m => m!).Distinct().ToList();
            foreach (var module in modules)
            {
                var needs = new List<string>();
                void Need(string key) { if (key != module && !needs.Contains(key)) needs.Add(key); }
                if (Plan != null && Plan.Libraries.TryGetValue(module, out var library))
                {
                    foreach (var other in library.Requires) if (Plan.Libraries.TryGetValue(other, out var lib)) Need(lib.ImportName);
                    foreach (var file in library.Sources)
                        foreach (var name in ImportedPreludes.FindImportNames(file.Text))
                        {
                            if (Plan.Libraries.TryGetValue(name, out var lib)) { Need(lib.ImportName); continue; }
                            try { foreach (var key in ImportedPreludes.WithDependencies(ImportedPreludes.ParseImportName(name))) Need(key); }
                            catch (Exception) { }
                        }
                }
                else
                {
                    try { foreach (var key in ImportedPreludes.WithDependencies(module)) Need(key); }
                    catch (Exception) { }
                }
                deps[module] = needs.Where(modules.Contains).ToList();
            }
            return deps;
        }

        /// <param name="floatWidthOverride">32 or 64: precision of `float` for this build (command line); overrides `#floatwidth` and the target.</param>
        /// <param name="target">The target the program is built for: its `#import` libraries are checked and its float precision is the
        /// default (`#floatwidth` and <paramref name="floatWidthOverride"/> win). Null = no restriction, 64 bits.</param>
        /// <param name="intWidthOverride">32 or 64: the width of `int` for this build (command line); overrides `#intwidth`, the project and the target.</param>
        public LinkedProgram CompileAndLink(IReadOnlyList<string> sources, Func<Value[], Value>? debugWriter = null, string? outname = null, VmExecutionMode? executionModeOverride = null, int? floatWidthOverride = null, TargetProfile? target = null, int? intWidthOverride = null)
        {
            int? directiveFloatWidth = null;
            int? directiveIntWidth = null;
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

            var registry = DirectiveRegistry.CreateDefault(); // completely empty, NOT CreateDefault()
            var resources = new ResourceTable();
            registry.Resources = resources;
            foreach (var symbol in ConditionalSymbols.For(target, Engine, floatWidthOverride, EffectiveDefines(), intWidthOverride ?? ProcessIntWidth)) registry.Symbols.Add(symbol);
            if (!string.IsNullOrWhiteSpace(ToolchainKind)) registry.Symbols.Add(ToolchainKind.Trim());
            var projectImports = new List<string>();   // libraries of the project that a source imports
            ProjectNativeOverlay.Register(Plan);   // the C++ natives of the project and its libraries are found like those of installed packages
            if (ProjectNativeOverlay.OwnKey(Plan) is { } ownNativeKey) nativeImports.Add(ownNativeKey);
            // a library is its own import: `#import "name"` of its own name (in its tests, say) means the project's files - and a standard library of an import the compiler knows
            // (`graphics`, `reflection`, ...) has the natives of the virtual machine, without the installed prelude
            string? ownImport = Plan?.Type == fire.Projects.OutputType.Library ? Plan.Project.Project.ImportName : null;
            var ownBuiltIn = new HashSet<string>();
            if (ownImport != null && BuiltInKey(ownImport) is { } ownKey) { ownBuiltIn.Add(ownKey); nativeImports.Add(ownKey); }
            foreach (var key in OwnImports ?? Array.Empty<string>())
            {
                ownBuiltIn.Add(key);
                foreach (var with in ImportedPreludes.WithDependencies(key)) nativeImports.Add(with);   // (what its package requires comes along as usual)
            }
            registry.Register("import", 1, (ctx, args, line) =>
            {
                if (args[0].Kind == ValueKind.String)
                {
                    string importName = args[0].AsString();
                    if (ownImport != null && string.Equals(importName, ownImport, StringComparison.OrdinalIgnoreCase)) return null;
                    if (ProjectLibraries.TryImport(Plan, importName, projectImports, nativeImports)) return null;   // a library of the project: its files come with it
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
            registry.Register("intwidth", 1, (ctx, args, line) =>
            {
                directiveIntWidth = ParseIntWidth(args[0]);
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



            // the files of a project are part of the program anyway: an `#include` of one of them (also of a markup file) adds nothing, so that nothing is declared twice
            if (Plan != null)
                foreach (var projectFile in Plan.SourcePaths) alreadyIncluded.Add(Path.GetFullPath(projectFile));
            // the same for the files the program is made of: a file that is part of it is not inserted a second time by an `#include` in another one
            if (SourcePaths != null)
                foreach (var sourceFile in SourcePaths) if (sourceFile != null) alreadyIncluded.Add(Path.GetFullPath(sourceFile));

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

            // Preludes (and native placeholders) of the extensions switched on via `#import` -
            // the same logic is used by the live diagnostics of the
            // editor (see ImportedPreludes). Every inserted prelude
            // shifts the start of the user code by one source.
            firstUserSource += ImportedPreludes.Insert(
                nativeImports, natives, processedSources,
                (preludeSource, folder) => Preprocessor.Process(preludeSource, folder, alreadyIncluded, registry), ownBuiltIn);

            // the libraries of the project come before the code of the project, the ones that others need first
            if (libraryFiles.Count > 0)
            {
                processedSources.InsertRange(1, libraryFiles.Select(l => l.Source));
                firstUserSource += libraryFiles.Count;
            }
            CheckPackageReferences();

            // No activeUsings/usingsByStmt needed any more (SPEC "Namespaces") -
            // every type reference in the AST carries its own namespace context
            // directly on itself (see Ast.TypeRef.Namespaces), set by the
            // parser when parsing each individual ProcessedSource.
            fire.Native.BuildMonitor.Step(fire.Native.BuildPhase.CompileFire, $"Compiling fire: parsing {processedSources.Count} sources", 0, 4);
            var program = Parser.ParseMultiple(processedSources);
            int driverSources = 0;
            if (TestDriver != null)
            {
                // `forge test`: the statements at the top level of the program's files do not run; a generated driver runs the tests instead
                program.RemoveAll(stmt => stmt.Source >= firstUserSource && !IsDeclaration(stmt));
                var driver = new ProcessedSource(TestDriver(program, firstUserSource), Array.Empty<string>(), "<tests>");
                processedSources.Add(driver);
                program.AddRange(Parser.ParseSource(driver, processedSources.Count - 1));
                driverSources = 1;
            }
            if (Plan != null && Plan.Type == fire.Projects.OutputType.Library) CheckLibraryHasNoEntryPoint(program, firstUserSource);
            fire.Native.BuildMonitor.Step(fire.Native.BuildPhase.CompileFire, "Compiling fire: resolving names", 1, 4);
            var resolveResult = Resolver.Resolve(program, natives.Names);
            string? memberLogFile = Environment.GetEnvironmentVariable("FIRE_MEMBER_LOG");
            if (memberLogFile != null)
            {
                // a refactoring tool: every member access whose receiver class is known, with the text of its line (TypeChecker.MemberLog)
                TypeChecker.MemberLog = new();
                try { TypeChecker.Check(program, resolveResult, TypeCheckMode.Lax, 0); }
                catch (Exception) { }
                var lines = new System.Text.StringBuilder();
                foreach (var e in TypeChecker.MemberLog)
                {
                    if (e.Source < 0 || e.Source >= processedSources.Count) continue;
                    var text = processedSources[e.Source].Source.Split('\n');
                    if (e.Line < 1 || e.Line > text.Length) continue;
                    lines.Append(e.Class).Append('\t').Append(e.Name).Append('\t').Append(e.Bare ? "1" : "0").Append('\t').Append(text[e.Line - 1].TrimEnd('\r')).Append('\n');
                }
                TypeChecker.MemberLog = null;
                lock (typeof(Linker)) File.AppendAllText(memberLogFile, lines.ToString());
            }
            string? varLogFile = Environment.GetEnvironmentVariable("FIRE_VAR_LOG");
            if (varLogFile != null)
            {
                // a migration tool: every `var` without a type of its own (TC019/TC020), in every source (the libraries too), with the text of its line
                TypeChecker.VarLog = new();
                try { TypeChecker.Check(program, resolveResult, TypeCheckMode.Strict, 0); }
                catch (Exception) { }
                var lines = new System.Text.StringBuilder();
                foreach (var e in TypeChecker.VarLog)
                {
                    if (e.Source < 0 || e.Source >= processedSources.Count) continue;
                    var text = processedSources[e.Source].Source.Split('\n');
                    if (e.Line < 1 || e.Line > text.Length) continue;
                    lines.Append(processedSources[e.Source].Module ?? processedSources[e.Source].Name ?? "?").Append('\t').Append(e.Line).Append('\t').Append(e.Name).Append('\t').Append(e.Code).Append('\t').Append(e.Type).Append('\t').Append(text[e.Line - 1].TrimEnd('\r')).Append('\n');
                }
                TypeChecker.VarLog = null;
                lock (typeof(Linker)) File.AppendAllText(varLogFile, lines.ToString());
            }
            fire.Native.BuildMonitor.Step(fire.Native.BuildPhase.CompileFire, "Compiling fire: checking types", 2, 4);
            // (the width of the program's int as below - the project's setting before the tag of the source: TC021 needs it)
            TypeChecker.ProgramIntWidth = intWidthOverride ?? ProcessIntWidth ?? Plan?.Settings?.IntWidth ?? directiveIntWidth ?? target?.IntWidth ?? 64;
            TypeChecker.NativeReturns = PackageImports.NativeReturnTypes(nativeImports);
            TypeChecker.Run(program, resolveResult, EffectiveTypeChecking, TypeCheckLibraries ? 0 : firstUserSource);
            fire.Native.BuildMonitor.Step(fire.Native.BuildPhase.CompileFire, "Compiling fire: generating bytecode", 3, 4);
            var compiled = Compiler.Compile(program, resolveResult, natives, ClassMeta, ForcedGlobalJobs, GlobalJobsDefault);
            // the classes of a package with `"serializable": false` (docs/stdlib/json.md)
            var notSerializable = new HashSet<int>(Enumerable.Range(0, processedSources.Count).Where(i => processedSources[i].NotSerializable));
            if (notSerializable.Count > 0)
                foreach (var rc in compiled.Classes.Values)
                    if (rc.Decl != null && notSerializable.Contains(rc.Decl.Source)) rc.NotSerializable = true;
            compiled.Resources = resources.Entries;   // the files of `new Resource("path")` travel with the program (a packed file, a native build)
            compiled.CustomUnits = fire.Values.Unit.CustomUnits.ToList();   // (read by Parser.ParseMultiple)

            if (target != null)
                foreach (var import in nativeImports)
                    if (!target.HasImport(import))
                        throw new NotSupportedException($"The library '{import}' is not available on the target '{target.Name}' (available: {string.Join(", ", target.Imports!)}).");
            ApplySettings(assemblyInfo, Plan?.Settings, ref directiveFloatWidth, ref directiveIntWidth);   // the project's settings before the tags of the source
            int floatWidth = floatWidthOverride ?? directiveFloatWidth ?? target?.FloatWidth ?? 64;
            if (floatWidth != 32 && floatWidth != 64) throw new ArgumentOutOfRangeException(nameof(floatWidthOverride), "The float width must be 32 or 64.");
            if (floatWidth == 32) FloatNarrowing.Apply(compiled);
            int intWidth = intWidthOverride ?? ProcessIntWidth ?? directiveIntWidth ?? target?.IntWidth ?? 64;
            if (intWidth != 16 && intWidth != 32 && intWidth != 64) throw new ArgumentOutOfRangeException(nameof(intWidthOverride), "The int width must be 16, 32 or 64.");
            if (intWidth < 64) FloatNarrowing.ApplyIntWidth(compiled, intWidth);

            var packageNatives = PackageImports.NativesOf(nativeImports);
            // a program that is packed carries the libraries of its packages: they are built now (a native build does not need them: it takes the C++ source)
            List<string>? packageLibraryFiles = !string.IsNullOrEmpty(outname) && Engine != "native" ? PackageImports.EnsureLibraries(nativeImports) : null;
            // graphics and windows in C (the default of the VM): a packed program binds their natives to the library of both, which travels with it
            List<string>? graphicsNatives = null;
            if (!string.IsNullOrEmpty(outname) && Engine != "native" && nativeImports.Contains(NativeImports.Graphics) && GraphicsLibrary.Wanted(false) && GraphicsLibrary.Locate() is { } gfxLibrary)
            {
                graphicsNatives = natives.Names.Where(n => n.StartsWith("__GRPH", StringComparison.Ordinal)).OrderBy(natives.IndexOf).ToList();
                (packageLibraryFiles ??= new List<string>()).Add(gfxLibrary);
            }
            var linkedProgram = new LinkedProgram(compiled, nativeImports, firstUserSource, executionModeOverride ?? assemblyInfo.ExecutionMode) { NativeNames = natives.Names.ToList(), FloatWidth = floatWidth, IntWidth = intWidth, WordWidth = target?.WordWidth ?? 64, PackageNatives = packageNatives.Names, PackageNativeLibraries = packageNatives.Libraries, PackageLibraryFiles = packageLibraryFiles, GraphicsNatives = graphicsNatives, GuiSubsystem = assemblyInfo.Subsystem == Utilities.SubsystemType.GUI, Optimize = Optimize, VersionInfo = Engine == "native" ? assemblyInfo.ToVersionInfo() : null, IconPath = assemblyInfo.IconPath, SourceFiles = SourceFilesOf(processedSources.Count - driverSources, libraryFiles.Select(l => l.Path).ToList(), sources.Count).Concat(new string?[driverSources]).ToList(),
                SourceHashes = SourceHashesOf(processedSources.Count - driverSources, sources).Concat(new string?[driverSources]).ToList(),
                SourceModules = processedSources.Select((p, i) => i == 0 ? "core" : p.Module).ToList(), ModuleDeps = ModuleDependencies(processedSources),
                SourceTextHashes = processedSources.Select(p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.Source))).ToLowerInvariant()).ToList() };

            if (!string.IsNullOrEmpty(outname))
            {
                // Icon and version info belong into the apphost BEFORE it becomes the bundle (see Packer.PackProgram).
                var verInfo = assemblyInfo.ToVersionInfo();
                var tempfile = outname + ".tmp";
                try
                {
                    Packer.PackProgram(linkedProgram, tempfile, apphost =>
                    {
                        Utilities.PeResourceEditor.SetSubsystem(apphost, assemblyInfo.Subsystem);   // the PE header decides about the console window (plain bytes, any OS)
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
