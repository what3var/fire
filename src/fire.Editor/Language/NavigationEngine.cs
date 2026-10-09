using System;
using System.Collections.Generic;
using System.Linq;

namespace fire.Editor
{
    /// <summary>Result of a click navigation (see NavigationEngine.
    /// TryResolve). `FilePath`: `null` means "target lies in the CURRENT
    /// document itself" (only `Line` relevant); a set path is the
    /// RELATIVE path from an `#include` line - MainWindow resolves it
    /// relative to the directory of the file currently being shown (see
    /// there, the only place that knows the actual file path).
    /// `PreludeName`: set if the target lies in a built-in prelude
    /// (standard library or an extension switched on via `#import`,
    /// see ScriptSymbolIndex.PreludeSourceOf) - then
    /// `FilePath` is ALWAYS null (no real file path available) and `Line`
    /// refers to the PRELUDE source, NOT to the current
    /// document; in this case the caller shows the prelude in a
    /// read-only window (FileViewerWindow.ShowPrelude) instead of a
    /// normal jump in the editor.</summary>
    public sealed record NavigationTarget(string? FilePath, int Line, string? PreludeName = null)
    {
        public bool IsPrelude => PreludeName != null;
    }

    /// <summary>What <see cref="NavigationEngine.TryResolveSymbol"/> found at a position: where it is declared
    /// (<see cref="Target"/>) and, if it is a class or a member, the symbol itself - for its documentation comment.</summary>
    public sealed record ResolvedSymbol(NavigationTarget Target, ClassInfo? Class = null, MemberInfo? Member = null)
    {
        /// <summary>The `///` documentation of the member or class, null if there is none.</summary>
        public DocComment? Documentation => Member?.Documentation ?? Class?.Documentation;

