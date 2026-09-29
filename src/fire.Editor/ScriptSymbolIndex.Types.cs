using System.Collections.Generic;
using System.Linq;
using fire.Lexing;

namespace fire.Editor
{
    public enum TypeKind
    {
        /// <summary>Nicht bestimmbar (dynamische Typisierung - der Normalfall
        /// für nicht typisierte Variablen, deren Wert sich nicht aus einem
        /// `new X(...)`/einer typisierten Methode ergibt).</summary>
        Unknown,

        /// <summary>Eine Instanz der Klasse `Name`.</summary>
        Instance,

        /// <summary>Die Klasse `Name` SELBST (`Name.Mitglied`) - nur statische
        /// Mitglieder.</summary>
        Static,

        /// <summary>Das Enum `Name` (`Name.Mitglied`).</summary>
        Enum,

        /// <summary>Ein einfacher Wert (`int`, `string`, ...) oder Enum-Wert.</summary>
        Primitive,

        /// <summary>Ein Array mit Elementtyp `Name`.</summary>
        Array,

        /// <summary>Der Namespace `Name` (vollqualifiziert) - `Name.` zeigt
        /// dessen Klassen/Enums/Unter-Namespaces.</summary>
        Namespace,
    }

    /// <summary>Der (best-effort) hergeleitete Typ eines Ausdrucks im Editor.
    /// `ViaThis`: der Ausdruck ist `this`/`base` selbst - dann sind auch
    /// statische Mitglieder über den Punkt erreichbar (siehe Resolver, "this.
    /// StaticMember").</summary>
    public sealed record ExprType(TypeKind Kind, string? Name = null, bool ViaThis = false)
    {
        public static readonly ExprType Unknown = new(TypeKind.Unknown);
    }

    /// <summary>
    /// Best-Effort-TYPHERLEITUNG für die Vervollständigung/Navigation - damit
    /// nach `x.` die Mitglieder der Klasse von `x` erscheinen statt beliebiger
    /// Mitglieder aller Klassen. Bewusst wie der Rest dieser Klasse
    /// token-basiert (siehe Klassen-Kommentar): keine echte Auflösung, aber
    /// deutlich mehr als "nur explizit typisierte Variablen":
    ///
    /// - Ausdrücke: `new X(...)`/`new X&lt;..&gt;(...)`, `new X[n]` (Array),
    ///   `this`/`base`, Variablen/Parameter/Felder (auch ohne `this.`),
    ///   Klassen-/Enum-Namen, Aufrufe/Feldzugriffe/Indizes darauf - beliebig
    ///   verkettet (`a.B().c[0].`).
    /// - Variablentyp: aus `var x : T`, `T x`, typisierten Parametern, `foreach
    ///   (T x in ...)` - oder, bei `var x = ausdruck`/`x = ausdruck`, aus dem
    ///   Typ des Ausdrucks (rekursiv, mit Tiefenlimit).
    /// - Mitgliedstypen: deklarierter Typ/Rückgabetyp; sonst bei Methoden aus
    ///   den `return`-Ausdrücken im Body, bei Feldern aus `= new X()` bzw.
    ///   einer Zuweisung `this.feld = ...` irgendwo in der Klasse.
    ///
    /// Alles, was sich so nicht bestimmen lässt, ist <see cref="TypeKind.Unknown"/>.
    /// </summary>
    public sealed partial class ScriptSymbolIndex
    {
        /// <summary>Maximale Rekursionstiefe der Herleitung (Variable aus
        /// Variable aus Methodenaufruf ...) - bricht auch Zyklen ab.</summary>
        private const int MaxDepth = 8;

        private static readonly HashSet<string> PrimitiveTypeNames = new() { "bool", "int", "float", "char", "string", "byte" };

        // -----------------------------------------------------------
        // Umschließende Funktion
        // -----------------------------------------------------------

        private sealed record FunctionContext(
            int BodyStartTokenIdx, int BodyStartOffset, int BodyEndOffset, List<(string Name, string? TypeName)> Params);

