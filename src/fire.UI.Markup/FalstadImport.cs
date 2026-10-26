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
            bool smallGrid = false;   // Falstad's grid: 16 pixels, 8 with flag 2 of the options
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
                        if (t.Length > 1 && int.TryParse(t[1], out int options)) smallGrid = (options & 2) != 0;
                        continue;
                    }
                    if (type == "o")
                    {
                        if (t.Length > 3 && int.TryParse(t[1], out int index)) scopes.Add((index, int.TryParse(t[3], out int v) ? v : 0));
                        continue;
                    }
                    if (type is "h" or "!" or "%" or "?" or "B" or "." or "34" or "32" or "38") continue;   // hints, models, sliders, subcircuits
                    // old files give the letters of the types as numbers ("82" is 'R')
                    if (int.TryParse(type, NumberStyles.None, CultureInfo.InvariantCulture, out int code) && code >= 65 && code < 127 && char.IsLetter((char)code))
                        type = ((char)code).ToString();
                    if (t.Length < 6) { warnings.Add($"Line {lineNo}: '{line}' is no element."); continue; }
                    int x1 = Grid(t[1]), y1 = Grid(t[2]), x2 = Grid(t[3]), y2 = Grid(t[4]);
                    int flags = int.TryParse(t[5], out int f) ? f : 0;
                    var e = Make(type, t, flags, warnings, lineNo);
                    if (e != null)
                    {
                        e.X1 = x1; e.Y1 = y1; e.X2 = x2; e.Y2 = y2;
                        // the gate of a thyristor or triac lies on Falstad's grid
                        if (smallGrid && e.Type is "Thyristor" or "Triac") e.Set("smallGrid", "true");
                        if (e.Fix is { } fix)
                        {
                            if (e.Type is "JKFlipFlop" or "VCVS" or "VCCS" or "CCCS" or "CCVS" or "CurrentConveyor" or "Optocoupler") { e.X2 = x1 + fix.X; e.Y2 = y1; }
                            else if (e.Type == "Triode")
                            {
                                // Falstad's side of plate and cathode, against the one the package takes from the direction
                                int dsign = y2 == y1 ? Math.Sign(x2 - x1) : Math.Sign(y2 - y1);
                                if (dsign == 0) dsign = 1;
                                if (fix.X != dsign) e.Set("flip", "true");
                            }
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
                    else if (wf == 3 || wf == 4)
                    {
                        e.Set("waveform", wf == 3 ? "triangle" : "sawtooth");
                        e.Set("voltage", Format(max));
                        e.Set("frequency", Format(freq));
                        if (bias != 0) e.Set("offset", Format(bias));
                        if (phase != 0) e.Set("phase", Format(phase));
                    }
                    else if (wf == 6)
                    {
                        e.Set("waveform", "noise");
                        e.Set("voltage", Format(max));
                        if (bias != 0) e.Set("offset", Format(bias));
                    }
                    else if (wf == 2 || wf == 5)
                    {
                        // a square between bias - max and bias + max; a pulse between bias and bias + max (its duty cycle)
                        e.Set("waveform", "square");
                        e.Set("voltage", Format(bias + max));
                        e.Set("offset", Format(wf == 5 ? bias : bias - max));
                        e.Set("frequency", Format(freq));
                        if (wf == 5 && duty != 0.5) e.Set("duty", Format(duty));
                    }
                    else
                    {
                        warnings.Add($"Line {lineNo}: a {WaveName(wf)} source is imported as DC.");
                        e.Set("voltage", Format(bias + max));
                    }
                    return e;
                }
                case "425":
                {
                    var e = new Element { Type = "RelayCoil" };
                    e.Set("label", Unescape(Tok(6)));
                    e.Set("inductance", Format(Num(7, 0.2)));
                    e.Set("onCurrent", Format(Num(9, 0.02)));
                    e.Set("coilResistance", Format(Num(10, 20)));
                    e.Set("offCurrent", Format(Num(11, 0.015)));
                    int mode = (int)Num(13);
                    string[] modes = { "normal", "onDelay", "offDelay", "latching", "set", "reset" };
                    if (mode > 0 && mode < modes.Length) e.Set("mode", modes[mode]);
                    if (mode == 1 || mode == 2) e.Set("delay", Format(Num(12, 0.005)));
                    return e;
                }
                case "426":
                {
                    var e = new Element { Type = "RelayContact" };
                    e.Set("label", Unescape(Tok(6)));
                    e.Set("onResistance", Format(Num(7, 0.05)));
                    e.Set("offResistance", Format(Num(8, 1e6)));
                    if ((flags & 2) != 0) e.Set("normallyClosed", "true");
                    return e;
                }
                case "n":
                {
                    // a rail of noise
                    var e = new Element { Type = "Rail" };
                    e.Set("waveform", "noise");
                    e.Set("voltage", Format(Num(8, 5)));
                    if (Num(9) != 0) e.Set("offset", Format(Num(9)));
                    return e;
                }
                case "172":
                {
                    // a rail with a slider: the voltage it was set to
                    var e = new Element { Type = "Rail" };
                    e.Set("voltage", Format(Num(7, 5)));
                    return e;
                }
                case "170":
                {
                    var e = new Element { Type = "Sweep" };
                    e.Set("minFrequency", Format(Num(6, 20)));
                    e.Set("maxFrequency", Format(Num(7, 4000)));
                    e.Set("amplitude", Format(Num(8, 5)));
                    e.Set("sweepTime", Format(Num(9, 0.1)));
                    if ((flags & 1) != 0) e.Set("logarithmic", "true");
                    if ((flags & 2) == 0) e.Set("bidirectional", "false");
                    return e;
                }
                case "S":
                {
                    var e = new Element { Type = "Switch2" };
                    int pos = (int)Num(6);
                    int throws = (int)Num(9, 2);
                    if (pos != 0) e.Set("position", pos.ToString(CultureInfo.InvariantCulture));
                    if (throws != 2) e.Set("throws", throws.ToString(CultureInfo.InvariantCulture));
                    if ((flags & 1) != 0) e.Set("centerOff", "true");
                    int link = (int)Num(8);
                    if (link != 0) e.Set("link", link.ToString(CultureInfo.InvariantCulture));
                    if (Tok(7) == "true") warnings.Add($"Line {lineNo}: a momentary two-way switch is imported as a normal one.");
                    return e;
                }
                case "181":
                {
                    var e = new Element { Type = "Lamp" };
                    e.Set("power", Format(Num(7, 100)));
                    e.Set("voltage", Format(Num(8, 120)));
                    e.Set("warmTime", Format(Num(9, 0.4)));
                    e.Set("coolTime", Format(Num(10, 0.4)));
                    return e;
                }
                case "404":
                {
                    var e = new Element { Type = "Fuse" };
                    e.Set("resistance", Format(Num(6, 0.0613)));
                    e.Set("i2t", Format(Num(7, 6.73)));
                    return e;
                }
                case "m":
                {
                    var e = new Element { Type = "Memristor" };
                    e.Set("onResistance", Format(Num(6, 100)));
                    e.Set("offResistance", Format(Num(7, 16000)));
                    e.Set("width", Format(Num(9, 1e-8)));
                    e.Set("mobility", Format(Num(10, 1e-10)));
                    return e;
                }
                case "187":
                {
                    var e = new Element { Type = "SparkGap" };
                    e.Set("onResistance", Format(Num(6, 1000)));
                    e.Set("offResistance", Format(Num(7, 1e9)));
                    e.Set("breakdown", Format(Num(8, 1000)));
                    e.Set("holdCurrent", Format(Num(9, 0.001)));
                    return e;
                }
                case "374":
                {
                    // Falstad's light: 0.1 + 10000 * position lux
                    var e = new Element { Type = "LightResistor" };
                    e.Set("lux", Format(10000 * Num(6, 0.5) + 0.1));
                    return e;
                }
                case "350":
                {
                    // R25, R50 (the B value between them), the range of the slider and its position
                    var e = new Element { Type = "Thermistor" };
                    double r25 = Num(6, 10000), r50 = Num(7, 3588), lo = Num(8, -40), hi = Num(9, 150), pos = Num(10, 0.34);
                    double beta = (Math.Log(r25) - Math.Log(r50)) / (1 / 298.15 - 1 / 323.15);
                    e.Set("resistance25", Format(r25));
                    e.Set("beta", Format(Math.Round(beta)));
                    e.Set("temperature", Format(Math.Round(pos * (hi - lo) + lo)));
                    return e;
                }
                case "209": { var e = new Element { Type = "Capacitor" }; e.Set("capacitance", Format(Num(6, 1e-5))); return e; }
                case "171":
                {
                    var e = new Element { Type = "TransmissionLine" };
                    e.Set("delay", Format(Num(6, 1e-6)));
                    e.Set("impedance", Format(Num(7, 75)));
                    int w = Grid8((int)Num(8, 32));
                    if (w != 4 && w > 0) e.Set("width", w.ToString(CultureInfo.InvariantCulture));
                    return e;
                }
                case "159":
                case "160":
                {
                    var e = new Element { Type = type == "159" ? "AnalogSwitch" : "AnalogSwitch2" };
                    if (t.Length > 7)
                    {
                        e.Set("onResistance", Format(Num(6, 20)));
                        e.Set("offResistance", Format(Num(7, 1e10)));
                    }
                    if (t.Length > 8) e.Set("threshold", Format(Num(8, 2.5)));
                    if ((flags & 1) != 0) { if (type == "159") e.Set("inverting", "true"); else e.Set("swap", "true"); }
                    int flips = ((flags & 4) != 0 ? 1 : 0) + ((flags & 8) != 0 ? 1 : 0) + ((flags & 16) != 0 ? 1 : 0);
                    if (flips % 2 == 1) e.Set("flip", "true");
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
                case "j":
                {
                    var e = new Element { Type = "Jfet" };
                    if ((flags & 1) != 0) e.Set("pChannel", "true");
                    if ((flags & 8) != 0) e.Set("flip", "true");
                    double vt = Num(6, -4), beta = Num(7, 0.00125);
                    if (vt != -4) e.Set("threshold", Format(vt));
                    if (beta != 0.00125) e.Set("beta", Format(beta));
                    return e;
                }
                case "400":
                {
                    var e = new Element { Type = "Darlington" };
                    e.Set("model", Tok(t.Length - 1) == "-1" ? "pnp" : "npn");
                    return e;
                }
                case "177":
                {
                    var e = new Element { Type = "Thyristor" };
                    if ((flags & 1) == 0) e.Set("legacy", "true");
                    double trig = Num(8, 0.01), hold = Num(9, 0.0082), gr = Num(10, 50);
                    if (trig != 0.01) e.Set("triggerCurrent", Format(trig));
                    if (hold != 0.0082) e.Set("holdingCurrent", Format(hold));
                    if (gr != 50) e.Set("gateResistance", Format(gr));
                    return e;
                }
                case "206":
                {
                    var e = new Element { Type = "Triac" };
                    double trig = Num(6, 0.01), hold = Num(7, 0.0082), gr = Num(8, 100);
                    if (trig != 0.01) e.Set("triggerCurrent", Format(trig));
                    if (hold != 0.0082) e.Set("holdingCurrent", Format(hold));
                    if (gr != 100) e.Set("gateResistance", Format(gr));
                    return e;
                }
                case "203":
                {
                    var e = new Element { Type = "Diac" };
                    e.Set("onResistance", Format(Num(6, 500)));
                    e.Set("offResistance", Format(Num(7, 1e8)));
                    e.Set("breakdown", Format(Num(8, 30)));
                    e.Set("holdCurrent", Format(Num(9, 0.01)));
                    return e;
                }
                case "176":
                {
                    // after the flags (and a forward drop with flag 1): the last voltage and the capacitance at 0 V
                    var e = new Element { Type = "Varactor" };
                    double cap = t.Length >= 8 ? Num(t.Length - 1, 4e-12) : 4e-12;
                    if (cap != 4e-12) e.Set("capacitance", Format(cap));
                    return e;
                }
                case "175": return new Element { Type = "TunnelDiode" };
                case "173":
                {
                    var e = new Element { Type = "Triode" };
                    double mu = Num(6, 93), kg1 = Num(7, 680);
                    if (mu != 93) e.Set("mu", Format(mu));
                    if (kg1 != 680) e.Set("kg1", Format(kg1));
                    // the side of the plate: flag 2 follows the direction, flag 1 flips it (the points are set after Make, see Fix)
                    int x1 = Pixels(t[1]), y1 = Pixels(t[2]), x2 = Pixels(t[3]), y2 = Pixels(t[4]);
                    int ds = y2 == y1 ? Math.Sign(x2 - x1) : Math.Sign(y2 - y1);
                    if (ds == 0) ds = 1;
                    int side = ((flags & 2) != 0 ? ds : 1) * ((flags & 1) != 0 ? -1 : 1);
                    e.Fix = (side, 0);
                    return e;
                }
                case "179":
                {
                    var e = new Element { Type = "CurrentConveyor" };
                    double gain = Num(6, 1);
                    if (gain != 1) e.Set("gain", Format(gain));
                    ChipLayout(e, flags);
                    return e;
                }
                case "402":
                {
                    // the supplies are the fourth number of the first two parts of the composite (its rails)
                    var e = new Element { Type = "Ota" };
                    double Rail(int i, double fallback)
                    {
                        var parts = Tok(i).Split('_');
                        return parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
                    }
                    double neg = Rail(6, -9), pos = Rail(7, 9);
                    if (pos != 9) e.Set("positive", Format(pos));
                    if (neg != -9) e.Set("negative", Format(neg));
                    return e;
                }
                case "417":
                {
                    var e = new Element { Type = "Unijunction" };
                    if ((flags & 2) != 0) e.Set("flip", "true");
                    return e;
                }
                case "407":
                {
                    var e = new Element { Type = "Optocoupler" };
                    if ((flags & (3 << 10)) != 0) warnings.Add($"Line {lineNo}: a flipped optocoupler is imported unflipped; check its connections.");
                    e.Fix = (12, 0);
                    return e;
                }
                case "212": case "213": case "214": case "215":
                {
                    // Falstad's sources compute their output from an expression of their inputs; the package has the linear ones
                    string kind = type switch { "212" => "VCVS", "213" => "VCCS", "214" => "CCVS", _ => "CCCS" };
                    int inputs = (int)Num(6, 2);
                    string expr = Unescape(Tok(7));
                    double? gain = inputs == 2 ? LinearGain(expr, type is "212" or "213") : null;
                    if (gain is not { } g)
                    {
                        warnings.Add($"Line {lineNo}: the {Describe(type)} with the expression '{expr}' is not linear in the package circuits and was left out.");
                        return null;
                    }
                    var e = new Element { Type = kind };
                    e.Set("gain", Format(g));
                    ChipLayout(e, flags);
                    return e;
                }
                default:
                    warnings.Add($"Line {lineNo}: the element '{type}' ({Describe(type)}) is not in the package circuits and was left out.");
                    return null;
            }
        }

        private static string Describe(string type) => type switch
        {
            "155" => "D flip-flop", "165" => "555 timer", "164" => "counter", "j" => "JFET", "A" => "antenna", "169" => "tapped transformer", "200" => "AM source",
            "201" => "FM source", "418" => "external voltage", "211" => "audio output", "179" => "current conveyor", 
            "419" => "decimal display", "403" => "scope", "176" => "varactor", "402" => "OTA", "184" => "multiplexer", "167" => "ADC", "166" => "DAC",
            "163" => "ring counter", "168" => "latch", "212" => "voltage-controlled voltage source", "213" => "voltage-controlled current source", "175" => "tunnel diode",
            "400" => "Darlington", "177" => "SCR", "173" => "triode", "423" => "line", "185" => "seven segment", "193" => "T flip-flop", "203" => "diac", "206" => "triac",
            "208" => "custom logic", "430" => "time delay relay", "417" => "unijunction", "407" => "optocoupler", "405" => "LED array", "370" => "ammeter",
            _ => "unknown",
        };

        // a chip of Falstad's (sizeX 2): the pins 32 pixels apart (16 small), the right side 96 (48) pixels to the right
        private static void ChipLayout(Element e, int flags)
        {
            bool small = (flags & 1) != 0;
            if (!small) e.Set("spacing", "4");
            e.Fix = (small ? 6 : 12, 0);
        }

        // the factor k of a linear expression: k*(a-b), (a-b)*k, (a-b)/k, a-b (difference) or k*a, a*k, a/k, a (a current)
        private static double? LinearGain(string expr, bool difference)
        {
            string x = expr.Replace(" ", "").ToLowerInvariant();
            string core = difference ? "(a-b)" : "a";
            if (difference && x == "a-b") return 1;
            if (x == core) return 1;
            static double? Number(string n) => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
            if (x.StartsWith(core + "*")) return Number(x[(core.Length + 1)..]);
            if (x.StartsWith(core + "/")) return Number(x[(core.Length + 1)..]) is { } d && d != 0 ? 1 / d : null;
            if (x.EndsWith("*" + core)) return Number(x[..^(core.Length + 1)]);
            return null;
        }

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
            "Potentiometer" => "RV", "Transformer" => "T", "Relay" => "K", "Switch2" => "S", "Lamp" => "LA", "Fuse" => "F",
            "LightResistor" or "Thermistor" or "Memristor" => "R", "SparkGap" => "E", "TransmissionLine" => "TL", "AnalogSwitch" or "AnalogSwitch2" => "U",
            "Sweep" => "V", "RelayCoil" => "K", "RelayContact" => "S", "Jfet" or "Darlington" => "Q", "Thyristor" or "Triac" or "Diac" or "Varactor" or "TunnelDiode" => "D",
            "Triode" => "V", "Unijunction" => "Q", "VCVS" or "VCCS" or "CCCS" or "CCVS" or "CurrentConveyor" or "Ota" or "Optocoupler" => "U", _ => "X",
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
