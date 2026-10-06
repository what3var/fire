using System;
using System.Collections.Generic;
using System.Text;

namespace fire.Runtime
{
    public static class NativeImports
    {
        public static string Print => "print";

        public static string Graphics => "graphics";

        /// <summary>Das Fenster (`Window`, SDL) für einen Framebuffer - setzt `graphics` voraus. Getrennt von `graphics`, damit eine Plattform ohne Fenster statt dessen ein Display einbinden kann.</summary>
        public static string Windows => "windows";

        public static string Devices => "devices";   // (a package; the name is kept for the packer, which knows its host parts)


        /// <summary>Die Oberflächen-Bibliothek (siehe fire.UI.Bridge) - setzt `graphics` voraus.</summary>
        public static string Ui => "ui";

        /// <summary>Die Abfrage-Bibliothek (`Linq.From(...).Where(...)`, reiner fire-Quelltext, siehe fire.Standard.LinqPrelude).</summary>
        public static string Linq => "linq";

        /// <summary>Die Reflection-Bibliothek (`Type.Of(obj)`, `Reflect.Get(...)`, siehe fire.Standard.ReflectionPrelude, native Seite: fire.Runtime.ReflectionNatives).</summary>
        public static string Reflection => "reflection";

    }
}
