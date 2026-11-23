using System.Globalization;
using System.Text;
using fire.Bytecode;

namespace fire.Compiler
{
    // tables/bridges.def from the C# bridges (fire.Terminal.Bridge, fire.Windows.Bridge, fire.Runtime.ReflectionNatives) - with fire.Runtime they go, and bridges.def
    // stays as the source of these names (BridgeNatives reads it; docs/ROADMAP_C_PORT.md, M1).
    public static partial class Tables
    {
        /// <summary>FIRE_BRIDGE_NATIVE(import, index, name): the natives an import of the compiler registers behind those every program has (ImportedPreludes.Insert:
        /// graphics, then windows, then reflection - the natives of packages follow, the program file names them), each in the order of its registration. A program
        /// file does not name these (a call addresses them by index): the VM in C reads its natives from here.</summary>
        public static string Bridges()
        {
            var b = new StringBuilder();
            b.Append("/* The natives of the imports the compiler knows, behind those of natives.def in this order when a program imports them (ImportedPreludes.Insert): graphics,\n");
            b.Append(" * windows, reflection - FIRE_BRIDGE_NATIVE(import, index, name), the index within the import. Written by `forge tables` from GraphicsBridge.RegisterStubs,\n");
            b.Append(" * WindowsBridge.RegisterStubs and ReflectionNatives.Register; do not edit. */\n");
            void Group(string import, Action<NativeRegistry> register)
            {
                var registry = new NativeRegistry();
                register(registry);
                int i = 0;
                foreach (var name in registry.Names.OrderBy(registry.IndexOf))
                    b.Append("FIRE_BRIDGE_NATIVE(").Append(import).Append(", ").Append(i++).Append(", ").Append(CString(name)).Append(")\n");
            }
            Group("graphics", fire.Terminal.Bridge.GraphicsBridge.RegisterStubs);
            Group("windows", fire.Windows.Bridge.WindowsBridge.RegisterStubs);
            Group("reflection", fire.Runtime.ReflectionNatives.Register);
            return b.ToString();
        }
    }
}
