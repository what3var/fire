using System;
using fire.Bytecode;
using fire.Runtime;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// `probe`/`silence` (docs/DESIGN_LAMBDA_REFLECTION_PROBE.md, SPEC 8.14): handlers for write accesses to members of an object. An object with
    /// probes carries a <see cref="ProbeTable"/> and thus an <see cref="ObjectInstance.AccessGuard"/>: its write accesses go via the
    /// slow path (<see cref="SetFieldSlow"/>), all other objects keep the inline-cache fast paths.
    /// </summary>
    public sealed partial class VM
    {
        /// <summary>Calls a lambda nested (like <see cref="CallMethodNested"/>): an exception in it runs to the handlers of the caller.
        /// null if it was redirected there (no result).</summary>
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

        /// <summary>Handler arguments by parameter count: 0 none, 1 (new), 2 (old, new), 3 (object, old, new), 4 (object, name, old, new).</summary>
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

        /// <summary>`obj.member = value` on an object with probes for `member` (stack: obj, value): `changing` handlers (one that returns `false`
        /// aborts the write - the expression still evaluates to the assigned value), then the write, then - only if the
        /// value changed - the `changed` handlers. If a handler writes the same member, nothing fires again for it.</summary>
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

        /// <summary>Registers a probe. `member == null`: all members. false with a message if the target is not an object, the member is unknown or
        /// the handler is not a lambda with 0 to 4 parameters.</summary>
        public bool TryProbeAdd(Value target, string? member, bool changing, Value handler, out long id, out string error)
        {
            id = 0;
            error = "";
            if (target.Kind != ValueKind.Class) { error = $"'probe' expects an object, got: {target.Kind}."; return false; }
            if (handler.Kind != ValueKind.Lambda) { error = $"The handler of a probe must be a lambda, got: {handler.Kind}."; return false; }
            var obj = (ObjectInstance)target.AsObjectRef();
            var lambda = (LambdaValue)handler.AsLambda();
            if (lambda.Proto.ParamCount > 4) { error = $"The handler of a probe may have at most 4 parameters (object, name, old, new), it has {lambda.Proto.ParamCount}."; return false; }
            if (member != null && !ReflectHas(obj, member)) { error = $"'{obj.ClassName}' has no member '{member}' - no probe can be registered there."; return false; }

            id = ProbeRegistry.NextId();
            obj.GetOrCreateProbes().Add(new ProbeEntry { Id = id, Member = member, IsChanging = changing, Handler = lambda });
            ProbeRegistry.Register(id, obj);
            return true;
        }

        /// <summary>`silence obj.member` or `silence obj.*` (`member == null`: all probes of the object).</summary>
        public bool TrySilenceMember(Value target, string? member, out string error)
        {
            error = "";
            if (target.Kind != ValueKind.Class) { error = $"'silence' expects an object, got: {target.Kind}."; return false; }
            var probes = ((ObjectInstance)target.AsObjectRef()).Probes;
            if (probes != null) ProbeRegistry.Forget(member == null ? probes.RemoveAll() : probes.RemoveMember(member));
            return true;
        }

        /// <summary>`silence x`: a probe handle (int) removes exactly this probe, an object all its probes.</summary>
        public bool TrySilenceValue(Value value, out string error)
        {
            error = "";
            if (value.Kind == ValueKind.Class) return TrySilenceMember(value, null, out error);
            if (value.Kind == ValueKind.Int)
            {
                long id = value.AsInt();
                var owner = ProbeRegistry.OwnerOf(id);
                if (owner?.Probes != null && owner.Probes.Remove(id)) ProbeRegistry.Forget(new[] { id });
                return true; // an already removed handle is not an error
            }
            error = $"'silence' expects a probe handle or an object, got: {value.Kind}.";
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
