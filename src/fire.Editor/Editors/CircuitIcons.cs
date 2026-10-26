using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace fire.Editor
{
    /// <summary>
    /// The small pictures of the parts in the palette of the circuit editor: the symbols of the CircuitView drawn as paths in a box of 32 x 20 (the
    /// leads run along y = 10 from x = 0 to x = 32), scaled to the size of the icon.
    /// </summary>
    internal static class CircuitIcons
    {
        private const string Circle = "M9,10 A7,7 0 1 1 23,10 A7,7 0 1 1 9,10";
        private const string Chip = "M6,1 L26,1 L26,19 L6,19 Z M0,5 L6,5 M0,15 L6,15 M26,5 L32,5 M26,15 L32,15";
        private const string OrBody = "M4,2 Q14,2 24,10 Q14,18 4,18 Q9,10 4,2 Z M0,6 L6,6 M0,14 L6,14";
        private const string AndBody = "M4,2 L15,2 A8,8 0 0 1 15,18 L4,18 Z M0,6 L4,6 M0,14 L4,14";
        private const string Bubble = " M23,10 A2,2 0 1 1 27,10 A2,2 0 1 1 23,10 M27,10 L32,10";

        private static readonly Dictionary<string, string> Paths = new()
        {
            ["Pointer"] = "M12,1 L12,17 L16,13.5 L18.5,19 L21,18 L18.5,12.5 L23,12.5 Z",
            ["Wire"] = "M0,10 L32,10",
            ["Ground"] = "M16,1 L16,10 M7,10 L25,10 M10,14 L22,14 M13,18 L19,18",
            ["Resistor"] = "M0,10 L8,10 L10,6 L13,14 L16,6 L19,14 L22,6 L24,10 L32,10",
            ["Capacitor"] = "M0,10 L14,10 M14,3 L14,17 M18,3 L18,17 M18,10 L32,10",
            ["Inductor"] = "M0,10 L5,10 A3,3 0 0 1 11,10 A3,3 0 0 1 17,10 A3,3 0 0 1 23,10 A3,3 0 0 1 29,10 L32,10",
            ["Switch"] = "M0,10 L9,10 L23,3 M23,10 L32,10 M8,10 A1,1 0 1 1 10,10 M22,10 A1,1 0 1 1 24,10",
            ["Source"] = "M0,10 L9,10 M23,10 L32,10 " + Circle + " M16,5 L16,9 M14,7 L18,7 M14,13 L18,13",
            ["Rail"] = "M0,10 L22,10 M22,10 A4,4 0 1 1 30,10 A4,4 0 1 1 22,10 M26,8 L26,12 M24,10 L28,10",
            ["CurrentSource"] = "M0,10 L9,10 M23,10 L32,10 " + Circle + " M12,10 L20,10 M17,7 L20,10 L17,13",
            ["AudioInput"] = "M0,10 L9,10 M23,10 L32,10 " + Circle + " M11,10 Q13.5,4 16,10 Q18.5,16 21,10",
            ["LogicInput"] = "M0,10 L18,10 M18,4 L30,4 L30,16 L18,16 Z M22,7 L22,13 L26,13",
            ["Diode"] = "M0,10 L11,10 M11,4 L11,16 L21,10 Z M21,4 L21,16 M21,10 L32,10",
            ["Zener"] = "M0,10 L11,10 M11,4 L11,16 L21,10 Z M19,2 L21,4 L21,16 L23,18 M21,10 L32,10",
            ["Led"] = "M0,10 L11,10 M11,5 L11,15 L19,10 Z M19,5 L19,15 M19,10 L32,10 M18,4 L23,0 M21,0 L23,0 L23,2 M22,6 L27,2 M25,2 L27,2 L27,4",
            ["Transistor"] = "M0,10 L12,10 M12,3 L12,17 M12,7 L22,1 L22,0 M12,13 L22,19 L22,20 M17,18 L21,19 L19,15",
            ["Mosfet"] = "M0,10 L9,10 M9,4 L9,16 M12,3 L12,7 M12,8 L12,12 M12,13 L12,17 M12,5 L22,5 L22,0 M12,15 L22,15 L22,20 M12,10 L22,10 L22,15",
            ["Formula"] = "M0,10 L8,10 M8,5 L24,5 L24,15 L8,15 Z M24,10 L32,10 M12,12 L15,7 L18,12",
            ["OpAmp"] = "M6,1 L6,19 L27,10 Z M0,5 L6,5 M0,15 L6,15 M27,10 L32,10 M8,5 L11,5 M8,15 L11,15 M9.5,13.5 L9.5,16.5",
            ["Comparator"] = "M6,1 L6,19 L27,10 Z M0,5 L6,5 M0,15 L6,15 M27,10 L32,10 M13,13 L15,13 L15,7 L18,7",
            ["And"] = AndBody + " M23,10 L32,10",
            ["Nand"] = AndBody + Bubble,
            ["Or"] = OrBody + " M24,10 L32,10",
            ["Nor"] = OrBody + Bubble,
            ["Xor"] = OrBody + " M1,2 Q6,10 1,18 M24,10 L32,10",
            ["Xnor"] = OrBody + " M1,2 Q6,10 1,18" + Bubble,
            ["Not"] = "M0,10 L6,10 M6,2 L6,18 L22,10 Z M22,10 A2,2 0 1 1 26,10 A2,2 0 1 1 22,10 M26,10 L32,10",
            ["Buffer"] = "M0,10 L6,10 M6,2 L6,18 L23,10 Z M23,10 L32,10",
            ["LogicOutput"] = "M0,10 L18,10 M18,10 A6,6 0 1 1 30,10 A6,6 0 1 1 18,10",
            ["Probe"] = "M0,10 L32,10 M11,5 L21,5 L21,15 L11,15 Z",
            ["Scope"] = "M2,2 L30,2 L30,18 L2,18 Z M4,12 L8,6 L12,14 L16,6 L20,14 L24,8 L28,12",
            ["Label"] = "M0,10 L6,10 M6,5 L23,5 L28,10 L23,15 L6,15 Z",
            ["Pin"] = "M0,10 L18,10 M18,6 L26,6 L26,14 L18,14 Z",
            ["Text"] = "M8,3 L24,3 M16,3 L16,18 M12,18 L20,18",
            ["GpioLine"] = "M0,10 L16,10 M16,3 L30,3 L30,17 L16,17 Z M19,13 L19,7 L27,7 M23,7 L23,13",
            ["Potentiometer"] = "M0,14 L8,14 L10,10 L13,18 L16,10 L19,18 L22,10 L24,14 L32,14 M16,1 L16,9 M13,6 L16,9 L19,6",
            ["Transformer"] = "M0,2 L4,2 A2.25,2.25 0 0 1 4,6.5 A2.25,2.25 0 0 1 4,11 A2.25,2.25 0 0 1 4,15.5 A2.25,2.25 0 0 1 4,20 L0,20 " +
                              "M14,1 L14,19 M18,1 L18,19 M32,2 L28,2 A2.25,2.25 0 0 0 28,6.5 A2.25,2.25 0 0 0 28,11 A2.25,2.25 0 0 0 28,15.5 A2.25,2.25 0 0 0 28,20 L32,20",
            ["Relay"] = "M0,6 L8,6 L24,1 M24,6 L32,6 M24,1 L32,1 M2,17 L7,17 A2.5,2.5 0 0 1 12,17 A2.5,2.5 0 0 1 17,17 A2.5,2.5 0 0 1 22,17 A2.5,2.5 0 0 1 27,17 L30,17 M16,14 L16,4",
            ["JKFlipFlop"] = Chip + " M10,7 L13,7 L13,12 Q13,14 11,14 M19,7 L19,13 M23,7 L19,10 L23,13",
            ["SchmittTrigger"] = "M0,10 L6,10 M6,2 L6,18 L23,10 Z M23,10 L32,10 M9,13 L14,13 L14,7 L17,7 M11,13 L11,7 L16,7",
            ["Switch2"] = "M0,10 L10,10 L24,4 M24,4 L32,4 M24,16 L32,16 M10,10 A1,1 0 1 1 10.01,10",
            ["Lamp"] = "M0,10 L9,10 M23,10 L32,10 " + Circle + " M11,5 L21,15 M11,15 L21,5",
            ["Fuse"] = "M0,10 L8,10 M8,7 L24,7 L24,13 L8,13 Z M8,10 L24,10 M24,10 L32,10",
            ["Crystal"] = "M0,10 L11,10 M11,4 L11,16 M21,4 L21,16 M21,10 L32,10 M14,5 L18,5 L18,15 L14,15 Z",
            ["LightResistor"] = "M0,12 L8,12 M8,9 L24,9 L24,15 L8,15 Z M24,12 L32,12 M9,1 L14,6 M14,6 L14,4 M15,1 L20,6 M20,6 L20,4",
            ["Thermistor"] = "M0,10 L8,10 M8,7 L24,7 L24,13 L8,13 Z M24,10 L32,10 M6,17 L24,3 L28,3",
            ["Memristor"] = "M0,10 L8,10 M8,7 L24,7 L24,13 L8,13 Z M24,10 L32,10 M20,7 L20,13 M22,7 L22,13",
            ["SparkGap"] = "M0,10 L14,10 M10,6 L14,10 L10,14 M18,10 L32,10 M22,6 L18,10 L22,14",
            ["TransmissionLine"] = "M0,6 L32,6 M0,14 L32,14 M4,6 L4,14 M28,6 L28,14",
            ["AnalogSwitch"] = "M0,10 L10,10 L22,5 M22,10 L32,10 M16,18 L16,8",
            ["AnalogSwitch2"] = "M0,10 L10,10 L24,4 M24,4 L32,4 M24,16 L32,16 M16,18 L16,8",
            ["Sweep"] = "M0,10 L18,10 M18,10 A6,6 0 1 1 30,10 A6,6 0 1 1 18,10 M19,10 Q20,6 21,10 Q22,14 23,10 Q23.7,7 24.4,10 Q25,13 25.6,10 Q26,8 26.4,10 Q26.8,12 27.2,10",
            ["RelayCoil"] = "M0,10 L10,10 M22,10 L32,10 M10,4 L22,4 L22,16 L10,16 Z M10,16 L22,4",
            ["RelayContact"] = "M0,12 L9,12 L23,5 M23,12 L32,12 M8,12 A1,1 0 1 1 10,12 M22,12 A1,1 0 1 1 24,12 M14,2 L18,2",
            ["Jfet"] = "M0,10 L12,10 M12,3 L12,17 M12,6 L22,6 L22,0 M12,14 L22,14 L22,20 M9,8 L12,10 L9,12",
            ["Darlington"] = "M0,10 L10,10 M10,3 L10,17 M13,4 L13,16 M13,7 L22,1 L22,0 M13,13 L22,19 L22,20 M17,18 L21,19 L19,15",
            ["Thyristor"] = "M0,10 L11,10 M11,4 L11,16 L21,10 Z M21,4 L21,16 M21,10 L32,10 M21,13 L26,18 L26,20",
            ["Triac"] = "M0,10 L11,10 M11,3 L11,17 M21,3 L21,17 M11,3 L21,7 L11,10 M21,10 L11,13 L21,17 M21,10 L32,10 M21,15 L26,19 L26,20",
            ["Diac"] = "M0,10 L11,10 M11,3 L11,17 M21,3 L21,17 M11,3 L21,7 L11,10 M21,10 L11,13 L21,17 M21,10 L32,10",
            ["Varactor"] = "M0,10 L11,10 M11,4 L11,16 L19,10 Z M19,4 L19,16 M22,4 L22,16 M22,10 L32,10",
            ["TunnelDiode"] = "M0,10 L11,10 M11,4 L11,16 L21,10 Z M18,4 L21,4 L21,16 L18,16 M21,10 L32,10",
            ["Triode"] = "M0,10 L10,10 M16,10 A8,8 0 1 1 16.01,10 M12,10 L14,10 M16,10 L18,10 M20,10 L22,10 M24,0 L24,6 M21,6 L27,6 M18,20 L18,14 L27,14",
            ["VCVS"] = Chip + " M12,7 L16,13 L20,7 Z",
            ["VCCS"] = Chip + " M16,6 L16,14 M13,11 L16,14 L19,11",
            ["CCCS"] = Chip + " M16,6 L16,14 M13,11 L16,14 L19,11 M10,6 L10,8",
            ["CCVS"] = Chip + " M12,7 L16,13 L20,7 Z M10,6 L10,8",
            ["CurrentConveyor"] = Chip + " M10,8 L14,8 M10,12 L14,12 M18,10 L22,10",
            ["Unijunction"] = "M0,14 L12,14 L19,9 M16,10 L19,9 L18,12 M20,4 L20,16 M20,6 L28,6 L28,0 M20,14 L28,14 L28,20",
            ["Ota"] = "M6,1 L6,19 L24,10 Z M0,5 L6,5 M0,15 L6,15 M24,10 L32,10 M18,13 L18,20 M20,10 A2,2 0 1 1 20.01,10",
            ["TFlipFlop"] = Chip + " M11,8 L19,8 M15,8 L15,14",
            ["Latch"] = Chip + " M11,7 L11,13 L18,13",
            ["RingCounter"] = Chip + " M15,10 m-4,0 a4,3 0 1,0 8,0 a4,3 0 1,0 -8,0",
            ["Multiplexer"] = "M8,2 L24,6 L24,14 L8,18 Z M2,6 L8,6 M2,14 L8,14 M24,10 L30,10",
            ["Demultiplexer"] = "M8,6 L24,2 L24,18 L8,14 Z M2,10 L8,10 M24,6 L30,6 M24,14 L30,14",
            ["HalfAdder"] = Chip + " M15,6 L15,14 M11,10 L19,10",
            ["FullAdder"] = Chip + " M15,6 L15,14 M11,10 L19,10 M20,6 L22,6",
            ["SipoShift"] = Chip + " M10,10 L20,10 M17,7 L20,10 L17,13",
            ["PisoShift"] = Chip + " M10,10 L20,10 M13,7 L10,10 L13,13",
            ["Adc"] = Chip + " M10,13 Q13,6 15,10 T20,8 M18,14 L18,12 L20,12 L20,14",
            ["Dac"] = Chip + " M10,14 L10,12 L12,12 L12,14 M14,12 L14,10 L16,10 L16,8 L18,8 L18,6",
            ["SevenSegmentDecoder"] = Chip + " M12,5 L18,5 L18,15 L12,15 M12,10 L18,10",
            ["SevenSegment"] = "M9,1 L23,1 L23,19 L9,19 Z M12,4 L20,4 M20,4 L20,10 M20,10 L20,16 M12,16 L20,16 M12,10 L12,16 M12,4 L12,10 M12,10 L20,10",
            ["Monostable"] = Chip + " M10,13 L13,13 L13,7 L17,7 L17,13 L20,13",
            ["PhaseComparator"] = Chip + " M11,8 L13,8 L13,12 M17,8 L19,8 L19,12",
            ["Vco"] = Chip + " M10,13 L12,13 L12,7 L14,7 L14,13 L16,13 L16,7 L17,7 L17,13 L18,13 L18,7 L19,7 L19,13 L20,13",
            ["TriState"] = "M0,10 L10,10 M10,4 L10,16 L22,10 Z M22,10 L32,10 M16,2 L16,7",
            ["Optocoupler"] = Chip + " M10,6 L10,14 M8,8 L12,8 L10,12 Z M14,9 L17,9 M15,11 L18,11 M20,6 L20,14 M20,9 L23,6 M20,11 L23,14",
            ["Counter"] = Chip +" M11,7 L11,13 M14,7 L18,7 L18,10 L14,10 L14,13 L18,13 M21,7 L21,13",
        };

        /// <summary>The icon of a kind of element (a chip for the kinds without a picture of their own).</summary>
        public static Control Create(string type, IBrush stroke, double width = 26, double height = 16)
        {
            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(Paths.TryGetValue(type, out var data) ? data : Chip),
                Stroke = stroke,
                StrokeThickness = 1.6,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
            };
            // the box of the symbols, scaled as a whole (a wire stays as long as the others)
            var canvas = new Canvas { Width = 32, Height = 20, ClipToBounds = false };
            canvas.Children.Add(path);
            return new Viewbox { Child = canvas, Width = width, Height = height, Stretch = Stretch.Uniform };
        }
    }
}
