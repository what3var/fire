using System.Collections.Generic;
using fire.Bytecode;
using fire.Values;
using fire.Terminal;
using fire.Terminal.Windows;

namespace fire.Terminal.Bridge
{
    /// <summary>
    /// Die Brücke zwischen fire und der Grafik-API (siehe docs/
    /// CONSOLE.md): registriert FramebufferManager/ConsoleManager/
    /// WindowManager als native Funktionen (über NativeRegistry.
    /// RegisterGroup, jeweils mit eigenem Namens-Präfix) und liefert dazu
    /// passenden fire-Quelltext (<see cref="PreludeSource"/>), der
    /// diese nativen Funktionen hinter drei gewöhnlichen Klassen
    /// (Framebuffer/Console/Window) versteckt - Skript-Code sieht nie eine
    /// rohe ID, nur normale Objekte mit normalen Methoden.
    ///
    /// Bewusst "grob" gehalten (siehe Anfrage) - nicht jede Manager-Methode
    /// ist abgebildet, nur eine repräsentative Auswahl (Erzeugen/Zerstören
    /// plus die gängigsten Operationen je Ressourcenart). Weitere Methoden
    /// lassen sich nach demselben Muster ergänzen: native Funktion in der
    /// passenden Build*Functions-Methode registrieren, passende fire-
    /// Methode in PreludeSource hinzufügen, die `this.id` automatisch
    /// mitgibt.
    /// </summary>
    public static class GraphicsBridge
    {
        public const string FramebufferPrefix = "__GRPHFb";
        public const string ConsolePrefix = "__GRPHCon";
        public const string WindowPrefix = "__GRPHWin";

        /// <summary>Ungültige/fehlgeschlagene Erzeugung - IdManager vergibt
        /// echte IDs immer ab 1 aufwärts (siehe dort), -1 ist deshalb als
        /// Sentinel sicher von jeder echten ID unterscheidbar. Die
        /// Create-Wrapper unten fangen JEDE Exception aus dem jeweiligen
        /// Manager ab und liefern diesen Sentinel statt die Exception roh
        /// durch die VM durchschlagen zu lassen - PreludeSource prüft
        /// darauf und wirft dafür eine echte, per `try`/`catch` fangbare
        /// HandleUnavailableException (siehe dort).</summary>
        public const int InvalidHandle = -1;

        public static void RegisterAll(
            NativeRegistry natives, FramebufferManager framebuffers, ConsoleManager consoles, WindowManager windows)
        {
            natives.RegisterGroup(FramebufferPrefix, BuildFramebufferFunctions(framebuffers));
            natives.RegisterGroup(ConsolePrefix, BuildConsoleFunctions(consoles));
            natives.RegisterGroup(WindowPrefix, BuildWindowFunctions(windows));
        }

