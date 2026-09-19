using System;
using System.Collections.Generic;
using ScriptLang.Terminal;

namespace ScriptLang.Terminal.Windows
{
    /// <summary>
    /// Verwaltet Konsolenfenster (ConsoleWindow-Instanzen) über aufsteigende,
    /// eindeutige IDs (siehe IdManager) - jedes Fenster zeigt EINEN
    /// Framebuffer (per ID, siehe FramebufferManager) an. Lebt bewusst in
    /// diesem Windows-Projekt (nicht im plattformunabhängigen Kern), da
    /// ConsoleWindow selbst SDL/Fenster-Handling voraussetzt.
    /// </summary>
    public sealed class WindowManager
    {
        private readonly IdManager<ConsoleWindow> _windows = new();
        private readonly FramebufferManager _framebuffers;

        public WindowManager(FramebufferManager framebuffers)
        {
            _framebuffers = framebuffers ?? throw new ArgumentNullException(nameof(framebuffers));
        }

        /// <summary>Erzeugt UND öffnet sofort ein neues Fenster für den
        /// Framebuffer mit der ID `framebufferId` - anders als
        /// FramebufferManager.CreateFramebuffer/ConsoleManager.CreateConsole
        /// (die nur das C#-Objekt anlegen) macht das hier auch gleich das
        /// eigentliche OS-Fenster sichtbar, da ein unsichtbar erzeugtes
        /// Fenster für den Aufrufer keinen Sinn ergäbe.</summary>
        public int CreateWindow(int framebufferId, string title = "ScriptLang Konsole")
        {
            var fb = _framebuffers.GetFramebuffer(framebufferId);
            var window = new ConsoleWindow(fb);
            window.Open(title);
            return _windows.Create(window);
        }

        /// <summary>Schließt und zerstört das Fenster - bereits zerstörte/
        /// unbekannte IDs sind KEIN Fehler (siehe IdManager.Destroy-Doku).</summary>
        public bool DestroyWindow(int id)
        {
            if (!_windows.TryGet(id, out var window) || window == null) return false;
            window.Dispose();
            return _windows.Destroy(id);
        }

        /// <summary>Für C#-seitige Weiterverwendung - kein Teil des rein-
        /// ID-basierten Oberflächen-APIs.</summary>
        public ConsoleWindow GetWindow(int id) => _windows.Get(id);

        /// <summary>Ein einzelner Zyklus für EIN Fenster (siehe
        /// ConsoleWindow.Tick) - liefert false, wenn das Fenster vom Nutzer
        /// geschlossen wurde (der Aufrufer sollte dann üblicherweise
        /// DestroyWindow(id) nachziehen).</summary>
        public bool Tick(int id) => _windows.Get(id).Tick();

        /// <summary>Bequemlichkeitsmethode: tickt ALLE aktuell verwalteten
        /// Fenster einmal durch und räumt dabei automatisch jedes Fenster
        /// auf, das der Nutzer währenddessen geschlossen hat (kein
        /// manuelles Nachziehen von DestroyWindow nötig). Für einen Host
        /// mit EINER zentralen Schleife, die neben der Skript-VM beliebig
        /// viele Konsolenfenster gleichzeitig offen hält.</summary>
        public void TickAll()
        {
            List<int>? closed = null;
            foreach (int id in _windows.Ids)
            {
                if (!_windows.Get(id).Tick())
                {
                    closed ??= new List<int>();
                    closed.Add(id);
                }
            }
            if (closed == null) return;
            foreach (int id in closed)
                DestroyWindow(id);
        }
    }
}
