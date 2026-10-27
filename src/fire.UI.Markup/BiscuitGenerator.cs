using System.Globalization;
using System.Text;
using System.Text.Json;

namespace fire.UI.Markup
{
    /// <summary>The files that are compiled as the script generated from them: the markup of a user interface (`.fxml`, docs/UI_MARKUP.md) and the schematic of a circuit
    /// (`.biscuit`, docs/CIRCUITS.md).</summary>
    public static class MarkupFiles
    {
        public static bool IsUiMarkup(string path) => string.Equals(Path.GetExtension(path), ".fxml", StringComparison.OrdinalIgnoreCase);
        public static bool IsBiscuit(string path) => string.Equals(Path.GetExtension(path), ".biscuit", StringComparison.OrdinalIgnoreCase);
        public static bool IsGenerated(string path) => IsUiMarkup(path) || IsBiscuit(path);

        /// <summary>The script generated from the file; throws <see cref="MarkupException"/> when it has mistakes.</summary>
        public static string GenerateScript(string path, string text) =>
            IsBiscuit(path) ? BiscuitGenerator.Generate(text, path) : FireUiGenerator.Generate(MarkupParser.Parse(text), path);
    }

    /// <summary>One element of a `.biscuit` file as the generator sees it.</summary>
    public sealed record BiscuitElement(string Type, string Name, int Line);

    /// <summary>An input or output of a `.biscuit` file: a port of the circuit bound to a pin, in a color ("#RRGGBB").</summary>
    public sealed record BiscuitPort(string Name, string Direction, string Pin, string Color, int Line);

    /// <summary>A `.biscuit` file read by the generator: the class it makes and its elements.</summary>
    public sealed class BiscuitDocument
    {
        public string ClassName { get; init; } = "";
        public string Namespace { get; init; } = "";
        public IReadOnlyList<BiscuitElement> Elements { get; init; } = Array.Empty<BiscuitElement>();
        public IReadOnlyList<BiscuitPort> Ports { get; init; } = Array.Empty<BiscuitPort>();
        /// <summary>The text of the file without line breaks and indentation (it goes into the generated class).</summary>
        public string Compact { get; init; } = "";
    }