        /// <summary>A one-line description of the symbol, e.g. `(method) Calc.Add(int a, int b) → int`.</summary>
        public string Header
        {
            get
            {
                if (Member is { } m)
                {
                    string type = m.TypeName != null ? m.TypeName + (m.TypeIsArray ? "[]" : "") : string.Empty;
                    string owner = m.Owner.Length > 0 ? m.Owner + "." : string.Empty;
                    return m.Kind switch
                    {
                        MemberKind.Method => $"(method) {owner}{m.Name}({m.Signature}){(type.Length > 0 ? " → " + type : "")}",
                        MemberKind.Constructor => $"(constructor) new {m.Owner}({m.Signature})",
                        MemberKind.Property => $"(property) {owner}{m.Name}{(type.Length > 0 ? " : " + type : "")}",
                        _ => $"(field) {owner}{m.Name}{(type.Length > 0 ? " : " + type : "")}",
                    };
                }
                if (Class is { } c)
                    return (c.IsInterface ? "interface " : "class ") + c.SimpleName + (c.BaseNames.Count > 0 ? " : " + string.Join(", ", c.BaseNames) : "");
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Determines where a click on a certain character position in the
    /// source should jump to - best-effort like the rest of the editor tools
    /// (ScriptSymbolIndex/CompletionEngine), TOKEN-/TEXT-based instead of via
    /// the real resolver (which would need complete, valid source
    /// and would deliver nothing at every typo).
    ///
    /// Recognises three cases, in this order:
    /// 1. Click on an `#include "path"` line -> the target is this file
    ///    (line 1, since the WHOLE file is meant, no particular symbol
    ///    in it).
    /// 2. Click on an identifier after a `.` (`receiver.Name`) ->
    ///    tries to determine the TYPE of `receiver` (as with
    ///    auto-completion: `this`, typed variable/parameter -
    ///    see ScriptSymbolIndex.TryResolveDeclaredType/EnclosingClassAt)
    ///    and to look up `Name` as its member.
    /// 3. Click on a "bare" identifier -> first tried as a known
    ///    class/enum name, otherwise as a member of the CURRENTLY
    ///    enclosing class (implicit `this.Name` call).
    ///
    /// Returns `null` if nothing suitable is found in the CURRENT document
    /// - in this case MainWindow additionally tries every
    /// included file (see ExtractIdentifierAndReceiver/
    /// TryResolveInOtherFile, there without offset context, since the click offset
    /// from THIS document would be meaningless in ANOTHER file).
    /// </summary>
    public static class NavigationEngine
    {
        public static NavigationTarget? TryResolve(string source, int offset, ScriptSymbolIndex index) =>
            TryResolveSymbol(source, offset, index)?.Target;

        /// <summary>Like <see cref="TryResolve"/>, but also returns the class/member that was found (for its documentation).</summary>
        public static ResolvedSymbol? TryResolveSymbol(string source, int offset, ScriptSymbolIndex index)
        {
            if (offset < 0 || offset > source.Length) return null;

            int line = LineOf(source, offset);
            var include = index.IncludeDirectives.FirstOrDefault(d => d.Line == line);
            if (include != null) return new ResolvedSymbol(new NavigationTarget(include.RelativePath, 1));

            string? identifier = ReadIdentifierAt(source, offset, out int idStart);
            if (identifier == null) return null;
            string? receiver = ReadIdentifierBeforeDot(source, idStart);
            // Behind a ')' / ']' (`a.B().c`) ReadIdentifierBeforeDot knows no name - the dot counts anyway.
            if (receiver == null && idStart > 0 && source[idStart - 1] == '.') receiver = "";

            if (receiver != null)
            {
                // First via the full type derivation of completion (`a.B().c.`, `var x = new T()`,
                // namespaces, static calls ...), then via the simple resolution of the receiver.
                // `Geo.Circle` / `A.B.Circle` as a type name (e.g. behind `new` or in a declaration): the written
                // path, fully qualified or via #using.
                string? dotted = ReadDottedNameBefore(source, idStart - 1);
                if (dotted != null)
                {
                    string qualified = dotted + "." + identifier;
                    string? qualifiedClass = index.TryFindClass(qualified, offset);
                    if (qualifiedClass != null && index.Classes.TryGetValue(qualifiedClass, out var qc) && qc.DeclLine > 0)
                        return new ResolvedSymbol(ClassTarget(qc), qc);
                    string? qualifiedEnum = index.TryFindEnum(qualified, offset);
                    if (qualifiedEnum != null && index.EnumDeclLines.ContainsKey(qualifiedEnum))
                        return new ResolvedSymbol(EnumTarget(index, qualifiedEnum));
                }

                var type = index.ResolveReceiver(idStart - 1);
                var viaType = ResolveMemberOfType(index, type, identifier);
                if (viaType != null) return viaType;
                if (receiver.Length == 0) return null;

                string? className = receiver == "this"
                    ? index.EnclosingClassAt(offset)
                    : index.TryResolveDeclaredType(offset, receiver);
                // 'ClassName.Member' (unusual, but covered) - the class name
                // as written, also from another namespace/via #using.
                className ??= index.TryFindClass(receiver, offset);

                if (className != null)
                {
                    var member = FindMember(index, className, identifier);
                    if (member != null) return MemberSymbol(index, member);
                }

                string? receiverEnum = index.TryFindEnum(receiver, offset);
                if (receiverEnum != null && enumMembersContain(index, receiverEnum, identifier))
                    return new ResolvedSymbol(EnumTarget(index, receiverEnum));

                return null;
            }

            string? classKey = index.TryFindClass(identifier, offset);
            if (classKey != null && index.Classes.TryGetValue(classKey, out var cls) && cls.DeclLine > 0)
                return new ResolvedSymbol(ClassTarget(cls), cls);

            string? enumKey = index.TryFindEnum(identifier, offset);
            if (enumKey != null && index.EnumDeclLines.ContainsKey(enumKey))
                return new ResolvedSymbol(EnumTarget(index, enumKey));

            string? enclosing = index.EnclosingClassAt(offset);
            if (enclosing != null)
            {
                var member = FindMember(index, enclosing, identifier);
                if (member != null) return MemberSymbol(index, member);
            }

            return null;
        }

        // -----------------------------------------------------------
        // Calls: the documentation shown while the arguments of a call are being typed
        // -----------------------------------------------------------

        /// <summary>An open call at the caret: the innermost `(` that is not closed yet, and how many arguments have been started.</summary>
        /// <param name="ParenOffset">Offset of the `(`.</param>
        /// <param name="RequiredArgs">How many parameters the called overload needs at least: 0 right after `(`, otherwise the number of arguments started so far.</param>
        public sealed record OpenCall(int ParenOffset, int RequiredArgs);

        /// <summary>Finds the call whose argument list the caret is in (null if the caret is not inside any parentheses of the current statement).
        /// Strings, characters and comments are skipped; a `;`, `{` or `}` ends the search.</summary>
        public static OpenCall? FindOpenCall(string source, int caret)
        {
            if (caret < 0 || caret > source.Length) return null;
            int limit = Math.Max(0, caret - 3000);
            int start = limit;
            for (int i = caret - 1; i >= limit; i--)
            {
                if (source[i] is ';' or '{' or '}') { start = i + 1; break; }
            }

            var open = new List<(int Pos, int Commas, bool HasText)>();
            void MarkText()
            {
                if (open.Count > 0) open[^1] = (open[^1].Pos, open[^1].Commas, true);
            }

            for (int i = start; i < caret; i++)
            {
                char c = source[i];
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    while (i < caret && source[i] != quote && source[i] != '\n')
                    {
                        if (source[i] == '\\') i++;
                        i++;
                    }
                    MarkText();
                }
                else if (c == '/' && i + 1 < caret && source[i + 1] == '/')
                {
                    while (i < caret && source[i] != '\n') i++;
                }
                else if (c == '/' && i + 1 < caret && source[i + 1] == '*')
                {
                    int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (close < 0 || close + 1 >= caret) return null; // inside an unfinished block comment
                    i = close + 1;
                }
                else if (c == '(')
                {
                    MarkText();
                    open.Add((i, 0, false));
                }
                else if (c == ')')
                {
                    if (open.Count > 0) open.RemoveAt(open.Count - 1);
                    MarkText();
                }
                else if (c == ',' && open.Count > 0)
                {
                    open[^1] = (open[^1].Pos, open[^1].Commas + 1, false);
                }
                else if (!char.IsWhiteSpace(c))
                {
                    MarkText();
                }
            }

            if (open.Count == 0) return null;
            var (pos, commas, hasText) = open[^1];
            return new OpenCall(pos, commas > 0 || hasText ? commas + 1 : 0);
        }

        /// <summary>The symbol being called at an open call: for `new Foo(` the (documented) constructor that fits the arguments typed so far,
        /// otherwise the class; for `obj.Method(` the documented overload that fits. Null if the name before the `(` is not a known symbol.</summary>
        public static ResolvedSymbol? TryResolveCall(string source, OpenCall call, ScriptSymbolIndex index)
        {
            int end = call.ParenOffset;
            while (end > 0 && char.IsWhiteSpace(source[end - 1])) end--;
            if (end <= 0 || !IsIdentChar(source[end - 1])) return null;

            var symbol = TryResolveSymbol(source, end - 1, index);
            if (symbol == null) return null;

            // `new Foo(` / `new Geo.Foo(`: the word before the (dotted) name is `new`.
            int nameStart = end;
            while (nameStart > 0 && (IsIdentChar(source[nameStart - 1]) || source[nameStart - 1] == '.')) nameStart--;
            int before = nameStart;
            while (before > 0 && char.IsWhiteSpace(source[before - 1])) before--;
            bool isNew = before >= 3 && source.Substring(before - 3, 3) == "new" && (before == 3 || !IsIdentChar(source[before - 4]));

            if (isNew && symbol.Class is { } cls)
            {
                var constructors = cls.Members.Where(m => m.Kind == MemberKind.Constructor && m.Documentation != null).ToList();
                var chosen = PickOverload(constructors, call.RequiredArgs);
                return chosen != null ? new ResolvedSymbol(symbol.Target, null, chosen) : symbol;
            }

            if (symbol.Member is { Kind: MemberKind.Method } method)
            {
                var overloads = index.MembersOf(method.Owner).Where(m => m.Name == method.Name && m.Kind == MemberKind.Method && m.Documentation != null).ToList();
                var chosen = PickOverload(overloads, call.RequiredArgs);
                return chosen != null ? new ResolvedSymbol(new NavigationTarget(chosen.Source?.FilePath, chosen.DeclLine, chosen.Source?.PreludeName), null, chosen) : symbol;
            }

            return symbol;
        }

        /// <summary>Of several overloads: the one with the fewest parameters that still has at least `required`, otherwise the one with the most.</summary>
        private static MemberInfo? PickOverload(List<MemberInfo> overloads, int required) =>
            overloads.Where(m => m.ParamCount >= required).OrderBy(m => m.ParamCount).FirstOrDefault()
            ?? overloads.OrderByDescending(m => m.ParamCount).FirstOrDefault();

        private static bool enumMembersContain(ScriptSymbolIndex index, string enumKey, string name) =>
            index.EnumMembers.TryGetValue(enumKey, out var members) && members.Contains(name);

        /// <summary>The member `name` of the class `className` (also an inherited one), with known declaration line.</summary>
        private static MemberInfo? FindMember(ScriptSymbolIndex index, string className, string name) =>
            index.MembersOf(className).FirstOrDefault(m => m.Name == name && m.DeclLine > 0);

        /// <summary>Target of a member: if it comes from a prelude (also an inherited one of a prelude base class), the
        /// target is there - not in the clicked class.</summary>
        private static NavigationTarget MemberTarget(ScriptSymbolIndex index, MemberInfo member) =>
            new(member.Source?.FilePath, member.DeclLine, member.Source?.PreludeName);

        private static ResolvedSymbol MemberSymbol(ScriptSymbolIndex index, MemberInfo member) =>
            new(MemberTarget(index, member), null, member);

        /// <summary>Target of an enum (individual members have no line of their own).</summary>
        private static NavigationTarget EnumTarget(ScriptSymbolIndex index, string enumKey)
        {
            index.EnumPreludes.TryGetValue(enumKey, out var prelude);
            index.EnumFiles.TryGetValue(enumKey, out var file);
            return new NavigationTarget(file, index.EnumDeclLines[enumKey], prelude);
        }

        /// <summary>Where a class is declared: in the document (no file), in a prelude, or in another file of the project.</summary>
        private static NavigationTarget ClassTarget(ClassInfo c) => new(c.Source?.FilePath, c.DeclLine, c.PreludeName);

        /// <summary>The member `identifier` for a derived receiver type (see ScriptSymbolIndex.ResolveReceiver).</summary>
        private static ResolvedSymbol? ResolveMemberOfType(ScriptSymbolIndex index, ExprType type, string identifier)
        {
            switch (type.Kind)
            {
                case TypeKind.Instance:
                case TypeKind.Static:
                    {
                        var member = FindMember(index, type.Name!, identifier);
                        return member == null ? null : MemberSymbol(index, member);
                    }
                case TypeKind.Enum:
                    return enumMembersContain(index, type.Name!, identifier) && index.EnumDeclLines.ContainsKey(type.Name!)
                        ? new ResolvedSymbol(EnumTarget(index, type.Name!)) : null;
                case TypeKind.Namespace:
                    {
                        // `Geometry.Circle`: a class/an enum in the namespace.
                        string full = type.Name + "." + identifier;
                        if (index.Classes.TryGetValue(full, out var cls) && cls.DeclLine > 0)
                            return new ResolvedSymbol(ClassTarget(cls), cls);
                        if (index.EnumDeclLines.ContainsKey(full)) return new ResolvedSymbol(EnumTarget(index, full));
                        return null;
                    }
                case TypeKind.Primitive:
                case TypeKind.Array:
                    {
                        // Methods from `class extends string { ... }` (prelude and own extensions).
                        if (BuiltinMembers.ExtensionClassOf(type) is { } key && index.Classes.ContainsKey(key))
                        {
                            var member = FindMember(index, key, identifier);
                            if (member != null) return MemberSymbol(index, member);
                        }
                        return null;
                    }
                default:
                    return null;
            }
        }

        /// <summary>Identifier + (if present) the receiver in front of a
        /// '.' at the click position - public, so that MainWindow, when
        /// TryResolve fails, can try the same resolution against the
        /// symbol indices of OTHER (included) files,
        /// WITHOUT using the (there meaningless) offset/scope information from
        /// THIS document again (see TryResolveInOtherFile).</summary>
        public static (string Identifier, string? Receiver)? ExtractIdentifierAndReceiver(string source, int offset)
        {
            string? identifier = ReadIdentifierAt(source, offset, out int idStart);
            if (identifier == null) return null;
            string? receiver = ReadIdentifierBeforeDot(source, idStart);
            return (identifier, receiver);
        }

        /// <summary>Like the core of TryResolve, but for ANOTHER file
        /// than the one that was clicked in - therefore WITHOUT offset-dependent
        /// cases ('this', enclosing class, typed local variable):
        /// `receiver` is interpreted here exclusively as a LITERAL class name,
        /// a `receiver == null` only as a direct class/
        /// enum name, NOT as an implicit `this.identifier` call (the scope
        /// context is missing for that in a foreign file).
        ///
        /// Deliberately EXCLUDES hits from the built-in standard library
        /// (see ClassInfo.IsFromPrelude) - EVERY ScriptSymbolIndex
        /// (also that of an included file) now automatically contains it
        /// (see ScriptSymbolIndex.MergeInPrelude); the return type
        /// here, however, is only a pure line number WITHOUT the possibility
        /// of signalling "this is actually the prelude, not this file"
        /// as the NavigationTarget.IsPrelude field of the
        /// main resolution (TryResolve) does - a prelude hit would therefore
        /// here wrongly be interpreted as a line INSIDE the foreign file.
        /// Until this gets a signalling of its own, rather
        /// NO jump than a WRONG one.</summary>
        public static int? TryResolveInOtherFile(string identifier, string? receiver, ScriptSymbolIndex otherIndex)
        {
            if (receiver != null)
            {
                // Without offset context: the name as written (possibly via the
                // #using of the other file, or as the only hit).
                string? receiverKey = otherIndex.TryFindClass(receiver, -1);
                if (receiverKey != null && otherIndex.Classes.TryGetValue(receiverKey, out var receiverClass) && !receiverClass.IsFromPrelude)
                {
                    var member = otherIndex.MembersOf(receiverKey).FirstOrDefault(m => m.Name == identifier && m.DeclLine > 0);
                    if (member != null) return member.DeclLine;
                }
                string? receiverEnum = otherIndex.TryFindEnum(receiver, -1);
                if (receiverEnum != null
                    && otherIndex.EnumDeclLines.TryGetValue(receiverEnum, out var enumLine)
                    && otherIndex.EnumMembers.TryGetValue(receiverEnum, out var enumMembers)
                    && enumMembers.Contains(identifier))
                    return enumLine;
                return null;
            }

            string? classKey = otherIndex.TryFindClass(identifier, -1);
            if (classKey != null && otherIndex.Classes.TryGetValue(classKey, out var cls) && cls.DeclLine > 0 && !cls.IsFromPrelude)
                return cls.DeclLine;
            string? enumKey = otherIndex.TryFindEnum(identifier, -1);
            if (enumKey != null && otherIndex.EnumDeclLines.TryGetValue(enumKey, out var directEnumLine))
                return directEnumLine;

            return null;
        }

        private static int LineOf(string source, int offset)
        {
            int line = 1;
            for (int i = 0; i < offset && i < source.Length; i++)
                if (source[i] == '\n') line++;
            return line;
        }

        private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Reads the identifier AT or IMMEDIATELY BEFORE `offset`
        /// (covers both "click in the middle of the word" and "click directly
        /// behind the last character of the word" - both are equally to be
        /// expected with a mouse click).</summary>
        private static string? ReadIdentifierAt(string source, int offset, out int idStart)
        {
            idStart = 0;
            int start = offset;
            while (start > 0 && IsIdentChar(source[start - 1])) start--;
            int end = offset;
            while (end < source.Length && IsIdentChar(source[end])) end++;
            if (start >= end) return null;
            if (char.IsDigit(source[start])) return null; // starts with a digit - not a valid identifier
            idStart = start;
            return source.Substring(start, end - start);
        }

        /// <summary>The identifier path joined by dots immediately before `dotIdx` (the '.'): `A.B` for `A.B.Name`,
        /// null if there is no pure path before it (e.g. `f().Name`).</summary>
        private static string? ReadDottedNameBefore(string source, int dotIdx)
        {
            int end = dotIdx;
            int start = end;
            while (start > 0 && (IsIdentChar(source[start - 1]) || source[start - 1] == '.')) start--;
            if (start >= end) return null;
            string path = source.Substring(start, end - start);
            if (path.StartsWith('.') || path.EndsWith('.') || path.Contains("..") || char.IsDigit(path[0])) return null;
            return path;
        }

        private static string? ReadIdentifierBeforeDot(string source, int idStart)
        {
            if (idStart <= 0 || source[idStart - 1] != '.') return null;
            int end = idStart - 1;
            int start = end;
            while (start > 0 && IsIdentChar(source[start - 1])) start--;
            if (start >= end) return null;
            return source.Substring(start, end - start);
        }
    }
}
