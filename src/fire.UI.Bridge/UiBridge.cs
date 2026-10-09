using System.Reflection;

namespace fire.UI.Bridge
{
    /// <summary>
    /// The user interface library `#import "ui"`: elements written purely in fire that draw into a framebuffer and receive their
    /// events from the window of the graphics bridge. There are no native functions, only the source
    /// <see cref="PreludeSource"/>; `ui` requires `graphics` and switches it on itself (see ImportedPreludes).
    ///
    /// Structure: `UI.Root` is the topmost element. Its constructor takes the framebuffer to draw into and the window the events
    /// (mouse, keyboard, text) come from; it creates the renderer for drawing itself and switches the event queue on there (`Window.EnableEvents`).
    /// `Root.Tick()` arranges the interface and draws it, lets the window run one cycle (fetch events, show the framebuffer) and
    /// processes the events that have arrived - every loop calls it once.
    ///
    /// Elements live in containers (see docs/UI.md). An element belongs to its container (`Add` calls `TakeTo`): the interface thus lives as long as its root
    /// lives, even if it was built in a helper function. You react to clicks with a lambda (`button.onClick = func () => { ... }`; it runs in the
    /// main program and sees the real global variables) or by polling in your own loop (`if (button.TakeClicked()) { ... }`). Drawing is done with the
    /// `Renderer` of the graphics bridge: areas with a `Brush`, frames and lines with a `Pen`, text with a brush.
    ///
    /// The source is in <c>ui/*.fire</c> (embedded, ordered by file name): so it can be edited and checked with the editor.
    /// </summary>
    public static partial class UiBridge
    {
        /// <summary>The source of the library (`namespace UI`), to be put BEFORE the user's script when there is `#import "ui"`.</summary>
        public static string PreludeSource { get; } = ReadSource();

        private static string ReadSource()
        {
            var assembly = typeof(UiBridge).Assembly;
            var parts = assembly.GetManifestResourceNames()
                .Select(name => (Name: name.Replace('\\', '/'), Actual: name))
                .Where(n => n.Name.StartsWith("ui/", StringComparison.Ordinal) && n.Name.EndsWith(".fire", StringComparison.Ordinal))
                .OrderBy(n => n.Name, StringComparer.Ordinal)
                .Select(n =>
                {
                    using var reader = new StreamReader(assembly.GetManifestResourceStream(n.Actual)!);
                    return reader.ReadToEnd();
                });
            return string.Join("\n", parts);
        }
    }
}