    /// <summary>Translates the schematic of a circuit (`.biscuit`: JSON with the elements on a grid, docs/CIRCUITS.md) into a fire script: ONE class that derives from
    /// `Circuits.Circuit`, loads the schematic in its constructor (the package `circuits` makes the parts and connects them) and has a field for every element with a name,
    /// typed with the class of its part (`Circuits.Resistor r1`). `new Amp()` is the circuit; a class of the program may derive from it.</summary>
    public static class BiscuitGenerator
    {
        /// <summary>The kinds of elements and the class of the field of a named one (the package `circuits` knows the rest: pins, properties, parts).</summary>
        public static readonly IReadOnlyDictionary<string, string> FieldTypes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Wire"] = "Circuits.Element", ["Ground"] = "Circuits.Ground", ["Resistor"] = "Circuits.Resistor", ["Capacitor"] = "Circuits.Capacitor",
            ["Inductor"] = "Circuits.Inductor", ["Switch"] = "Circuits.Switch", ["Source"] = "Circuits.VoltageSource", ["Rail"] = "Circuits.VoltageSource",
            ["CurrentSource"] = "Circuits.CurrentSource", ["AudioInput"] = "Circuits.AudioInput", ["LogicInput"] = "Circuits.LogicInput",
            ["Diode"] = "Circuits.Diode", ["Zener"] = "Circuits.Zener", ["Led"] = "Circuits.Led", ["Transistor"] = "Circuits.Transistor", ["Mosfet"] = "Circuits.Mosfet",
            ["Formula"] = "Circuits.Formula", ["Jfet"] = "Circuits.Jfet", ["Darlington"] = "Circuits.Darlington", ["Thyristor"] = "Circuits.Thyristor", ["Triac"] = "Circuits.Triac", ["Diac"] = "Circuits.Diac",
            ["Varactor"] = "Circuits.Varactor", ["TunnelDiode"] = "Circuits.TunnelDiode", ["Triode"] = "Circuits.Triode", ["Pentode"] = "Circuits.Pentode", ["VacuumDiode"] = "Circuits.VacuumDiode", ["VCVS"] = "Circuits.VCVS", ["VCCS"] = "Circuits.VCCS",
            ["CCCS"] = "Circuits.CCCS", ["CCVS"] = "Circuits.CCVS", ["CurrentConveyor"] = "Circuits.CurrentConveyor", ["Ota"] = "Circuits.Ota", ["Unijunction"] = "Circuits.Unijunction", ["Optocoupler"] = "Circuits.Optocoupler", ["OpAmp"] = "Circuits.OpAmp", ["Comparator"] = "Circuits.Comparator",
            ["And"] = "Circuits.Gate", ["Nand"] = "Circuits.Gate", ["Or"] = "Circuits.Gate", ["Nor"] = "Circuits.Gate", ["Xor"] = "Circuits.Gate", ["Xnor"] = "Circuits.Gate",
            ["Not"] = "Circuits.Gate", ["Buffer"] = "Circuits.Gate", ["DFlipFlop"] = "Circuits.FlipFlop", ["SRLatch"] = "Circuits.SRLatch", ["Counter"] = "Circuits.Counter",
            ["Timer555"] = "Circuits.Timer555", ["TFlipFlop"] = "Circuits.TFlipFlop", ["Latch"] = "Circuits.Latch", ["RingCounter"] = "Circuits.RingCounter",
            ["Multiplexer"] = "Circuits.Multiplexer", ["Demultiplexer"] = "Circuits.Demultiplexer", ["HalfAdder"] = "Circuits.HalfAdder", ["FullAdder"] = "Circuits.FullAdder",
            ["SipoShift"] = "Circuits.SipoShift", ["PisoShift"] = "Circuits.PisoShift", ["Adc"] = "Circuits.Adc", ["Dac"] = "Circuits.Dac",
            ["SevenSegmentDecoder"] = "Circuits.SevenSegmentDecoder", ["SevenSegment"] = "Circuits.SevenSegment", ["Monostable"] = "Circuits.Monostable",
            ["PhaseComparator"] = "Circuits.PhaseComparator", ["Vco"] = "Circuits.Vco", ["TriState"] = "Circuits.TriState", ["GpioLine"] = "Circuits.GpioLine", ["I2cAdc"] = "Circuits.I2cAdc", ["I2cDac"] = "Circuits.I2cDac",
            ["Potentiometer"] = "Circuits.Potentiometer", ["Transformer"] = "Circuits.Transformer", ["Relay"] = "Circuits.Relay", ["JKFlipFlop"] = "Circuits.JKFlipFlop",
            ["SchmittTrigger"] = "Circuits.SchmittTrigger", ["Switch2"] = "Circuits.Switch2", ["Lamp"] = "Circuits.Lamp", ["Fuse"] = "Circuits.Fuse",
            ["Crystal"] = "Circuits.Crystal", ["LightResistor"] = "Circuits.LightResistor", ["Thermistor"] = "Circuits.Thermistor", ["Memristor"] = "Circuits.Memristor",
            ["SparkGap"] = "Circuits.SparkGap", ["TransmissionLine"] = "Circuits.TransmissionLine", ["AnalogSwitch"] = "Circuits.AnalogSwitch",
            ["AnalogSwitch2"] = "Circuits.AnalogSwitch2", ["Sweep"] = "Circuits.SweepSource", ["RelayCoil"] = "Circuits.RelayCoil", ["RelayContact"] = "Circuits.CoilContact", ["SpiShiftOut"] = "Circuits.SpiShiftOut", ["SpiShiftIn"] = "Circuits.SpiShiftIn", ["LogicOutput"] = "Circuits.Element", ["Probe"] = "Circuits.Element", ["Scope"] = "Circuits.Scope",
            ["Label"] = "Circuits.Element", ["Pin"] = "Circuits.Element", ["Text"] = "Circuits.Element",
        };