        /// <summary>Index der '{' des Bodys, der zur Signatur gehört, deren
        /// Parameterliste bei `afterParen` endet - oder -1, wenn dort gar
        /// keine Funktionsdefinition steht (sondern z.B. ein reiner Aufruf
        /// `foo(x)`, dem später zufällig irgendein '{' folgt). Erlaubt sind
        /// nur `: base(...)`, `=&gt;` und `on x` zwischen ')' und '{', und
        /// jeder Zeilenumbruch davor außer bei der '{' selbst beendet die
        /// Suche.</summary>
        private int FindBodyBrace(int afterParen)
        {
            int j = afterParen;
            while (j < _tokens.Count)
            {
                var t = _tokens[j];
                if (t.Type == TokenType.LBrace) return j;
                if (t.NewlineBefore || t.Type == TokenType.Eof) return -1;
                switch (t.Type)
                {
                    case TokenType.Colon: case TokenType.Arrow: case TokenType.On:
                    case TokenType.Base: case TokenType.This: case TokenType.Identifier: case TokenType.Dot:
                        j++;
                        break;
                    case TokenType.LParen:
                        int close = MatchForward(j, TokenType.LParen, TokenType.RParen);
                        if (close < 0) return -1;
                        j = close + 1;
                        break;
                    default:
                        return -1;
                }
            }
            return -1;
        }

        private List<FunctionContext>? _functions;

        /// <summary>Alle Funktionsdefinitionen (Methoden, Konstruktoren,
        /// Lambdas mit Block-Body) des Dokuments, in Quelltext-Reihenfolge -
        /// einmalig berechnet (die Typ-Herleitung fragt sehr oft danach).</summary>
        private List<FunctionContext> AllFunctions()
        {
            if (_functions != null) return _functions;
            var result = new List<FunctionContext>();
            for (int i = 0; i < _tokens.Count; i++)
            {
                var type = _tokens[i].Type;
                bool isKeyword = type is TokenType.Construct or TokenType.Func;
                bool isNamed = type == TokenType.Identifier
                    && (i == 0 || _tokens[i - 1].Type is not (TokenType.Dot or TokenType.New));
                if (!isKeyword && !isNamed) continue;

                // '<T>' einer generischen Methode zwischen Name und '('.
                int paren = i + 1;
                if (isNamed && paren < _tokens.Count && _tokens[paren].Type == TokenType.Lt)
                    paren = SkipAngleBrackets(paren);
                if (paren >= _tokens.Count || _tokens[paren].Type != TokenType.LParen) continue;

                var parms = ReadParamList(paren, out int afterParen);
                int open = FindBodyBrace(afterParen);
                if (open < 0) continue;

                int end = MatchBrace(open);
                result.Add(new FunctionContext(open, TokenOffset(open), TokenOffset(end) + 1, parms));
            }
            return _functions = result;
        }

        /// <summary>Die INNERSTE Funktion, deren Body `offset` enthält (bei
        /// Verschachtelung startet die innere später - der letzte Treffer).</summary>
        private FunctionContext? EnclosingFunction(int offset)
        {
            FunctionContext? best = null;
            foreach (var fn in AllFunctions())
                if (offset >= fn.BodyStartOffset && offset <= fn.BodyEndOffset)
                    best = fn;
            return best;
        }

        /// <summary>Parameter (Name, evtl. Typname) der unmittelbar
        /// umschließenden Methode/des Konstruktors/Lambdas an `offset` - die
        /// INNERSTE Funktionssignatur, deren Body `offset` enthält.</summary>
        public List<(string Name, string? TypeName)> EnclosingFunctionParams(int offset) =>
            EnclosingFunction(offset)?.Params ?? new List<(string, string?)>();

        // -----------------------------------------------------------
        // Öffentliche Einstiege
        // -----------------------------------------------------------

        /// <summary>Der Typ des Ausdrucks unmittelbar VOR dem '.' an
        /// `dotOffset` (Zeichen-Offset des Punkts) - `Unknown`, wenn der
        /// Punkt nicht gefunden oder der Ausdruck nicht herleitbar ist.</summary>
        public ExprType ResolveReceiver(int dotOffset)
        {
            int dotIdx = FindTokenIndex(dotOffset);
            if (dotIdx <= 0 || _tokens[dotIdx].Type != TokenType.Dot) return ExprType.Unknown;
            // 'this.'/'base.' behalten ViaThis (dort sind auch statische Mitglieder
            // erreichbar) - jede andere Herkunft nicht, siehe EvalExprRange.
            return EvalExprRange(FirstTokenOfChainEndingAt(dotIdx - 1), dotIdx - 1, 0, keepViaThis: true);
        }

        /// <summary>Der Typ eines einzelnen Bezeichners `name` an `offset`
        /// (lokale Variable/Parameter, Feld der umschließenden Klasse,
        /// Klassen-/Enum-Name).</summary>
        public ExprType ResolveIdentifier(int offset, string name) => ResolveName(name, offset, 0);

