using System;
using System.Collections.Generic;
using System.Text;

namespace fire.Runtime
{
    public static class NativeImports
    {
        public static string Print => "print";

        public static string Graphics => "graphics";

        /// <summary>The window (`Window`, SDL) for a framebuffer - requires `graphics`. Separate from `graphics` so that a platform without a window can include a display instead.</summary>
        public static string Windows => "windows";

        public static string Devices => "devices";   // (a package; the name is kept for the packer, which knows its host parts)


        /// <summary>The UI library (see fire.UI.Bridge) - requires `graphics`.</summary>
        public static string Ui => "ui";

        /// <summary>Die Abfrage-Bibliothek (`Linq.From(...).Where(...)`, reiner fire-Quelltext, siehe fire.Standard.LinqPrelude).</summary>
        public static string Linq => "linq";

        /// <summary>Die Reflection-Bibliothek (`Type.Of(obj)`, `Reflect.Get(...)`, siehe fire.Standard.ReflectionPrelude, native Seite: fire.Runtime.ReflectionNatives).</summary>
        public static string Reflection => "reflection";

    }
}
