using System.Globalization;
using System.Text;

namespace fire.UI.Markup
{
    /// <summary>A circuit of Paul Falstad's simulator (the text of File > Export As Text, or of a `.txt` file) as a `.biscuit` schematic (docs/CIRCUITS.md). The grid of the
    /// schematic is 8 pixels of Falstad's, the elements keep their points, so the drawing stays the same. Elements the package `circuits` does not have are left out
    /// and named in <see cref="Result.Warnings"/>.</summary>
    public static class FalstadImport
    {
        public sealed class Result
        {
            public string Biscuit { get; init; } = "";
            public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
            public int Elements { get; init; }
        }

        private sealed class Element
        {
            public required string Type { get; init; }
            public int X1, Y1, X2, Y2;
            public string Name = "";
            /// <summary>The second point when the package wants another one than Falstad's (a transformer, a chip).</summary>
            public (int X, int Y)? Fix;
            public readonly List<KeyValuePair<string, string>> Props = new();
            public void Set(string key, string value) => Props.Add(new(key, value));
        }

        /// <summary>Converts the text; `className` goes into the file (empty: the class is named after the file).</summary>
        public static Result Convert(string text, string className = "")
        {
            var warnings = new List<string>();
            var elements = new List<Element?>();   // by the index Falstad gives them (null: left out), for the scopes
            var scopes = new List<(int Index, int Value)>();
            string timeStep = "";
            int lineNo = 0;
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                lineNo++;
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var t = line.Split(new[] { ' ', '+', '\t', '\f' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length == 0) continue;
                string type = t[0];
                try
                {
                    if (type == "$")
                    {
                        if (t.Length > 2 && double.TryParse(t[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double dt) && dt > 0) timeStep = Format(dt) + "s";
                        continue;
                    }
                    if (type == "o")
                    {
                        if (t.Length > 3 && int.TryParse(t[1], out int index)) scopes.Add((index, int.TryParse(t[3], out int v) ? v : 0));
                        continue;
                    }
                    if (type is "h" or "!" or "%" or "?" or "B" or "." or "34" or "32" or "38") continue;   // hints, models, sliders, subcircuits
                    if (t.Length < 6) { warnings.Add($"Line {lineNo}: '{line}' is no element."); continue; }
                    int x1 = Grid(t[1]), y1 = Grid(t[2]), x2 = Grid(t[3]), y2 = Grid(t[4]);
                    int flags = int.TryParse(t[5], out int f) ? f : 0;
                    var e = Make(type, t, flags, warnings, lineNo);
                    if (e != null)
                    {
                        e.X1 = x1; e.Y1 = y1; e.X2 = x2; e.Y2 = y2;
                        if (e.Fix is { } fix)
                        {
                            if (e.Type == "JKFlipFlop") { e.X2 = x1 + fix.X; e.Y2 = y1; }
                            else { e.X2 = Grid8(fix.X); e.Y2 = Grid8(fix.Y); }
                        }
                    }
                    elements.Add(e);
                }
                catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
                {
                    warnings.Add($"Line {lineNo}: '{line}' could not be read.");
                    elements.Add(null);
                }
            }

            // names for the elements that scopes look at, and the scopes below the drawing
            var present = elements.Where(e => e != null).Cast<Element>().ToList();
            int bottom = present.Count == 0 ? 0 : present.Max(e => Math.Max(e.Y1, e.Y2)) + 6;
            int left = present.Count == 0 ? 0 : present.Min(e => Math.Min(e.X1, e.X2));
            int n = 0;
            foreach (var (index, value) in scopes)
            {
                if (index < 0 || index >= elements.Count || elements[index] is not { } target) { warnings.Add("A scope looks at an element that was left out."); continue; }
                if (target.Name == "") target.Name = UniqueName(present, target.Type == "Wire" ? "W" : Prefix(target.Type));
                var scope = new Element { Type = "Scope", X1 = left + n * 34, Y1 = bottom, X2 = left + n * 34 + 32, Y2 = bottom + 16 };
                scope.Set("probe", target.Name);
                if (value == 1) scope.Set("quantity", "current");
                present.Add(scope);
                n++;
            }

            var sb = new StringBuilder();
            sb.Append("{\n  \"biscuit\": 1");
            if (className != "") sb.Append(",\n  \"class\": ").Append(Json(className));
            if (timeStep != "") sb.Append(",\n  \"timeStep\": ").Append(Json(timeStep));
            sb.Append(",\n  \"elements\": [");
            for (int i = 0; i < present.Count; i++)
            {
                var e = present[i];
                sb.Append(i == 0 ? "\n    " : ",\n    ");
                sb.Append("{\"type\": ").Append(Json(e.Type));
                if (e.Name != "") sb.Append(", \"name\": ").Append(Json(e.Name));
                sb.Append($", \"from\": [{e.X1}, {e.Y1}], \"to\": [{e.X2}, {e.Y2}]");
                foreach (var p in e.Props) sb.Append(", ").Append(Json(p.Key)).Append(": ").Append(Json(p.Value));
                sb.Append('}');
            }
            sb.Append("\n  ]\n}\n");
            return new Result { Biscuit = sb.ToString(), Warnings = warnings, Elements = present.Count };
        }

        private static Element? Make(string type, string[] t, int flags, List<string> warnings, int lineNo)
        {
            string Tok(int i) => i < t.Length ? t[i] : "";
            double Num(int i, double fallback = 0) => i < t.Length && double.TryParse(t[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
            switch (type)
            {
                case "w": return new Element { Type = "Wire" };
                case "r": { var e = new Element { Type = "Resistor" }; e.Set("resistance", Format(Num(6, 1000))); return e; }
                case "c": { var e = new Element { Type = "Capacitor" }; e.Set("capacitance", Format(Num(6, 1e-5))); return e; }
                case "l": { var e = new Element { Type = "Inductor" }; e.Set("inductance", Format(Num(6, 1))); return e; }
                case "v":
                case "R":
                {
                    var e = new Element { Type = type == "v" ? "Source" : "Rail" };
                    int wf = (int)Num(6);
                    double freq = Num(7, 40), max = Num(8, 5), bias = Num(9), phase = Num(10), duty = Num(11, 0.5);
                    if (wf == 0) e.Set("voltage", Format(max));
                    else if (wf == 1)
                    {
                        e.Set("waveform", "sine");
                        e.Set("voltage", Format(max));
                        e.Set("frequency", Format(freq));
                        if (bias != 0) e.Set("offset", Format(bias));
                        if (phase != 0) e.Set("phase", Format(phase));
                    }
                    else
                    {
                        // square (and the curves the package has not got: triangle, sawtooth, pulse, noise) as a square of the same levels
                        if (wf != 2) warnings.Add($"Line {lineNo}: a {WaveName(wf)} source is imported as a square wave.");
                        e.Set("waveform", "square");
                        e.Set("voltage", Format(bias + max));
                        e.Set("offset", Format(bias - max));
                        e.Set("frequency", Format(freq));
                        if (wf == 5 && duty != 0.5) e.Set("duty", Format(duty));
                    }
                    return e;
                }
                case "i": { var e = new Element { Type = "CurrentSource" }; e.Set("current", Format(Num(6, 0.01))); return e; }
                case "g": return new Element { Type = "Ground" };
                case "s":
                {
                    var e = new Element { Type = "Switch" };
                    string pos = Tok(6);
                    bool closed = pos == "true" || pos == "0";
                    if (closed) e.Set("closed", "true");
                    if (Tok(7) == "true") e.Set("momentary", "true");
                    return e;
                }
                case "d":
                {
                    var e = new Element { Type = "Diode" };
                    string model = (flags & 2) != 0 ? Tok(6) : "";
                    e.Set("model", KnownDiode(model));
                    return e;
                }
                case "z": { var e = new Element { Type = "Zener" }; e.Set("voltage", Format((flags & 2) != 0 ? 5.6 : Num((flags & 1) != 0 ? 7 : 6, 5.6))); return e; }
                case "162":
                {
                    var e = new Element { Type = "Led" };
                    // after the flags: (a model name with flag 2, a forward drop with flag 1), then R G B of the colour
                    int at = (flags & 3) != 0 ? 7 : 6;
                    e.Set("color", LedColor(Num(at, 1), Num(at + 1, 0), Num(at + 2, 0)));
                    return e;
                }
                case "t":
                {
                    var e = new Element { Type = "Transistor" };
                    e.Set("model", Tok(6) == "-1" ? "pnp" : "npn");
                    if ((flags & 1) != 0) e.Set("flip", "true");
                    return e;
                }
                case "f":
                {
                    var e = new Element { Type = "Mosfet" };
                    e.Set("model", (flags & 1) != 0 ? "pmos" : "nmos");
                    if ((flags & 8) != 0) e.Set("flip", "true");
                    return e;
                }
                case "a":
                {
                    var e = new Element { Type = "OpAmp" };
                    e.Set("model", "opamp");
                    if ((flags & 1) != 0) e.Set("flip", "true");
                    if ((flags & 2) != 0) warnings.Add($"Line {lineNo}: a small op amp has its inputs 1 unit from the middle in Falstad's simulator; check its connections.");
                    return e;
                }
                case "401": { var e = new Element { Type = "Comparator" }; if ((flags & 1) != 0) e.Set("flip", "true"); return e; }
                case "150": case "151": case "152": case "153": case "154":
                {
                    string gate = type switch { "150" => "And", "151" => "Nand", "152" => "Or", "153" => "Nor", _ => "Xor" };
                    var e = new Element { Type = gate };
                    int inputs = (int)Num(6, 2);
                    if (inputs != 2) e.Set("inputs", inputs.ToString(CultureInfo.InvariantCulture));
                    if ((flags & 1) != 0) warnings.Add($"Line {lineNo}: a small gate has its inputs closer together in Falstad's simulator; check its connections.");
                    return e;
                }
                case "I": return new Element { Type = "Not" };
                case "L":
                {
                    var e = new Element { Type = "LogicInput" };
                    string pos = Tok(6);
                    if (pos == "1" || pos == "true") e.Set("high", "true");
                    double hi = Num(8, 5);
                    if (hi != 5) e.Set("supply", Format(hi));
                    return e;
                }
                case "M": { var e = new Element { Type = "LogicOutput" }; double th = Num(6, 2.5); if (th != 2.5) e.Set("threshold", Format(th)); return e; }
                case "p": return new Element { Type = "Probe" };
                case "207":
                {
                    var e = new Element { Type = "Label" };
                    e.Set("text", Unescape(string.Join(" ", t.Skip(6))));
                    return e;
                }
                case "x":
                {
                    var e = new Element { Type = "Text" };
                    e.Set("text", Unescape(string.Join(" ", t.Skip(7))));
                    return e;
                }
                case "174":
                {
                    // the points stay: the second one says where the wiper is (as in the package)
                    var e = new Element { Type = "Potentiometer" };
                    e.Set("resistance", Format(Num(6, 1000)));
                    double pos = Num(7, 0.5);
                    if (pos != 0.5) e.Set("position", Format(pos));
                    return e;
                }
                case "T":
                {
                    var e = new Element { Type = "Transformer" };
                    e.Set("inductance", Format(Num(6, 4)));
                    double ratio = Num(7, 1);
                    if (ratio != 1) e.Set("ratio", Format(ratio));
                    double k = Num(10, 0.999);
                    if (k != 0.999) e.Set("coupling", Format(k));
                    if ((flags & 4) != 0) e.Set("reverse", "true");
                    if (Num(11) > 0) warnings.Add($"Line {lineNo}: the saturation of a transformer is not simulated.");
                    // the width (Falstad keeps it in the second point) and the side of the secondary: the points are set after Make (see Fix)
                    int x1 = Pixels(t[1]), y1 = Pixels(t[2]), x2 = Pixels(t[3]), y2 = Pixels(t[4]);
                    bool vertical = (flags & 8) != 0;
                    int width = vertical ? -Math.Max(32, Math.Abs(x2 - x1)) : Math.Max(32, Math.Abs(y2 - y1));
                    int dsF = (y2 - y1) == 0 ? Math.Sign(x2 - x1) : Math.Sign(y2 - y1);
                    int flip = (flags & 16) != 0 ? -1 : 1;
                    int ex = vertical ? x1 : x2, ey = vertical ? y2 : y1;
                    int dsO = (ey - y1) == 0 ? Math.Sign(ex - x1) : Math.Sign(ey - y1);
                    if (dsO == 0) dsO = 1;
                    if (dsF == 0) dsF = 1;
                    int w = (int)Math.Round(dsF * width * flip / (8.0 * dsO));
                    if (w != 4) e.Set("width", w.ToString(CultureInfo.InvariantCulture));
                    e.Fix = (vertical ? x1 : x2, vertical ? y2 : y1);
                    return e;
                }
                case "156":
                {
                    // a chip: the pins go down from (x, y), 32 pixels apart (16 for a small one), the right side 96 (48) pixels to the right
                    var e = new Element { Type = "JKFlipFlop" };
                    bool small = (flags & 1) != 0;
                    if ((flags & 4) == 0) e.Set("falling", "true");
                    if ((flags & 2) != 0) e.Set("reset", "true");
                    if ((flags & 8) != 0) warnings.Add($"Line {lineNo}: the inverted reset of a JK flip-flop is imported as an active-high reset.");
                    if ((flags & (7 << 10)) != 0) warnings.Add($"Line {lineNo}: a flipped JK flip-flop is imported unflipped; check its connections.");
                    if ((flags & (1 << 13)) != 0) { double hv = Num(6, 5); if (hv != 5) e.Set("supply", Format(hv)); }
                    if (!small) e.Set("spacing", "4");
                    e.Fix = (small ? 6 : 12, 0);   // (the width: the second point is set from the first)
                    return e;
                }
                case "182":
                case "183":
                {
                    var e = new Element { Type = "SchmittTrigger" };
                    if (type == "183") e.Set("inverting", "true");
                    if (t.Length > 9)
                    {
                        double lo = Num(7, 1.66), hi = Num(8, 3.33), on = Num(9, 5), off = Num(10, 0);
                        if (lo != 1.66) e.Set("lower", Format(lo));
                        if (hi != 3.33) e.Set("upper", Format(hi));
                        if (on != 5) e.Set("supply", Format(on));
                        if (off != 0) warnings.Add($"Line {lineNo}: a Schmitt trigger with a low level other than 0 V is imported with 0 V.");
                    }
                    return e;
                }
                case "178":
                {
                    var e = new Element { Type = "Relay" };
                    int poles = (int)Num(6, 1);
                    if (poles > 1) warnings.Add($"Line {lineNo}: a relay with {poles} poles is imported with its first pole only.");
                    double l = Num(7, 0.2), ron = Num(9, 0.05), onI = Num(11, 0.02), coilR = Num(12, 20), offI = Num(13, onI);
                    if (l != 0.2) e.Set("inductance", Format(l));
                    if (coilR != 20) e.Set("coilResistance", Format(coilR));
                    if (onI != 0.02) e.Set("onCurrent", Format(onI));
                    if (offI != 0.015) e.Set("offCurrent", Format(offI));
                    if (ron != 0.05) warnings.Add($"Line {lineNo}: the contact resistance of a relay is 0.05 Ohm in the package.");
                    // the coil: on both sides (flag 4), at the second end (flag 1), else at the first
                    e.Set("coil", (flags & 4) != 0 ? "both" : (flags & 1) != 0 ? "end" : "start");
                    if ((flags & 8) != 0) e.Set("flip", "true");
                    return e;
                }
                case "O":
                {
                    warnings.Add($"Line {lineNo}: an output is imported as a logic output.");
                    return new Element { Type = "LogicOutput" };
                }
                default:
                    warnings.Add($"Line {lineNo}: the element '{type}' ({Describe(type)}) is not in the package circuits and was left out.");
                    return null;
            }
        }

        private static string Describe(string type) => type switch
        {
            "155" => "D flip-flop", "165" => "555 timer", "164" => "counter", "S" => "two-way switch", "j" => "JFET",
            "181" => "lamp", "A" => "antenna", "m" => "memristor", "170" => "sweep", "171" => "tapped transformer", "200" => "AM source", "201" => "FM source",
            _ => "unknown",
        };

        private static string WaveName(int wf) => wf switch { 3 => "triangle", 4 => "sawtooth", 5 => "pulse", 6 => "noise", 7 => "variable", _ => "special" };

        private static string KnownDiode(string model)
        {
            foreach (var known in new[] { "1N4148", "1N4001", "1N5817", "1N4733", "1N4742" })
                if (string.Equals(model, known, StringComparison.OrdinalIgnoreCase)) return known;
            return "diode";
        }

        private static string LedColor(double r, double g, double b)
        {
            if (r > 0.7 && g > 0.7 && b > 0.7) return "white";
            if (b > 0.7 && r < 0.5) return "blue";
            if (r > 0.7 && g > 0.6) return "yellow";
            if (g > 0.7 && r < 0.5) return "green";
            return "red";
        }

        private static string Prefix(string type) => type switch
        {
            "Resistor" => "R", "Capacitor" => "C", "Inductor" => "L", "Source" or "Rail" => "V", "CurrentSource" => "I", "Diode" or "Zener" or "Led" => "D",
            "Transistor" or "Mosfet" => "Q", "Switch" => "S", "OpAmp" or "Comparator" or "SchmittTrigger" or "JKFlipFlop" => "U", "Probe" => "P",
            "Potentiometer" => "RV", "Transformer" => "T", "Relay" => "K", _ => "X",
        };

        private static string UniqueName(List<Element> elements, string prefix)
        {
            for (int i = 1; ; i++)
            {
                string name = prefix + i.ToString(CultureInfo.InvariantCulture);
                if (!elements.Any(e => e.Name == name)) return name;
            }
        }

        // Falstad's pixels to units of the grid (8 pixels)
        private static int Grid(string s) => (int)Math.Round(int.Parse(s, CultureInfo.InvariantCulture) / 8.0);
        private static int Grid8(int pixels) => (int)Math.Round(pixels / 8.0);
        private static int Pixels(string s) => int.Parse(s, CultureInfo.InvariantCulture);

        private static string Unescape(string s) => s.Replace("\\s", " ").Replace("\\p", "+").Replace("%2B", "+").Replace("%2b", "+").Replace("\\\\", "\\");

        /// <summary>A number with an SI prefix ("4.7k", "10u").</summary>
        public static string Format(double v)
        {
            if (v == 0) return "0";
            string[] prefixes = { "p", "n", "u", "m", "", "k", "M", "G" };
            double[] factors = { 1e-12, 1e-9, 1e-6, 1e-3, 1, 1e3, 1e6, 1e9 };
            double a = Math.Abs(v);
            int k = 4;
            for (int i = 0; i < factors.Length; i++) if (a >= factors[i] * 0.9999999) k = i;
            if (a < 1e-12) k = 0;
            return Math.Round(v / factors[k], 6).ToString("0.######", CultureInfo.InvariantCulture) + prefixes[k];
        }

        private static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < ' ') sb.Append($"\\u{(int)c:x4}"); else sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