        /// <summary>Wie ResolveIdentifier, aber nur der Klassenname, wenn
        /// `name` eine INSTANZ einer bekannten Klasse ist - für
        /// NavigationEngine ("zu Definition springen").</summary>
        public string? TryResolveDeclaredType(int offset, string name)
        {
            var type = ResolveName(name, offset, 0);
            return type.Kind == TypeKind.Instance ? type.Name : null;
        }

        /// <summary>Namen der lokalen Variablen/Parameter, die an `offset`
        /// sichtbar sind: Deklarationen zwischen dem Beginn der unmittelbar
        /// umschließenden Funktion und `offset` (textuell, KEIN echtes
        /// Block-Scope-Tracking - eine Variable aus einem bereits
        /// verlassenen Geschwister-Block wird hier auch noch vorgeschlagen;
        /// bewusste Vereinfachung für Autovervollständigung).</summary>
        public List<string> LocalVarsBeforeCursor(int offset)
        {
            var result = new List<string>();
            var fn = EnclosingFunction(offset);
            int n = _tokens.Count;
            for (int k = fn?.BodyStartTokenIdx ?? 0; k < n - 1; k++)
            {
                if (TokenOffset(k) >= offset) break;
                var t = _tokens[k];
                string? declared = null;
                if (t.Type == TokenType.Var && _tokens[k + 1].Type == TokenType.Identifier)
                    declared = _tokens[k + 1].Lexeme;
                else if (t.Type == TokenType.Foreach && k + 3 < n && _tokens[k + 1].Type == TokenType.LParen
                         && _tokens[k + 2].Type == TokenType.Identifier && _tokens[k + 3].Type == TokenType.In)
                    declared = _tokens[k + 2].Lexeme; // 'foreach (name in ...)'
                else if ((t.Type == TokenType.Identifier || IsTypeKeyword(t.Type)) && _tokens[k + 1].Type == TokenType.Identifier
                         && (k == 0 || _tokens[k - 1].Type is not (TokenType.Dot or TokenType.New))
                         && !_tokens[k + 1].NewlineBefore
                         && (PrimitiveTypeNames.Contains(t.Lexeme) || ResolveClassKey(t.Lexeme, ContextAt(offset)) != null
                             || ResolveEnumKey(t.Lexeme, ContextAt(offset)) != null))
                    declared = _tokens[k + 1].Lexeme;

                if (declared != null && TokenOffset(k + 1) < offset && !result.Contains(declared))
                    result.Add(declared);
            }
            return result;
        }

        // -----------------------------------------------------------
        // Token-Hilfen
        // -----------------------------------------------------------

        private int FindTokenIndex(int offset)
        {
            int lo = 0, hi = _tokens.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int o = TokenOffset(mid);
                if (o == offset) return mid;
                if (o < offset) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }

        private int MatchForward(int openIdx, TokenType open, TokenType close)
        {
            int depth = 0;
            for (int j = openIdx; j < _tokens.Count; j++)
            {
                if (_tokens[j].Type == open) depth++;
                else if (_tokens[j].Type == close && --depth == 0) return j;
            }
            return -1;
        }

        private int MatchBackward(int closeIdx, TokenType open, TokenType close)
        {
            int depth = 0;
            for (int j = closeIdx; j >= 0; j--)
            {
                if (_tokens[j].Type == close) depth++;
                else if (_tokens[j].Type == open && --depth == 0) return j;
            }
            return -1;
        }

        // -----------------------------------------------------------
        // Ausdrücke als Ketten `a.b(...).c[...]`
        // -----------------------------------------------------------

        private enum SegKind { Name, Call, Index, New }

        private readonly record struct Seg(SegKind Kind, string Text);

