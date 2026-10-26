using System.Reflection;

namespace fire.UI.Markup
{
    /// <summary>The example circuits of the circuit editor (Samples menu): small schematics of our own, embedded in this assembly
    /// (CircuitSamples/*.biscuit).</summary>
    public static class CircuitSamples
    {
        /// <summary>One example: its title in the menu, a line about it and the name of its file.</summary>
        public sealed record Sample(string Title, string Description, string File)
        {
            /// <summary>The schematic (the text of the .biscuit file).</summary>
            public string Text => Read(File);
        }

        /// <summary>The examples in the order of the menu.</summary>
        public static IReadOnlyList<Sample> All { get; } = new[]
        {
            new Sample("RC low-pass", "A square wave through a resistor into a capacitor", "rc-lowpass.biscuit"),
            new Sample("LC tank", "A parallel resonant circuit rings at every edge", "lc-ringing.biscuit"),
            new Sample("Bridge rectifier", "Four diodes and a smoothing capacitor", "bridge-rectifier.biscuit"),
            new Sample("Zener regulator", "A Zener diode holds the load near 5.6 V", "zener-regulator.biscuit"),
            new Sample("Transistor switch", "A logic input switches an LED through an NPN transistor", "transistor-switch.biscuit"),
            new Sample("Inverting amplifier", "An op amp with a gain of -10", "inverting-amplifier.biscuit"),
            new Sample("Schmitt trigger oscillator", "A relaxation oscillator that blinks an LED", "schmitt-oscillator.biscuit"),
            new Sample("555 astable", "The classic timer as a blinker", "timer555-blinker.biscuit"),
            new Sample("Counter with display", "A 4-bit counter shows 0 to F on a 7-segment display", "counter-display.biscuit"),
            new Sample("Full adder", "Logic inputs, a full adder and logic outputs", "full-adder.biscuit"),
        };

        private static string Read(string file)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CircuitSamples." + file)
                ?? throw new FileNotFoundException("There is no example circuit '" + file + "'.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
