using System;
using fire.Bytecode;
using fire.Runtime;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// `probe`/`silence` (docs/DESIGN_LAMBDA_REFLECTION_PROBE.md, SPEC 8.14): Handler für Schreibzugriffe auf Mitglieder eines Objekts. Ein Objekt mit
    /// Proben trägt eine <see cref="ProbeTable"/> und damit einen <see cref="ObjectInstance.AccessGuard"/>: seine Schreibzugriffe laufen über den
    /// langsamen Pfad (<see cref="SetFieldSlow"/>), alle anderen Objekte behalten die Inline-Cache-Schnellpfade.
    /// </summary>
    public sealed partial class VM
    {
        /// <summary>Ruft eine Lambda verschachtelt auf (wie <see cref="CallMethodNested"/>): eine Exception darin läuft zu den Handlern des Aufrufers.
        /// null, wenn sie dorthin umgeleitet wurde (kein Ergebnis).</summary>
        private Value? CallLambdaNested(LambdaValue lambda, Value[] args)
        {
            CheckArity(lambda.Proto, args.Length);
            args = FillDefaultArgs(lambda.Proto, args, lambda.OnTarget);

            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;

            _frames.Push(new CallFrame(savedChunk, savedIp, savedScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var scope = new Scope(_globalScope);
            foreach (var a in args) scope.DefineSlot(a);
            if (lambda.Captures != null) foreach (var c in lambda.Captures) scope.DefineSlot(c);

            _currentThis = lambda.OnTarget;
            _currentScope = scope;
            _currentChunk = lambda.Proto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);

            bool completedNormally =
                ReferenceEquals(_currentChunk, savedChunk) && _ip == savedIp && ReferenceEquals(_currentScope, savedScope);
            return completedNormally ? Pop() : (Value?)null;
        }

        /// <summary>Handler-Argumente nach Parameterzahl: 0 keine, 1 (neu), 2 (alt, neu), 3 (Objekt, alt, neu), 4 (Objekt, Name, alt, neu).</summary>
        private Value? RunProbeHandler(ProbeEntry entry, ObjectInstance obj, string member, Value oldValue, Value newValue)
        {
            var handler = entry.Handler;
            Value[] args = handler.Proto.ParamCount switch
            {
                0 => Array.Empty<Value>(),
                1 => new[] { newValue },
                2 => new[] { oldValue, newValue },
                3 => new[] { Value.MakeClassRef(obj), oldValue, newValue },
                _ => new[] { Value.MakeClassRef(obj), Value.MakeString(member), oldValue, newValue },
            };
            return CallLambdaNested(handler, args);
        }

        /// <summary>`obj.member = value` auf einem Objekt mit Proben für `member` (Stack: obj, value): `changing`-Handler (einer, der `false`
        /// liefert, bricht das Schreiben ab - der Ausdruck wertet trotzdem zum zugewiesenen Wert aus), dann das Schreiben, dann - nur bei geändertem
        /// Wert - die `changed`-Handler. Schreibt ein Handler dasselbe Mitglied, feuert dafür nichts erneut.</summary>
        private bool SetFieldProbed(string name, ProbeTable probes)
        {
            var obj = (ObjectInstance)_stack[_sp - 2].AsObjectRef();
            var value = _stack[_sp - 1];
            if (!probes.TryBeginRunning(name)) return SetFieldSlowSections(name, -1);
            try
            {
                Value oldValue;
                if (obj.TryGetFieldLocked(name, out var current)) oldValue = current;
                else if (ResolveClass(obj.ClassName).FindMethod("get_" + name, 0) != null)
                {
                    var read = CallMethodNested(obj, "get_" + name, Array.Empty<Value>());
                    if (read == null) return false;
                    oldValue = read.Value;
                }
                else oldValue = Value.MakeUndefined();

                foreach (var entry in probes.Match(name, changing: true))
                {
                    var verdict = RunProbeHandler(entry, obj, name, oldValue, value);
                    if (verdict == null) return false;
                    if (verdict.Value.Kind == ValueKind.Bool && !verdict.Value.AsBool())
                    {
                        Pop(); Pop();
                        Push(value);
                        return true;
                    }
                }

                if (!SetFieldSlowSections(name, -1)) return false;

                if (!Value.ValuesEqual(oldValue, value))
                    foreach (var entry in probes.Match(name, changing: false))
                        if (RunProbeHandler(entry, obj, name, oldValue, value) == null) return false;
                return true;
            }
            finally { probes.EndRunning(name); }
        }

        /// <summary>Meldet eine Probe an. `member == null`: alle Mitglieder. false mit Meldung, wenn das Ziel kein Objekt, das Mitglied unbekannt oder
        /// der Handler keine Lambda mit 0 bis 4 Parametern ist.</summary>
        public bool TryProbeAdd(Value target, string? member, bool changing, Value handler, out long id, out string error)
        {
            id = 0;
            error = "";
            if (target.Kind != ValueKind.Class) { error = $"'probe' erwartet ein Objekt, erhalten: {target.Kind}."; return false; }
            if (handler.Kind != ValueKind.Lambda) { error = $"Der Handler einer Probe muss eine Lambda sein, erhalten: {handler.Kind}."; return false; }
            var obj = (ObjectInstance)target.AsObjectRef();
            var lambda = (LambdaValue)handler.AsLambda();
            if (lambda.Proto.ParamCount > 4) { error = $"Der Handler einer Probe darf höchstens 4 Parameter haben (Objekt, Name, alt, neu), hat {lambda.Proto.ParamCount}."; return false; }
            if (member != null && !ReflectHas(obj, member)) { error = $"'{obj.ClassName}' hat kein Mitglied '{member}' - dort lässt sich keine Probe anmelden."; return false; }

            id = ProbeRegistry.NextId();
            obj.GetOrCreateProbes().Add(new ProbeEntry { Id = id, Member = member, IsChanging = changing, Handler = lambda });
            ProbeRegistry.Register(id, obj);
            return true;
        }

        /// <summary>`silence obj.member` bzw. `silence obj.*` (`member == null`: alle Proben des Objekts).</summary>
        public bool TrySilenceMember(Value target, string? member, out string error)
        {
            error = "";
            if (target.Kind != ValueKind.Class) { error = $"'silence' erwartet ein Objekt, erhalten: {target.Kind}."; return false; }
            var probes = ((ObjectInstance)target.AsObjectRef()).Probes;
            if (probes != null) ProbeRegistry.Forget(member == null ? probes.RemoveAll() : probes.RemoveMember(member));
            return true;
        }

        /// <summary>`silence x`: ein Probe-Handle (int) entfernt genau diese Probe, ein Objekt alle seine Proben.</summary>
        public bool TrySilenceValue(Value value, out string error)
        {
            error = "";
            if (value.Kind == ValueKind.Class) return TrySilenceMember(value, null, out error);
            if (value.Kind == ValueKind.Int)
            {
                long id = value.AsInt();
                var owner = ProbeRegistry.OwnerOf(id);
                if (owner?.Probes != null && owner.Probes.Remove(id)) ProbeRegistry.Forget(new[] { id });
                return true; // ein schon entferntes Handle ist kein Fehler
            }
            error = $"'silence' erwartet ein Probe-Handle oder ein Objekt, erhalten: {value.Kind}.";
            return false;
        }

        private void OpProbe()
        {
            int nameIdx = ReadU16();
            int flags = ReadByte();
            var handler = Pop();
            var target = Pop();
            string? member = (flags & 2) != 0 ? null : _constants[nameIdx].AsString();
            if (!TryProbeAdd(target, member, (flags & 1) != 0, handler, out long id, out string error))
                throw new InvalidOperationException(error);
            Push(Value.MakeInt(id));
        }

        private void OpSilenceMember()
        {
            int nameIdx = ReadU16();
            int wildcard = ReadByte();
            var target = Pop();
            if (!TrySilenceMember(target, wildcard != 0 ? null : _constants[nameIdx].AsString(), out string error))
                throw new InvalidOperationException(error);
        }

        private void OpSilenceValue()
        {
            if (!TrySilenceValue(Pop(), out string error))
                throw new InvalidOperationException(error);
        }
    }
}