        /// <summary>Liest die Kette, die mit dem Token `endIdx` ENDET, RÜCKWÄRTS
        /// (wo sie beginnt, steht erst danach fest): Bezeichner, `this`,
        /// `base`, Aufrufe `name(...)` (auch `new X&lt;..&gt;(...)`), Indizes
        /// `[...]`, verbunden durch '.'. `segs` in Quelltext-Reihenfolge,
        /// `startIdx` das erste Token der Kette. `false`, wenn das, was dort
        /// endet, keine solche Kette ist (Literal, Klammerausdruck,
        /// Operator ...).</summary>
        private bool TryParseChainBackward(int endIdx, out List<Seg> segs, out int startIdx)
        {
            var rev = new List<Seg>();
            segs = rev;
            startIdx = endIdx;
            int j = endIdx;

            while (true)
            {
                if (j < 0) return false;
                var t = _tokens[j];
                Seg seg;
                int segStart;

                if (t.Type == TokenType.RBracket)
                {
                    int open = MatchBackward(j, TokenType.LBracket, TokenType.RBracket);
                    if (open < 0) return false;
                    rev.Add(new Seg(SegKind.Index, string.Empty));
                    j = open - 1; // das Indizierte davor weiterlesen
                    continue;
                }

                if (t.Type == TokenType.RParen)
                {
                    int open = MatchBackward(j, TokenType.LParen, TokenType.RParen);
                    if (open < 0) return false;
                    int k = open - 1;
                    if (k >= 0 && _tokens[k].Type == TokenType.Gt) // 'new Box<int>(...)'
                    {
                        int lt = k;
                        int depth = 0;
                        for (; lt >= 0; lt--)
                        {
                            if (_tokens[lt].Type == TokenType.Gt) depth++;
                            else if (_tokens[lt].Type == TokenType.Lt && --depth == 0) break;
                            else if (_tokens[lt].Type is TokenType.LParen or TokenType.RParen) return false;
                        }
                        if (lt < 0) return false;
                        k = lt - 1;
                    }
                    if (k < 0 || _tokens[k].Type != TokenType.Identifier) return false; // Klammerausdruck '(...)' - nicht unterstützt
                    bool isNew = k > 0 && _tokens[k - 1].Type == TokenType.New;
                    seg = new Seg(isNew ? SegKind.New : SegKind.Call, _tokens[k].Lexeme);
                    segStart = isNew ? k - 1 : k;
                }
                else if (t.Type is TokenType.Identifier or TokenType.This or TokenType.Base)
                {
                    // 'new Name' (nur mit folgendem '[', siehe 'new X[n]') - steht
                    // hinter dem Namen aber ein '.', ist er der ANFANG eines
                    // qualifizierten Namens ('new Geometry.Circle(...)'), siehe
                    // MergeQualifiedNew.
                    bool isNew = t.Type == TokenType.Identifier && j > 0 && _tokens[j - 1].Type == TokenType.New
                        && !(j + 1 < _tokens.Count && _tokens[j + 1].Type == TokenType.Dot);
                    seg = new Seg(isNew ? SegKind.New : SegKind.Name, t.Lexeme);
                    segStart = isNew ? j - 1 : j;
                }
                else
                {
                    return false;
                }

                rev.Add(seg);
                startIdx = segStart;
                if (seg.Kind == SegKind.New) break;
                if (segStart - 1 >= 0 && _tokens[segStart - 1].Type == TokenType.Dot)
                {
                    j = segStart - 2;
                    continue;
                }
                break;
            }

            rev.Reverse();
            MergeQualifiedNew(rev, ref startIdx);
            return true;
        }

        /// <summary>`new Geometry.Circle(...)`/`new Geometry.Circle[n]`: der
        /// Rückwärts-Lauf sieht hier die Kette `Geometry` `.` `Circle(...)` und
        /// erst danach das `new` DAVOR - dann zu EINEM New-Segment mit dem
        /// qualifizierten Namen zusammenfassen.</summary>
        private void MergeQualifiedNew(List<Seg> segs, ref int startIdx)
        {
            if (startIdx <= 0 || _tokens[startIdx - 1].Type != TokenType.New) return;

            int core = segs.Count > 0 && segs[^1].Kind == SegKind.Index ? segs.Count - 1 : segs.Count;
            if (core < 2) return;
            for (int q = 0; q < core - 1; q++)
                if (segs[q].Kind != SegKind.Name || segs[q].Text is "this" or "base") return;
            var last = segs[core - 1];
            bool hasIndex = core < segs.Count;
            if (last.Kind != (hasIndex ? SegKind.Name : SegKind.Call)) return;

            string qualified = string.Join(".", segs.Take(core).Select(sg => sg.Text));
            var merged = new List<Seg> { new Seg(SegKind.New, qualified) };
            if (hasIndex) merged.Add(segs[^1]);
            segs.Clear();
            segs.AddRange(merged);
            startIdx--;
        }

        private int FirstTokenOfChainEndingAt(int endIdx) =>
            TryParseChainBackward(endIdx, out _, out int start) ? start : endIdx;

