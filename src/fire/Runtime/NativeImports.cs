using System;
using System.Collections.Generic;
using System.Text;

namespace fire.Runtime
{
    public static class NativeImports
    {
        public static string Print => "print";

        public static string Graphics => "graphics";

        public static string Devices => "devices";

        public static string IO => "io";

        /// <summary>Die Oberflächen-Bibliothek (siehe fire.UI.Bridge) - setzt `graphics` voraus.</summary>
        public static string Ui => "ui";

        /// <summary>Die Abfrage-Bibliothek (`Linq.From(...).Where(...)`, reiner fire-Quelltext, siehe fire.Standard.LinqPrelude).</summary>
        public static string Linq => "linq";
    }
}
