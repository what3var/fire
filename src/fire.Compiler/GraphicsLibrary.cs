using System.Runtime.InteropServices;
using fire.Bytecode;
using fire.Package.Manager;
using fire.Runtime;

namespace fire.Compiler
{
    /// <summary>
    /// `graphics` and `windows` in the virtual machine through their C implementation (docs/spec/UI_PLATFORM.md, part 2): the same bridges and the same display as a native build,
    /// as one shared library (PackageLibrary.EnsureGraphics) whose natives are bound like those of a package (PackageNativeBinding). The names are registered in the order of the
    /// compiler's stubs (GraphicsBridge, WindowsBridge: the calls address natives by index).
    /// </summary>
    public static partial class GraphicsLibrary
    {
        /// <summary>Does the VM take the C implementation? FIRE_VM_GRAPHICS=cs keeps the C# one (fire.Terminal), =c asks for C; the default is C - except where the host
        /// renders the windows itself (the editor's previews: `hostRenders`), which still needs the C# one.</summary>
        public static bool Wanted(bool hostRenders)
        {
            string? choice = Environment.GetEnvironmentVariable("FIRE_VM_GRAPHICS");
            if (choice == "cs") return false;
            if (choice == "c") return true;
            return !hostRenders;
        }

        private static string? _path;
        private static string? _failure;

        /// <summary>The library on this machine, built now if needed (null: it cannot be built - <see cref="Failure"/> says why).</summary>
        public static string? Locate(Action<string>? log = null)
        {
            if (_path != null && File.Exists(_path)) return _path;
            var graphics = PackageStore.Default.FindImport("graphics");
            var windows = PackageStore.Default.FindImport("windows");
            if (graphics == null || windows == null) { _failure = "the standard packages graphics and windows are not installed"; return null; }
            var say = log ?? (m => (fire.Native.ToolchainProvider.Log ?? Console.Error.WriteLine)(m));
            try { _path = PackageLibrary.EnsureGraphics(graphics, windows, say); _failure = null; }
            catch (PackageException ex)
            {
                // (into the log - spark's build log: the program's windows cannot open without this library; a build monitor has had the compiler's output line by line already)
                _failure = ex.Message; _path = null;
                say("error: " + (fire.Native.BuildMonitor.Current != null ? ex.Message.Split('\n')[0].TrimEnd() : ex.Message));
            }
            return _path;
        }

        /// <summary>Why the library cannot be had (null: it can).</summary>
        public static string? Failure => _failure;
    }

    /// <summary>The host drives the windows of the program (RuntimeSession.Build): <see cref="Display"/> gets their pictures and gives their events; null: windows
    /// without a screen (the design view: the pictures stay in the framebuffers).</summary>
    public sealed record HostWindows(IHostDisplay? Display);

    /// <summary>One event a host hands to a program's window (<see cref="IHostDisplay.NextEvent"/>): <see cref="Type"/> as EventType (mouse, keys, text, resize, close);
    /// positions in pixels of the window. MouseDown/Up: Button, X, Y; MouseMove: X, Y (the buttons the window keeps itself); MouseScroll: Dx, Dy (notches, Dy &gt; 0: up), X, Y;
    /// KeyDown/Up: KeyCode, Modifiers (the numbers of UI.Keys); TextInput: Text; Resize: Width, Height; Touch*: Finger, X, Y, Pressure; Joystick*: Joystick, Index, Value;
    /// DropFiles/DropText: Data, X, Y; DragHover: X, Y; DragLeave; Close.</summary>
    public struct HostDisplayEvent
    {
        public int Type, Button, Buttons, KeyCode, Modifiers, Width, Height;
        public float X, Y, Dx, Dy;
        public string? Text;
        /// <summary>TouchDown/Move/Up: the finger and how hard it presses (0 to 1).</summary>
        public long Finger;
        public float Pressure;
        /// <summary>Joystick events: the device, the axis/button/hat and its value.</summary>
        public int Joystick, Index;
        public float Value;
        /// <summary>DropFiles (the paths, one per line) / DropText: what was dropped.</summary>
        public string? Data;
    }

    /// <summary>A host that drives the windows of a program running on the C library (<see cref="GraphicsLibrary.Instance"/>): the editor's live view. Called on the thread of
    /// the window (the windows' manager), not the host's.</summary>
    public interface IHostDisplay
    {
        /// <summary>The next event for window <paramref name="window"/> (1, 2, ...: in the order the program opened them); false: none now.</summary>
        bool NextEvent(int window, out HostDisplayEvent e);
        /// <summary>A picture of the window (R,G,B,A per word, row by row; only valid during the call). With <paramref name="vsync"/>, the host waits like the vertical blank.</summary>
        void Present(int window, ReadOnlySpan<uint> rgba, int width, int height, bool vsync);
        /// <summary>The mouse pointer the program wants over the window (the numbering of UI.Cursor).</summary>
        void Cursor(int window, int cursor);
        /// <summary>Window.StartDrag: the program drags text and/or files out of the window - 0 nothing happened, 1 copied, 2 moved.</summary>
        int StartDrag(int window, string text, string[] files) => 0;
        /// <summary>Window.CanStartDrag: does the host take drags out of the window?</summary>
        bool CanStartDrag(int window) => false;
    }
}