        /// <summary>Der Typ des Ausdrucks, der GENAU die Tokens `startIdx`..
        /// `endIdx` umfasst (steht dort mehr/etwas anderes als eine Kette
        /// oder ein Literal, ist das Ergebnis Unknown).</summary>
        private ExprType EvalExprRange(int startIdx, int endIdx, int depth, bool keepViaThis = false)
        {
            var type = EvalExprRangeCore(startIdx, endIdx, depth);
            // `var x = this` (oder eine Methode, die `this` zurückgibt) macht x
            // zu einer GEWÖHNLICHEN Instanz - `x.` erreicht keine statischen
            // Mitglieder, nur `this.` selbst.
            return type.ViaThis && !keepViaThis ? type with { ViaThis = false } : type;
        }

        private ExprType EvalExprRangeCore(int startIdx, int endIdx, int depth)
        {
            if (depth > MaxDepth || startIdx < 0 || startIdx > endIdx || endIdx >= _tokens.Count)
                return ExprType.Unknown;

            if (startIdx == endIdx)
            {
                switch (_tokens[startIdx].Type)
                {
                    case TokenType.StringLiteral: case TokenType.InterpolatedStringLiteral: return new ExprType(TypeKind.Primitive, "string");
                    case TokenType.IntLiteral: return new ExprType(TypeKind.Primitive, "int");
                    case TokenType.FloatLiteral: return new ExprType(TypeKind.Primitive, "float");
                    case TokenType.CharLiteral: return new ExprType(TypeKind.Primitive, "char");
                    case TokenType.True: case TokenType.False: return new ExprType(TypeKind.Primitive, "bool");
                }
            }

            if (!TryParseChainBackward(endIdx, out var segs, out int start) || start != startIdx)
                return ExprType.Unknown;
            return EvalChain(segs, TokenOffset(startIdx), depth);
        }

        private ExprType EvalChain(List<Seg> segs, int scopeOffset, int depth)
        {
            if (segs.Count == 0 || depth > MaxDepth) return ExprType.Unknown;

            ExprType cur;
            int next = 1;
            var root = segs[0];
            switch (root.Kind)
            {
                case SegKind.New:
                    {
                        var context = ContextAt(scopeOffset);
                        var created = FromTypeName(root.Text, isArray: false, context);
                        if (segs.Count > 1 && segs[1].Kind == SegKind.Index) // 'new X[n]'
                        {
                            created = FromTypeName(root.Text, isArray: true, context);
                            next = 2;
                        }
                        cur = created;
                        break;
                    }
                case SegKind.Name when root.Text == "this":
                    {
                        var cls = EnclosingClassAt(scopeOffset);
                        cur = cls != null ? new ExprType(TypeKind.Instance, cls, ViaThis: true) : ExprType.Unknown;
                        break;
                    }
                case SegKind.Name when root.Text == "base":
                    {
                        var cls = EnclosingClassAt(scopeOffset);
                        string? baseName = cls != null && Classes.TryGetValue(cls, out var info)
                            ? ResolvedBases(info).FirstOrDefault(b => !Classes[b].IsInterface)
                            : null;
                        cur = baseName != null ? new ExprType(TypeKind.Instance, baseName, ViaThis: true) : ExprType.Unknown;
                        break;
                    }
                case SegKind.Name:
                    cur = ResolveName(root.Text, scopeOffset, depth);
                    break;
                case SegKind.Call:
                    {
                        // Aufruf einer Methode der umschließenden Klasse ohne 'this.'
                        var cls = EnclosingClassAt(scopeOffset);
                        var method = cls == null ? null
                            : MembersOf(cls).FirstOrDefault(m => m.Kind == MemberKind.Method && m.Name == root.Text);
                        cur = method != null ? TypeOfMember(method, depth) : ExprType.Unknown;
                        break;
                    }
                default:
                    return ExprType.Unknown;
            }

            for (int i = next; i < segs.Count && cur.Kind != TypeKind.Unknown; i++)
            {
                var seg = segs[i];
                cur = seg.Kind switch
                {
                    SegKind.Name => MemberType(cur, seg.Text, isCall: false, depth),
                    SegKind.Call => MemberType(cur, seg.Text, isCall: true, depth),
                    SegKind.Index => ElementType(cur, depth),
                    _ => ExprType.Unknown,
                };
            }
            return cur;
        }

