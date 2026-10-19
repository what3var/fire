using System.Collections.Generic;

namespace fire.Standard
{
    /// <summary>
    /// The reflection library (`#import "reflection"`): query and use classes and members at runtime. The source here is
    /// the fire side (`Type`, `Member`, `Reflect`, `Selector`); the actual work is done by native functions of the runtime
    /// (`fire.Runtime.ReflectionNatives`), so that access checks, units and locking apply as in normal code.
    /// See docs/DESIGN_LAMBDA_REFLECTION_PROBE.md and SPEC 8.13.
    /// </summary>
    public static class ReflectionPrelude
    {
        /// <summary>If this native function is registered, the program uses the library (the compiler then also writes type metadata).</summary>
        public const string MembersNative = "__refl_members";

        /// <summary>The classes of the library: for an access through them, for private/protected the code BEFORE it counts.</summary>
        public static readonly HashSet<string> HelperClasses = new() { "Reflect", "Type", "Member", "Selector", "Attribute" };

    }
}
