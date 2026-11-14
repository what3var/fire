using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// The enums of each program, kept with its class table (CompiledProgram.Classes - every VM of the program, of its threads and tasks, shares that one
    /// instance): a name of an enum is looked up in the program that runs, never in the whole process (two programs in one process - the editor, the tests,
    /// ember - may each declare an enum `Mode` of their own).
    /// </summary>
    public static class ProgramEnums
    {
        private static readonly ConditionalWeakTable<object, IReadOnlyDictionary<string, EnumType>> Tables = new();

        /// <summary>The enums of the program whose class table is `classes` (CompiledProgram.Enums sets them).</summary>
        public static void Register(IReadOnlyDictionary<string, RuntimeClass> classes, IReadOnlyDictionary<string, EnumType> enums) => Tables.AddOrUpdate(classes, enums);

        /// <summary>The enum of the program with the class table `classes` that `name` means: its full name, or the name without its namespace when only one
        /// enum of the program is called so (EnumType.Matches); null when the program has none of that name.</summary>
        public static EnumType? Find(IReadOnlyDictionary<string, RuntimeClass>? classes, string name)
        {
            if (classes == null || !Tables.TryGetValue(classes, out var enums)) return null;
            if (enums.TryGetValue(name, out var exact)) return exact;
            EnumType? found = null;
            foreach (var e in enums.Values)
                if (e.Matches(name)) { if (found != null) return null; found = e; }
            return found;
        }
    }
}