        private static Dictionary<string, NativeFunction> BuildFramebufferFunctions(FramebufferManager mgr)
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args =>
                {
                    try { return Value.MakeInt(mgr.CreateFramebuffer((int)args[0].AsInt(), (int)args[1].AsInt())); }
                    catch { return Value.MakeInt(InvalidHandle); }
                },
                ["Destroy"] = args => Value.MakeBool(mgr.DestroyFramebuffer((int)args[0].AsInt())),
                ["Width"] = args => Value.MakeInt(mgr.GetWidth((int)args[0].AsInt())),
                ["Height"] = args => Value.MakeInt(mgr.GetHeight((int)args[0].AsInt())),
                ["ReadByte"] = args => Value.MakeInt(mgr.ReadByte((int)args[0].AsInt(), (int)args[1].AsInt())),
                ["WriteByte"] = args =>
                {
                    mgr.WriteByte((int)args[0].AsInt(), (int)args[1].AsInt(), (byte)args[2].AsInt());
                    return Value.MakeUndefined();
                },
            };
        }

        private static Dictionary<string, NativeFunction> BuildConsoleFunctions(ConsoleManager mgr)
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args =>
                {
                    try { return Value.MakeInt(mgr.CreateConsole((int)args[0].AsInt())); }
                    catch { return Value.MakeInt(InvalidHandle); }
                },
                ["Destroy"] = args => Value.MakeBool(mgr.DestroyConsole((int)args[0].AsInt())),
                ["Print"] = args =>
                {
                    mgr.Print((int)args[0].AsInt(), args[1].AsString());
                    return Value.MakeUndefined();
                },
                ["Locate"] = args =>
                {
                    mgr.Locate((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt());
                    return Value.MakeUndefined();
                },
                ["Clear"] = args =>
                {
                    mgr.Clear((int)args[0].AsInt());
                    return Value.MakeUndefined();
                },
                ["SetColor"] = args =>
                {
                    mgr.SetColor((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt());
                    return Value.MakeUndefined();
                },
                ["SetPixel"] = args =>
                {
                    mgr.SetPixel((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt(), (int)args[3].AsInt());
                    return Value.MakeUndefined();
                },
                ["GetPixel"] = args => Value.MakeInt(mgr.GetPixel((int)args[0].AsInt(), (int)args[1].AsInt(), (int)args[2].AsInt())),
            };
        }

        private static Dictionary<string, NativeFunction> BuildWindowFunctions(WindowManager mgr)
        {
            return new Dictionary<string, NativeFunction>
            {
                ["Create"] = args =>
                {
                    try { return Value.MakeInt(mgr.CreateWindow((int)args[0].AsInt(), args[1].AsString())); }
                    catch { return Value.MakeInt(InvalidHandle); }
                },
                ["Destroy"] = args => Value.MakeBool(mgr.DestroyWindow((int)args[0].AsInt())),
                ["Tick"] = args => Value.MakeBool(mgr.Tick((int)args[0].AsInt())),
            };
        }

        /// <summary>fire-Quelltext, der die per <see cref="RegisterAll"/>
        /// registrierten nativen Funktionen hinter drei gewöhnlichen Klassen
        /// versteckt - VOR das eigentliche Nutzer-Skript zu setzen (analog
        /// zu Standard.Prelude.Source, siehe Parser.ParseWithPrelude für das
        /// allgemeine Muster). Jede Klasse merkt sich ihre ID als Feld und
        /// gibt sie bei jeder Methode automatisch als erstes Argument an die
        /// zugehörige native Funktion mit - Skript-Code selbst sieht nie
        /// eine rohe ID.</summary>
        public const string PreludeSource = """
            class HandleUnavailableException : Exception {
                string message

                construct(string message) {
                    this.message = message
                }
            }

            class Framebuffer {
                int id

                construct(int width, int height) {
                    this.id = __GRPHFbCreate(width, height)
                    if (this.id == -1) {
                        throw new HandleUnavailableException("Framebuffer konnte nicht erstellt werden.")
                    }
                }

                destruct() {
                    __GRPHFbDestroy(this.id)
                }

                int Width() { return __GRPHFbWidth(this.id) }
                int Height() { return __GRPHFbHeight(this.id) }
                int ReadByte(int offset) { return __GRPHFbReadByte(this.id, offset) }
                WriteByte(int offset, int value) { __GRPHFbWriteByte(this.id, offset, value) }
            }

            class Console {
                int id

                construct(Framebuffer framebuffer) {
                    this.id = __GRPHConCreate(framebuffer.id)
                    if (this.id == -1) {
                        throw new HandleUnavailableException("Konsole konnte nicht erstellt werden.")
                    }
                }

                destruct() {
                    __GRPHConDestroy(this.id)
                }

                Print(string text) { __GRPHConPrint(this.id, text) }
                Locate(int row, int column) { __GRPHConLocate(this.id, row, column) }
                Clear() { __GRPHConClear(this.id) }
                SetColor(int foreground, int background) { __GRPHConSetColor(this.id, foreground, background) }
                SetPixel(int x, int y, int color) { __GRPHConSetPixel(this.id, x, y, color) }
                int GetPixel(int x, int y) { return __GRPHConGetPixel(this.id, x, y) }
            }

            class Window {
                int id

                construct(Framebuffer framebuffer, string title) {
                    this.id = __GRPHWinCreate(framebuffer.id, title)
                    if (this.id == -1) {
                        throw new HandleUnavailableException("Fenster konnte nicht erstellt werden.")
                    }
                }

                destruct() {
                    __GRPHWinDestroy(this.id)
                }

                bool Tick() { return __GRPHWinTick(this.id) }
            }
            """;
    }
}
