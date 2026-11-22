using fire.Compiler;
using fire.Debugging;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;

namespace fire.Editor
{
    // The C# VM in spark's DebugSession (VmDebugTarget): only the test program has it (DebuggerTests) - it goes away with fire.Runtime (docs/ROADMAP_C_PORT.md, M1).
    // spark itself compiles with forge and debugs `fire` or a native program over NativeDebugTarget.
    public sealed partial class DebugSession
    {
        /// <summary>Recompiles the source text and sets up a fresh
        /// main-thread VM (on its own background thread, see
        /// DebugThreadContext.ForMain - starts waiting, nothing runs yet).
        /// On a parse/resolve error HasProgram stays false, CompileError
        /// contains the message.</summary>
        /// <param name="environment">The arguments and the path the program gets (Environment.Args/ProgramPath); none without.</param>
        public bool Compile(string[] sources, string? outname = null, string? basePath = null, fire.Projects.BuildPlan? plan = null, fire.Standard.ProgramEnvironment? environment = null)
        {
            Reset();
            LastDiagnostics = Array.Empty<Diagnostic>();

            // the warnings of the code (`warn 123: "text"`) are collected while the parser reads it
            var warnings = new DiagnosticCollector();
            using var collecting = warnings.Begin();
            try
            {
                var target = VmDebugTarget.Build(sources, ExecutionMode, outname, basePath, DeviceManager, plan, environment);
                LastDiagnostics = warnings.Items.ToList();
                Attach(target);
                return true;
            }
            catch (Exception ex) when (ex is ParseException or LexException or ResolverException or TypeCheckException
                or NotSupportedException or PreprocessorException or LibraryEntryPointException or PackageReferenceException or fire.Projects.ProjectException or fire.Package.Manager.PackageException)
            {
                // All collected errors (the resolver/compiler do not stop at the
                // first, see CompileErrors), not only the first.
                CompileError = CompileErrors.Describe(ex);
                LastDiagnostics = CompileDiagnostics.Of(ex).Concat(warnings.Items).ToList();
                return false;
            }
        }

        /// <summary>Like <see cref="Compile"/>, but the compiler runs on a thread of the pool (the window stays responsive and can show the progress): <paramref name="monitor"/>
        /// gets its phases and the commands of a package's natives that are compiled for it. The session takes the program on the caller's thread. Cancelled: throws
        /// <see cref="OperationCanceledException"/> and keeps no program.</summary>
        public async System.Threading.Tasks.Task<bool> CompileAsync(string[] sources, string? outname, string? basePath, fire.Projects.BuildPlan? plan,
            fire.Standard.ProgramEnvironment? environment, fire.Native.BuildMonitor? monitor)
        {
            Reset();
            LastDiagnostics = Array.Empty<Diagnostic>();
            var mode = ExecutionMode;
            var devices = DeviceManager;
            var (target, diagnostics, error) = await System.Threading.Tasks.Task.Run(() =>
            {
                using var watching = fire.Native.BuildMonitor.Use(monitor);
                var warnings = new DiagnosticCollector();
                using var collecting = warnings.Begin();
                try
                {
                    var built = VmDebugTarget.Build(sources, mode, outname, basePath, devices, plan, environment);
                    if (monitor?.IsCancellationRequested == true)
                    {
                        built.Dispose();
                        throw new OperationCanceledException(monitor.Token);
                    }
                    return ((IDebugTarget?)built, (IReadOnlyList<Diagnostic>)warnings.Items.ToList(), (string?)null);
                }
                catch (Exception ex) when (ex is ParseException or LexException or ResolverException or TypeCheckException
                    or NotSupportedException or PreprocessorException or LibraryEntryPointException or PackageReferenceException or fire.Projects.ProjectException or fire.Package.Manager.PackageException)
                {
                    return (null, CompileDiagnostics.Of(ex).Concat(warnings.Items).ToList(), CompileErrors.Describe(ex));
                }
            });
            LastDiagnostics = diagnostics;
            if (target == null)
            {
                CompileError = error;
                return false;
            }
            Attach(target);
            return true;
        }
    }
}