        /// <summary>Names of members of Circuits.Circuit (and Part): an element must not be named like one, its field would hide it.</summary>
        private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
        {
            "name", "ports", "owner", "stepsInFire", "commitsInFire", "parts", "instruments", "ground", "dt", "time", "solver", "dirty", "external", "fireSteps", "clipped",
            "paramNames", "paramValues", "bindings", "schematic", "branch", "index",
            "Name", "AddPort", "PortAt", "Ports", "Branches", "Compile", "Stamp", "StampStep", "Commit", "Save", "Probe", "Reset", "CurrentRaw", "Current", "Invalidate", "Plan",
            "Ground", "Schematic", "HasSchematic", "LoadSchematic", "OnBuilt", "RemovePart", "Detach", "Add", "Parts", "Parameter", "Get", "HasParameter", "Value", "Bind",
            "ApplyBindings", "TimeStep", "Time", "TimeRaw", "IsCompiled", "Release", "Attach", "Top", "Collect", "AllPorts", "CircuitPorts", "GroundAll", "Build", "Step",
            "FireStep", "Run", "ProcessSamples", "SampleAt", "ProcessAudio", "Clipped", "PlanInfo", "this", "base",
        };

        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "class", "var", "int", "float", "bool", "string", "char", "any", "undefined", "true", "false", "if", "else", "while", "for", "foreach", "in", "of", "from", "is",
            "return", "new", "delete", "namespace", "using", "func", "on", "off", "copy", "take", "try", "catch", "finally", "throw", "break", "continue", "switch", "case",
            "default", "static", "construct", "destruct", "fire", "sync", "unit", "enum", "interface", "extern", "native", "byte", "do", "readonly", "flat",
        };

        /// <summary>Reads a `.biscuit` text (checks it; throws <see cref="MarkupException"/> with the lines of the mistakes).</summary>
        public static BiscuitDocument Read(string text, string sourceName = "")
        {
            var errors = new List<MarkupDiagnostic>();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
            catch (JsonException ex) { throw new MarkupException(new[] { new MarkupDiagnostic((int)(ex.LineNumber ?? -1) + 1, "The file is no valid JSON: " + ex.Message.Split(" Path:")[0]) }); }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new MarkupException(new[] { new MarkupDiagnostic(1, "A .biscuit file is a JSON object.") });
                string className = root.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
                if (className == "") className = Identifier(Path.GetFileNameWithoutExtension(sourceName));
                if (className == "") className = "Schematic";
                if (!IsIdentifier(className)) errors.Add(new MarkupDiagnostic(1, $"'{className}' is no name for a class."));
                string ns = root.TryGetProperty("namespace", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
                if (ns != "" && !ns.Split('.').All(IsIdentifier)) errors.Add(new MarkupDiagnostic(1, $"'{ns}' is no namespace."));

                var lines = LineStarts(text);
                var elements = new List<BiscuitElement>();
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (root.TryGetProperty("elements", out var list))
                {
                    if (list.ValueKind != JsonValueKind.Array) errors.Add(new MarkupDiagnostic(1, "'elements' is a list."));
                    else
                    {
                        int i = 0;
                        foreach (var e in list.EnumerateArray())
                        {
                            int line = LineOf(lines, ElementOffset(text, i));
                            i++;
                            if (e.ValueKind != JsonValueKind.Object) { errors.Add(new MarkupDiagnostic(line, "An element is a JSON object.")); continue; }
                            string type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
                            if (!FieldTypes.ContainsKey(type)) { errors.Add(new MarkupDiagnostic(line, type == "" ? "An element without a type." : $"There is no element '{type}'.")); continue; }
                            if (!IsPoint(e, "from") || !IsPoint(e, "to")) errors.Add(new MarkupDiagnostic(line, $"The {type} needs 'from' and 'to' as [x, y] (whole numbers)."));
                            string name = e.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString()! : "";
                            if (name != "")
                            {
                                if (!IsIdentifier(name)) errors.Add(new MarkupDiagnostic(line, $"'{name}' is no name for an element (letters, digits and '_', not a keyword)."));
                                else if (Reserved.Contains(name)) errors.Add(new MarkupDiagnostic(line, $"'{name}' is a member of Circuits.Circuit: choose another name for the element."));
                                else if (name == className) errors.Add(new MarkupDiagnostic(line, $"'{name}' is the name of the class."));
                                else if (!names.Add(name)) errors.Add(new MarkupDiagnostic(line, $"Two elements are named '{name}'."));
                            }
                            elements.Add(new BiscuitElement(type, name, line));
                        }
                    }
                }
                // the inputs and outputs: a member each (named like no element and no member of Circuits.Circuit)
                var ports = new List<BiscuitPort>();
                if (root.TryGetProperty("ports", out var plist))
                {
                    int at = text.IndexOf("\"ports\"", StringComparison.Ordinal);
                    int line = LineOf(lines, at);
                    if (plist.ValueKind != JsonValueKind.Array) errors.Add(new MarkupDiagnostic(line, "'ports' is a list."));
                    else
                    {
                        foreach (var p in plist.EnumerateArray())
                        {
                            if (p.ValueKind != JsonValueKind.Object) { errors.Add(new MarkupDiagnostic(line, "A port is a JSON object.")); continue; }
                            string Str(string key) => p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                            string name = Str("name"), direction = Str("direction"), pin = Str("pin"), color = Str("color");
                            if (direction == "") direction = "input";
                            if (!IsIdentifier(name)) errors.Add(new MarkupDiagnostic(line, $"'{name}' is no name for a port (letters, digits and '_', not a keyword)."));
                            else if (Reserved.Contains(name)) errors.Add(new MarkupDiagnostic(line, $"'{name}' is a member of Circuits.Circuit: choose another name for the port."));
                            else if (name == className) errors.Add(new MarkupDiagnostic(line, $"'{name}' is the name of the class."));
                            else if (!names.Add(name)) errors.Add(new MarkupDiagnostic(line, $"The port '{name}' is named like an element or another port."));
                            if (direction is not ("input" or "output")) errors.Add(new MarkupDiagnostic(line, $"The port '{name}': the direction is 'input' or 'output'."));
                            ports.Add(new BiscuitPort(name, direction, pin, color, line));
                        }
                    }
                }
                if (errors.Count > 0) throw new MarkupException(errors);
                string compact = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                return new BiscuitDocument { ClassName = className, Namespace = ns, Elements = elements, Ports = ports, Compact = compact };
            }
        }

        /// <summary>The script of a `.biscuit` text. `sourceName` (the file) names the class when the file does not ("class").</summary>
        public static string Generate(string text, string sourceName = "")
        {
            var d = Read(text, sourceName);
            var sb = new StringBuilder();
            string file = Path.GetFileName(sourceName);
            sb.Append("// Generated from ").Append(file == "" ? "a schematic" : file).Append(" (docs/CIRCUITS.md) - edit the schematic, not this script.\n");
            sb.Append("#import \"circuits\"\n");
            string indent = "";
            if (d.Namespace != "")
            {
                sb.Append("namespace ").Append(d.Namespace).Append(" {\n");
                indent = "    ";
            }
            sb.Append(indent).Append("/// <summary>The circuit of ").Append(file == "" ? "the schematic" : file).Append(": the parts and connections of the drawing; a field for every element with a name.</summary>\n");
            sb.Append(indent).Append("class ").Append(d.ClassName).Append(" : Circuits.Circuit {\n");
            var named = d.Elements.Where(e => e.Name != "").ToList();
            foreach (var e in named) sb.Append(indent).Append("    ").Append(FieldTypes[e.Type]).Append(' ').Append(e.Name).Append('\n');
            if (named.Count > 0) sb.Append('\n');
            // the inputs and outputs: a port each, in the color of the drawing (Colorize is read by the editor only)
            foreach (var p in d.Ports)
            {
                sb.Append(indent).Append("    /// <summary>The ").Append(p.Direction).Append(" ").Append(p.Name).Append(" of the circuit")
                  .Append(p.Pin == "" ? " (bound to no pin)" : ", bound to " + p.Pin).Append(".</summary>\n");
                if (ColorNumber(p.Color) is { } rgb) sb.Append(indent).Append("    [Colorize 0x").Append(rgb).Append("]\n");
                sb.Append(indent).Append("    Circuits.Port ").Append(p.Name).Append('\n');
            }
            if (d.Ports.Count > 0) sb.Append('\n');
            sb.Append(indent).Append("    construct() : base(").Append(Quote(d.ClassName)).Append(") {\n");
            sb.Append(indent).Append("        this.LoadSchematic(").Append(Quote(d.Compact)).Append(")\n");
            sb.Append(indent).Append("    }\n");
            sb.Append('\n');
            sb.Append(indent).Append("    /// <summary>The schematic made its parts (again): the fields point at them.</summary>\n");
            sb.Append(indent).Append("    undefined OnBuilt() {\n");
            if (named.Count > 0) sb.Append(indent).Append("        var s = this.Schematic\n");
            foreach (var p in d.Ports)
            {
                // the port of the circuit is there once the schematic bound it to its pin
                sb.Append(indent).Append("        this.").Append(p.Name).Append(" = undefined\n");
                sb.Append(indent).Append("        foreach (port in this.ports) { if (port.name == ").Append(Quote(p.Name)).Append(") { this.").Append(p.Name).Append(" = port } }\n");
            }
            foreach (var e in named)
            {
                string type = FieldTypes[e.Type];
                string q = Quote(e.Name);
                if (type == "Circuits.Element") sb.Append(indent).Append("        this.").Append(e.Name).Append(" = s.Find(").Append(q).Append(")\n");
                else if (type == "Circuits.Scope") sb.Append(indent).Append("        this.").Append(e.Name).Append(" = s.Find(").Append(q).Append(").scope\n");
                else
                {
                    sb.Append(indent).Append("        var p_").Append(e.Name).Append(" = s.Find(").Append(q).Append(").part\n");
                    sb.Append(indent).Append("        if (p_").Append(e.Name).Append(" != undefined) { this.").Append(e.Name).Append(" = p_").Append(e.Name).Append('!').Append(type)
                      .Append(" } else { this.").Append(e.Name).Append(" = undefined }\n");
                }
            }
            sb.Append(indent).Append("    }\n");
            sb.Append(indent).Append("}\n");
            if (d.Namespace != "") sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>"#RRGGBB" as the six hex digits (upper case), null if it is no color.</summary>
        public static string? ColorNumber(string color)
        {
            var t = color.Trim();
            if (t.StartsWith('#')) t = t[1..];
            else if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
            return t.Length == 6 && t.All(Uri.IsHexDigit) ? t.ToUpperInvariant() : null;
        }

        /// <summary>A text as a fire string literal.</summary>
        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static bool IsPoint(JsonElement e, string key) =>
            e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2 &&
            p.EnumerateArray().All(v => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _));

        public static bool IsIdentifier(string s) =>
            s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_') && !Keywords.Contains(s);

        /// <summary>A file name as a class name ("my amp" is "my_amp", "2stage" is "_2stage").</summary>
        public static string Identifier(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
            if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, '_');
            string r = sb.ToString();
            return Keywords.Contains(r) ? "_" + r : r;
        }

        private static List<int> LineStarts(string text)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
            return starts;
        }

        private static int LineOf(List<int> starts, int offset)
        {
            if (offset < 0) return 1;
            int line = starts.BinarySearch(offset);
            return line >= 0 ? line + 1 : ~line;
        }

        // where the i-th object of the "elements" array starts (an estimate good for messages: the i-th '{' after "elements")
        private static int ElementOffset(string text, int index)
        {
            int at = text.IndexOf("\"elements\"", StringComparison.Ordinal);
            if (at < 0) return -1;
            at = text.IndexOf('[', at);
            int depth = 0, count = -1;
            bool inString = false;
            for (int i = at + 1; i < text.Length && at >= 0; i++)
            {
                char ch = text[i];
                if (inString) { if (ch == '\\') i++; else if (ch == '"') inString = false; continue; }
                if (ch == '"') inString = true;
                else if (ch == '{' || ch == '[') { if (depth == 0 && ch == '{') { count++; if (count == index) return i; } depth++; }
                else if (ch == '}' || ch == ']') { if (depth == 0) return -1; depth--; }
            }
            return -1;
        }
    }
}