        private ExprType MemberType(ExprType receiver, string name, bool isCall, int depth)
        {
            switch (receiver.Kind)
            {
                case TypeKind.Instance:
                case TypeKind.Static:
                    {
                        var member = MembersOf(receiver.Name!).FirstOrDefault(m => m.Name == name
                            && (isCall ? m.Kind == MemberKind.Method : m.Kind is MemberKind.Field or MemberKind.Property));
                        return member != null ? TypeOfMember(member, depth) : ExprType.Unknown;
                    }
                case TypeKind.Enum:
                    return EnumMembers.TryGetValue(receiver.Name!, out var members) && members.Contains(name)
                        ? new ExprType(TypeKind.Primitive, "int")
                        : ExprType.Unknown;
                case TypeKind.Namespace:
                    {
                        // 'Geometry.Circle' (die Klasse selbst: statischer
                        // Zugriff), 'Geometry.Color' (Enum), 'Geometry.Inner'
                        // (Unter-Namespace) - Aufrufe gibt es auf einem
                        // Namespace nicht.
                        string full = receiver.Name + "." + name;
                        if (isCall) return ExprType.Unknown;
                        if (Classes.ContainsKey(full)) return new ExprType(TypeKind.Static, full);
                        if (EnumMembers.ContainsKey(full)) return new ExprType(TypeKind.Enum, full);
                        if (Namespaces.Contains(full)) return new ExprType(TypeKind.Namespace, full);
                        return ExprType.Unknown;
                    }
                default:
                    return ExprType.Unknown;
            }
        }

        /// <summary>Der Typ von `x[...]`: bei einem Array der Elementtyp, bei
        /// einer Instanz der Rückgabetyp ihres `operator[]`.</summary>
        private ExprType ElementType(ExprType container, int depth)
        {
            switch (container.Kind)
            {
                case TypeKind.Array:
                    return FromTypeName(container.Name!, isArray: false, System.Array.Empty<string>()); // Name ist schon ein Schlüssel
                case TypeKind.Instance:
                    {
                        var op = MembersOf(container.Name!).FirstOrDefault(m => m.Name == "operator[]");
                        return op != null ? TypeOfMember(op, depth) : ExprType.Unknown;
                    }
                default:
                    return ExprType.Unknown;
            }
        }

        /// <summary>Der Typ, den der Typname `name` (so geschrieben, evtl.
        /// qualifiziert) meint, aufgelöst gegen `context` (siehe
        /// ClassInfo.Context) - Klasse, Enum, oder ein einfacher Typ wie `int`.</summary>
        private ExprType FromTypeName(string? name, bool isArray, IReadOnlyList<string> context)
        {
            if (name == null) return ExprType.Unknown;
            if (PrimitiveTypeNames.Contains(name))
                return new ExprType(isArray ? TypeKind.Array : TypeKind.Primitive, name);

            string? classKey = ResolveClassKey(name, context);
            if (classKey != null)
                return new ExprType(isArray ? TypeKind.Array : TypeKind.Instance, classKey);

            string? enumKey = ResolveEnumKey(name, context);
            if (enumKey != null)
                return new ExprType(isArray ? TypeKind.Array : TypeKind.Primitive, enumKey);
            return ExprType.Unknown;
        }

        // -----------------------------------------------------------
        // Typ eines Mitglieds
        // -----------------------------------------------------------

        private ExprType TypeOfMember(MemberInfo member, int depth)
        {
            if (depth > MaxDepth) return ExprType.Unknown;

            // Typnamen in der Deklaration sind relativ zum Kontext der
            // deklarierenden Klasse geschrieben (Namespace + #using ihrer Datei).
            var ownerContext = Classes.TryGetValue(member.Owner, out var ownerInfo)
                ? ownerInfo.Context
                : System.Array.Empty<string>();
            var declared = FromTypeName(member.TypeName, member.TypeIsArray, ownerContext);
            if (declared.Kind != TypeKind.Unknown) return declared;

            // Kein (brauchbarer) deklarierter Typ - aus dem Body/den
            // Zuweisungen schließen. Immer im Index, dessen Token-Strom die
            // Indizes des Mitglieds meinen (bei Prelude-Klassen der der
            // Prelude, nicht dieser).
            var source = member.Source ?? this;
            return member.Kind switch
            {
                MemberKind.Method => source.InferReturnType(member, depth + 1),
                MemberKind.Field => source.InferFieldType(member, depth + 1),
                _ => ExprType.Unknown,
            };
        }

