using System.Text;
using fire.Bytecode;
using fire.Compiler;
using fire.Debugging;
using fire.Runtime;

/// <summary>
/// The debugger of the VM in C (docs/ROADMAP_C_PORT.md phase B step 6, native/vm/vm_debug.c): `fire x.fbc` with FIRE_DEBUG speaks the protocol of native
/// programs - the same plans as against a native debug build (stops, depths, call stack, scopes, this, globals at every stop, every step to the end) compared
/// with the C# VM, and the protocol checks of native builds that are not about the C (expand/set, hello, profile, pause, threads).
/// </summary>
static partial class NativeDebugTests
{
    static readonly object VmFireLock = new();
    static string? s_vmFire, s_vmFireError;

    /// <summary>The program `fire` of the repository (native/vm/fire.c), built once into the work directory; null with a reason when there is none.</summary>
    static string? VmFire(string cxx, string workDir, string sanitize, out string error)
    {
        lock (VmFireLock)
        {
            if (s_vmFire != null || s_vmFireError != null) { error = s_vmFireError ?? ""; return s_vmFire; }
            string? source = null;
            foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
                for (var dir = new DirectoryInfo(start); dir != null && source == null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "native", "vm", "fire.c"))) source = Path.Combine(dir.FullName, "native", "vm", "fire.c");
            if (source == null) { s_vmFireError = "native/vm/fire.c nicht gefunden"; error = s_vmFireError; return null; }
            string exe = Path.Combine(workDir, "fire_vmdbg");
            string build = RunTool(fire.Native.ToolchainDef.CCompilerFor(cxx), $"-O1 -g {sanitize} \"{source}\" -o \"{exe}\" -lm -lpthread -ldl", workDir, out int exit, 300000);
            if (exit != 0) { s_vmFireError = "fire.c: " + build; error = s_vmFireError; return null; }
            s_vmFire = exe;
            error = "";
            return exe;
        }
    }

    /// <summary>The case as a program file (mode Debug, with line tables and its sources), like `forge bytecode -m debug`.</summary>
    static string? BuildVm(string script, string dir, string name, out string error, string? sourcePath = null)
    {
        error = "";
        try
        {
            var linked = new Linker { SourcePaths = sourcePath != null ? new[] { sourcePath } : null }.CompileAndLink(new[] { script }, null, null, VmExecutionMode.Debug, null);
            string fbc = Path.Combine(dir, name + ".fbc");
            File.WriteAllBytes(fbc, ProgramFile.Write(linked));
            return fbc;
        }
        catch (Exception ex) { error = "nicht uebersetzbar: " + ex.Message; return null; }
    }

    static void RunVmEngine(Action<string, bool, string?> check, string cxx, string workDir, string sanitize)
    {
        var selected = Cases.Where(c => TestSelectedName("VM-Debugger: " + c.Name)).ToList();
        bool protocol = TestSelectedName("VM-Debugger: Protokoll");
        if (selected.Count == 0 && !TestSelectedName("VM-Debugger: expand") && !protocol) return;
        string? fire = VmFire(cxx, workDir, sanitize, out string fireError);
        if (fire == null) { check("VM-Debugger: fire (native/vm) bauen", false, fireError); return; }
        TestJobs.Ordered(selected.Count, index =>
        {
            var c = selected[index];
            var results = new List<(string Title, bool Ok, string? Detail)>();
            string? fbc = BuildVm(c.Script, workDir, $"vmdbg{index}", out string error);
            if (fbc == null) { results.Add(($"VM-Debugger: {c.Name} (Bau)", false, error)); return results; }
            try
            {
                var (vmStops, vmOut) = RunVm(c.Script, c.Breakpoints, c.Plan, true);
                var (cStops, cOut, cErr) = RunNative(() => NativeDebugClient.Launch(fbc, workDir, fire), c.Breakpoints, c.Plan, true);
                results.Add(($"VM-Debugger: {c.Name}: Haltepunkte/Schritte, Aufrufstapel, Scopes, this, Globals wie die C#-VM", vmStops.SequenceEqual(cStops) && vmOut.SequenceEqual(cOut),
                    Diff(vmStops, cStops) + $"  Ausgabe C#-VM: {string.Join("|", vmOut)}\n  Ausgabe C-VM: {string.Join("|", cOut)}\n  stderr: {cErr}"));
                if (c.StepAll)
                    foreach (var op in new[] { Op.StepInto, Op.StepOver })
                    {
                        var (vmAll, _) = RunVm(c.Script, Array.Empty<int>(), Array.Empty<Op>(), false, () => op);
                        var (cAll, _, cErr2) = RunNative(() => NativeDebugClient.Launch(fbc, workDir, fire), Array.Empty<int>(), Array.Empty<Op>(), false, () => op);
                        results.Add(($"VM-Debugger: {c.Name}: jeder {(op == Op.StepInto ? "Step Into" : "Step Over")} bis zum Ende wie die C#-VM ({vmAll.Count} Halte)", vmAll.SequenceEqual(cAll),
                            Diff(vmAll, cAll) + "  stderr: " + cErr2));
                    }
            }
            catch (Exception ex) { results.Add(($"VM-Debugger: {c.Name}", false, ex.ToString())); }
            return results;
        }, (_, results) => { foreach (var (title, ok, detail) in results) check(title, ok, detail); });

        // the checks of the protocol that are not about the C of a native build
        RunProtocol(check, cxx, workDir, sanitize, fire);

        // framed over stdio (`fire --debug stdio;...` is the same as FIRE_DEBUG): the plan of a case
        if (TestSelectedName("VM-Debugger: stdio (Rahmen)"))
        {
            var c = Cases.First(x => x.Name == "Schleifen und Aufrufe");
            try
            {
                string? fbc = BuildVm(c.Script, workDir, "vmdbgstdio", out string error);
                if (fbc == null) { check("VM-Debugger: stdio (Rahmen)", false, error); return; }
                var (vmStops, vmOut) = RunVm(c.Script, c.Breakpoints, c.Plan, true);
                var (cStops, cOut, cErr) = RunNative(() => NativeDebugClient.LaunchStdio(fbc, workDir, launcher: fire), c.Breakpoints, c.Plan, true);
                check($"VM-Debugger: stdio (Rahmen): {c.Name}: wie die C#-VM", vmStops.SequenceEqual(cStops) && vmOut.SequenceEqual(cOut),
                    Diff(vmStops, cStops) + $"  Ausgabe C#-VM: {string.Join("|", vmOut)}\n  Ausgabe C-VM: {string.Join("|", cOut)}\n  stderr: {cErr}");
            }
            catch (Exception ex) { check("VM-Debugger: stdio (Rahmen)", false, ex.ToString()); }
        }
    }
}
