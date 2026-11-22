using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

/// <summary>The temporary folders a run of the tests made for itself (the standard packages it installs, the test font): removed when the process ends - normally,
/// after a `return` of a FIRE_TESTS_ONLY group, after an exception nothing catches, at Ctrl+C or SIGTERM. A folder another run made is never touched.</summary>
static class TestTempFolders
{
    private static readonly List<string> Folders = new();
    private static bool _hooked;
    private static PosixSignalRegistration? _sigint, _sigterm;

    public static void Add(string folder)
    {
        lock (Folders)
        {
            Folders.Add(folder);
            if (_hooked) return;
            _hooked = true;
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RemoveAll();
        try
        {
            // (the signal still ends the process - context.Cancel stays false - once the folders are gone)
            _sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => RemoveAll());
            _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => RemoveAll());
        }
        catch (PlatformNotSupportedException) { }
    }

    public static void RemoveAll()
    {
        string[] folders;
        lock (Folders) { folders = Folders.ToArray(); Folders.Clear(); }
        foreach (var folder in folders)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"(the temporary folder {folder} could not be removed: {ex.Message})"); }
        }
    }
}
