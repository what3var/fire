using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace fire.Values
{
    /// <summary>
    /// The type of an enum value at run time (docs/language/10-enums-and-namespaces.md): an enum value is an <see cref="ValueKind.Int"/> that carries the id of
    /// its enum (<see cref="Value.Enum"/>), so that it shows its name, is not an `int` for `is of` and keeps its type through `|` of a flag enum - while
    /// every operation on whole numbers works on it as before (`c + 1` is a plain int).
    ///
    /// The types are interned for the process: the same declaration (name, flag or not, the members) is the same type, whichever program declared it; a
    /// different declaration with the same name gets an id of its own. The id is small (16 bits, like the slot the native runtime keeps it in).
    /// </summary>
    public sealed class EnumType
    {
        private static readonly object Lock = new();
        private static readonly List<EnumType> All = new() { null! };   // (id 0: no enum)
        private static readonly Dictionary<string, EnumType> ByKey = new(StringComparer.Ordinal);

        /// <summary>The name as the program writes it, with its namespace (`Geo.Kind`).</summary>
        public string Name { get; }
        /// <summary>A `flag enum`: its values combine with `|`, `&amp;`, `^` and `~`, and a combined value shows the names of its flags (`a, b`).</summary>
        public bool IsFlags { get; }
        /// <summary>The members in the order of the declaration.</summary>
        public IReadOnlyList<(string Name, long Value)> Members { get; }
        public ushort Id { get; }

        private EnumType(string name, bool isFlags, IReadOnlyList<(string, long)> members, ushort id)
        {
            Name = name;
            IsFlags = isFlags;
            Members = members;
            Id = id;
        }

        /// <summary>The type of this declaration (the same object for the same declaration).</summary>
        public static EnumType Intern(string name, bool isFlags, IReadOnlyList<(string Name, long Value)> members)
        {
            string key = (isFlags ? "flag " : "") + name + "{" + string.Join(",", members.Select(m => m.Name + "=" + m.Value)) + "}";
            lock (Lock)
            {
                if (ByKey.TryGetValue(key, out var known)) return known;
                if (All.Count > ushort.MaxValue) throw new InvalidOperationException("Too many enum types in one process.");
                var type = new EnumType(name, isFlags, members.ToList(), (ushort)All.Count);
                All.Add(type);
                ByKey[key] = type;
                return type;
            }
        }

        /// <summary>The value of a text: a member's name, for a flag enum names joined with `,` (`a, b`); null when it is none.</summary>
        public long? Parse(string text)
        {
            long result = 0;
            foreach (var part in IsFlags ? text.Split(',') : new[] { text })
            {
                string name = part.Trim();
                int i = Members.ToList().FindIndex(m => m.Name == name);
                if (i < 0) return null;
                result |= Members[i].Value;
            }
            return result;
        }

        /// <summary>The type with this id (null for 0 or an unknown id).</summary>
        public static EnumType? ById(ushort id)
        {
            if (id == 0) return null;
            lock (Lock) return id < All.Count ? All[id] : null;
        }

        /// <summary>The name of the member with this value (the first one), or null.</summary>
        public string? NameOf(long value)
        {
            foreach (var (name, v) in Members) if (v == value) return name;
            return null;
        }

        /// <summary>The text of a value: the name of its member; for a flag enum the names of the flags it combines (`a, b`, like .NET), the member with 0 for
        /// no flag; otherwise the number.</summary>
        public string Format(long value)
        {
            if (NameOf(value) is { } name) return name;
            if (IsFlags && value != 0)
            {
                var parts = new StringBuilder();
                long rest = value;
                foreach (var (memberName, v) in Members)
                {
                    if (v == 0 || (v & (v - 1)) != 0) continue;   // (only single bits: a combined member is shown through its flags)
                    if ((value & v) != v) continue;
                    if (parts.Length > 0) parts.Append(", ");
                    parts.Append(memberName);
                    rest &= ~v;
                }
                if (rest == 0 && parts.Length > 0) return parts.ToString();
            }
            return value.ToString();
        }

        /// <summary>Whether a type name as a program writes it means this enum: the full name, or the name without its namespace (`#using`, inside the namespace).</summary>
        public bool Matches(string typeName) => Name == typeName || Name.EndsWith("." + typeName, StringComparison.Ordinal);

        public override string ToString() => Name;
    }
}