        /// <summary>Der Rückgabetyp einer Methode ohne deklarierten Typ: der
        /// Typ des ersten herleitbaren `return`-Ausdrucks im Body.</summary>
        internal ExprType InferReturnType(MemberInfo method, int depth)
        {
            if (depth > MaxDepth || method.BodyStart < 0) return ExprType.Unknown;
            for (int k = method.BodyStart + 1; k < method.BodyEnd; k++)
            {
                if (_tokens[k].Type != TokenType.Return) continue;
                int start = k + 1;
                if (start >= method.BodyEnd || _tokens[start].NewlineBefore) continue; // 'return' ohne Wert
                int end = ExpressionEnd(start, method.BodyEnd);
                var type = EvalExprRange(start, end - 1, depth + 1);
                if (type.Kind != TypeKind.Unknown) return type;
            }
            return ExprType.Unknown;
        }

        /// <summary>Der Typ eines Feldes ohne deklarierten Typ: der Typ der
        /// ersten herleitbaren Zuweisung `this.feld = ausdruck` irgendwo in
        /// der Klasse (typisch: im Konstruktor).</summary>
        internal ExprType InferFieldType(MemberInfo field, int depth)
        {
            if (depth > MaxDepth || !Classes.TryGetValue(field.Owner, out var owner)) return ExprType.Unknown;
            foreach (var (start, end) in owner.BodyRanges)
                for (int k = start + 1; k + 3 < end; k++)
                {
                    if (_tokens[k].Type != TokenType.This || _tokens[k + 1].Type != TokenType.Dot
                        || _tokens[k + 2].Type != TokenType.Identifier || _tokens[k + 2].Lexeme != field.Name
                        || _tokens[k + 3].Type != TokenType.Assign)
                        continue;
                    int valueStart = k + 4;
                    int valueEnd = ExpressionEnd(valueStart, end);
                    var type = EvalExprRange(valueStart, valueEnd - 1, depth + 1);
                    if (type.Kind != TypeKind.Unknown) return type;
                }
            return ExprType.Unknown;
        }

        // -----------------------------------------------------------
        // Bezeichner: lokale Variablen, Parameter, Felder, Klassen, Enums
        // -----------------------------------------------------------

        private ExprType ResolveName(string name, int offset, int depth)
        {
            if (depth > MaxDepth) return ExprType.Unknown;

            var local = FindLocal(name, offset, depth, out bool found);
            if (found) return local;

            // Feld/Property der umschließenden Klasse (SPEC "Implizite
            // Mitglieder-Referenzen": auch ohne 'this.' ansprechbar).
            var enclosing = EnclosingClassAt(offset);
            if (enclosing != null)
            {
                var member = MembersOf(enclosing).FirstOrDefault(
                    m => m.Name == name && m.Kind is MemberKind.Field or MemberKind.Property);
                if (member != null) return TypeOfMember(member, depth);
            }

            // Klassen-/Enum-/Namespace-Namen als Ausdruck (`Name.Mitglied`):
            // wie beim echten Compiler NUR der exakt geschriebene, ggf. schon
            // vollqualifizierte Name (siehe Resolver.TryResolveStaticMemberAccess
            // - keine Auflösung über `#using`/den aktuellen Namespace).
            if (Classes.ContainsKey(name)) return new ExprType(TypeKind.Static, name);
            if (EnumMembers.ContainsKey(name)) return new ExprType(TypeKind.Enum, name);
            if (Namespaces.Contains(name)) return new ExprType(TypeKind.Namespace, name);
            return ExprType.Unknown;
        }

        private bool IsStatementStart(int k) =>
            k == 0 || _tokens[k].NewlineBefore
            || _tokens[k - 1].Type is TokenType.Semicolon or TokenType.LBrace or TokenType.RBrace
                or TokenType.RParen or TokenType.Else;

