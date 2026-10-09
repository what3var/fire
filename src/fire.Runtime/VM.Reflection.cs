using System;
using System.Collections.Generic;
using fire.Ast;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// The VM's side of the reflection library (`#import "reflection"`, see <see cref="ReflectionNatives"/> and
    /// docs/DESIGN_LAMBDA_REFLECTION_PROBE.md): accesses to members by name go via the same paths as normal code
    /// (access check, units, property accessors, sections of the globals) - except that the "caller" for private/protected accesses is the
    /// code that called the library (<see cref="ReflectionCallerClass"/>).
    /// </summary>
    public sealed partial class VM
    {
        /// <summary>A native function raised an exception that was already redirected to a handler: the call returns no
        /// result (see CallNativeGuarded).</summary>
        private bool _nativeRedirected;

        /// <summary>The class of the code that called the reflection library (null: code outside any class).</summary>
        private RuntimeClass? ReflectionCallerClass()
        {
            foreach (var frame in _frames)
            {
                var rc = frame.ReturnChunk.OwnerClass;
                if (rc is not { IsReflectionHelper: true }) return rc;
            }
            return null;
        }

        /// <summary>Throws a `ReflectionException` (catchable) and remembers the redirection for the native call.</summary>
        internal Value ReflectFail(string message) => NativeFail("ReflectionException", message);

        /// <summary>Raises, from within a NATIVE function, a catchable script exception of the class `className` (with the constructor `(message)`) and
        /// remembers the redirection: the native call then returns no result (see CallNativeGuarded).</summary>
        internal Value NativeFail(string className, string message)
        {
            var instance = ConstructNested(ResolveClass(className), new[] { Value.MakeString(message) });
            ThrowException(Value.MakeClassRef(instance));
            _nativeRedirected = true;
            return default;
        }

        private bool ReflectRequireObject(Value target, string what, out ObjectInstance obj)
        {
            if (target.Kind == ValueKind.Class)
            {
                obj = (ObjectInstance)target.AsObjectRef();
                return true;
            }
            obj = null!;
            ReflectFail($"{what}: expects an object, got: {target.Kind}.");
            return false;
        }

        public RuntimeClass? ReflectFindClass(string name) => _classes.TryGetValue(name, out var rc) ? rc : null;

        public IEnumerable<string> ReflectClassNames() => _classes.Keys;

        /// <summary>Does the object have a member of this name (field, property accessor or method)?</summary>
        public bool ReflectHas(ObjectInstance obj, string name)
        {
            if (obj.HasFieldLocked(name)) return true;
            var rc = ResolveClass(obj.ClassName);
            return rc.FindMethod("get_" + name, 0) != null || rc.FindMethod("set_" + name, 1) != null || rc.Methods.ContainsKey(name) || HasInheritedMethod(rc, name);
        }

        private static bool HasInheritedMethod(RuntimeClass rc, string name)
        {
            for (var c = rc; c != null; c = c.Base)
                if (c.Methods.ContainsKey(name)) return true;
            return false;
        }

        public Value ReflectGet(Value target, string name)
        {
            if (!ReflectRequireObject(target, "Reflect.Get", out var obj)) return default;
            var rc = ResolveClass(obj.ClassName);
            if (!obj.HasFieldLocked(name) && rc.FindMethod("get_" + name, 0) == null)
                return ReflectFail($"'{obj.ClassName}' has no readable member '{name}'.");
            Push(target);
            if (!GetFieldSlowCore(name, -1)) { _nativeRedirected = true; return default; }
            return Pop();
        }

        public Value ReflectSet(Value target, string name, Value value)
        {
            if (!ReflectRequireObject(target, "Reflect.Set", out var obj)) return default;
            var rc = ResolveClass(obj.ClassName);
            if (!obj.HasFieldLocked(name) && rc.FindMethod("set_" + name, 1) == null)
                return ReflectFail(rc.FindMethod("get_" + name, 0) != null
                    ? $"The property '{name}' of '{obj.ClassName}' has no setter (only 'get')."
                    : $"'{obj.ClassName}' has no writable member '{name}'.");
            for (var c = rc; c != null; c = c.Base)
            {
                if (c.Meta == null) continue;
                foreach (var m in c.Meta.Members)
                    if (m.Kind == "field" && m.Name == name && !m.IsStatic)
                    {
                        if (m.IsReadonly) return ReflectFail($"The field '{name}' of '{c.Name}' is 'readonly' and cannot be assigned.");
                        goto checkedReadonly;
                    }
            }
            checkedReadonly:
            Push(target);
            Push(value);
            if (!SetFieldSlow(name, -1)) { _nativeRedirected = true; return default; }
            Pop();
            return Value.MakeUndefined();
        }

        public Value ReflectCall(Value target, string name, Value[] args)
        {
            if (!ReflectRequireObject(target, "Reflect.Call", out var obj)) return default;
            var rc = ResolveClass(obj.ClassName);
            var proto = rc.FindMethodWithAccess(name, args.Length).Item1;
            if (proto == null)
                return ReflectFail($"'{obj.ClassName}' has no method '{name}' with {args.Length} parameter(s).");
            if (proto.IsStatic)
                return ReflectFail($"'{name}' is a static method - Reflect.Call only calls instance methods.");
            var result = _threadBroker != null && _sectionDepth == 0 && obj.InGlobalsDomain
                ? CallGlobalsMethodInSection(obj, name, args)
                : CallMethodNested(obj, name, args);
            if (result == null) { _nativeRedirected = true; return default; }
            return result.Value;
        }

        public Value ReflectNew(string className, Value[] args)
        {
            if (!_classes.TryGetValue(className, out var rc))
                return ReflectFail($"Unknown class '{className}'.");
            var ctor = rc.FindConstructor(args.Length);
            if (ctor == null)
                return ReflectFail($"'{className}' has no constructor with {args.Length} parameter(s).");
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(rc, ctor.Access ?? AccessModifier.Public))
            {
                ThrowAccessDenied($"The constructor of '{className}' is {DescribeAccess(ctor.Access ?? AccessModifier.Public)} and cannot be called from here.");
                _nativeRedirected = true;
                return default;
            }
            return Value.MakeClassRef(ConstructNested(rc, args));
        }
    }
}