        /// <summary>Der Typ der lokalen Variable/des Parameters `name` an
        /// `offset`, aus der LETZTEN passenden Deklaration davor in der
        /// umschließenden Funktion (Top-Level-Code: im ganzen Dokument);
        /// `found` sagt, ob es überhaupt eine Deklaration gab (auch wenn der
        /// Typ Unknown bleibt - dann soll KEIN gleichnamiges Feld/keine
        /// gleichnamige Klasse einspringen).</summary>
        private ExprType FindLocal(string name, int offset, int depth, out bool found)
        {
            found = false;
            var result = ExprType.Unknown;
            bool explicitType = false;

            var context = ContextAt(offset);
            var fn = EnclosingFunction(offset);
            if (fn != null)
                foreach (var (paramName, paramType) in fn.Params)
                    if (paramName == name)
                    {
                        found = true;
                        result = FromTypeName(paramType, isArray: false, context);
                        explicitType = result.Kind != TypeKind.Unknown;
                    }

            int n = _tokens.Count;
            for (int k = fn?.BodyStartTokenIdx ?? 0; k < n; k++)
            {
                if (TokenOffset(k) >= offset) break;
                var t = _tokens[k];

                // 'var name [: einheit] [= ausdruck]' - hinter dem ':' steht nur
                // eine EINHEIT (einen Typ gibt man als 'T name' an, nicht als
                // 'var name : T'), der Typ kommt also allein aus dem
                // Initialisierer. Außer als Schleifenvariable von 'foreach (var
                // name in ...)', die der Fall darunter (beim 'foreach'-Token
                // selbst) schon vollständig behandelt hat.
                if (t.Type == TokenType.Var && k + 1 < n && _tokens[k + 1].Type == TokenType.Identifier
                    && _tokens[k + 1].Lexeme == name
                    && !(k >= 2 && _tokens[k - 1].Type == TokenType.LParen && _tokens[k - 2].Type == TokenType.Foreach))
                {
                    found = true;
                    result = ExprType.Unknown;
                    explicitType = false;
                    int statementEnd = ExpressionEnd(k + 1, n);
                    for (int q = k + 2; q < statementEnd; q++)
                        if (_tokens[q].Type == TokenType.Assign)
                        {
                            result = EvalExprRange(q + 1, ExpressionEnd(q + 1, n) - 1, depth + 1);
                            break;
                        }
                    continue;
                }

                // foreach (name in ausdruck) - so schreibt es die Sprache (SPEC 8.5);
                // ein zusätzliches 'var' davor wird ebenfalls erkannt.
                int loopVar = t.Type == TokenType.Foreach && k + 3 < n && _tokens[k + 1].Type == TokenType.LParen
                    ? (_tokens[k + 2].Type == TokenType.Var ? k + 3 : k + 2)
                    : -1;
                if (loopVar > 0 && loopVar + 1 < n && _tokens[loopVar].Type == TokenType.Identifier
                    && _tokens[loopVar].Lexeme == name && _tokens[loopVar + 1].Type == TokenType.In)
                {
                    found = true;
                    explicitType = false;
                    int close = MatchForward(k + 1, TokenType.LParen, TokenType.RParen);
                    var iterable = close > loopVar + 2 ? EvalExprRange(loopVar + 2, close - 1, depth + 1) : ExprType.Unknown;
                    result = iterable.Kind == TypeKind.Array
                        ? FromTypeName(iterable.Name, isArray: false, System.Array.Empty<string>()) // Name ist schon ein Schlüssel
                        : ExprType.Unknown;
                    continue;
                }

                // 'T name' (auch 'foreach (T name in ...)', 'catch (T name)'), T
                // evtl. qualifiziert ('Geometry.Circle name').
                if ((t.Type == TokenType.Identifier || IsTypeKeyword(t.Type)) && k + 1 < n
                    && _tokens[k + 1].Type == TokenType.Identifier && _tokens[k + 1].Lexeme == name
                    && !_tokens[k + 1].NewlineBefore)
                {
                    int typeStart = k;
                    while (typeStart >= 2 && _tokens[typeStart - 1].Type == TokenType.Dot
                           && _tokens[typeStart - 2].Type == TokenType.Identifier)
                        typeStart -= 2;
                    if (typeStart == 0 || _tokens[typeStart - 1].Type is not (TokenType.Dot or TokenType.New))
                    {
                        found = true;
                        bool isArray = k + 2 < n && _tokens[k + 2].Type == TokenType.LBracket;
                        string typeText = string.Concat(_tokens.Skip(typeStart).Take(k - typeStart + 1).Select(tok => tok.Lexeme));
                        result = FromTypeName(typeText, isArray, context);
                        explicitType = result.Kind != TypeKind.Unknown;
                        continue;
                    }
                }

                // name = ausdruck - nur als Anweisung, und nur, wenn der Typ
                // nicht ausdrücklich deklariert ist (dann wäre er maßgeblich).
                if (!explicitType && t.Type == TokenType.Identifier && t.Lexeme == name
                    && k + 1 < n && _tokens[k + 1].Type == TokenType.Assign && IsStatementStart(k))
                {
                    var assigned = EvalExprRange(k + 2, ExpressionEnd(k + 2, n) - 1, depth + 1);
                    if (assigned.Kind != TypeKind.Unknown)
                    {
                        found = true;
                        result = assigned;
                    }
                }
            }

            return result;
        }
    }
}
