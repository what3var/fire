using System;
using System.Collections.Generic;
using fire.Ast;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Die Seite der VM für die Reflection-Bibliothek (`#import "reflection"`, siehe <see cref="ReflectionNatives"/> und
    /// docs/DESIGN_LAMBDA_REFLECTION_PROBE.md): Zugriffe auf Mitglieder nach Namen laufen über dieselben Pfade wie normaler Code
    /// (Zugriffsprüfung, Einheiten, Property-Accessoren, Sektionen der Globals) - nur dass der "Aufrufer" für private/protected-Zugriffe der
    /// Code ist, der die Bibliothek aufgerufen hat (<see cref="ReflectionCallerClass"/>).
    /// </summary>
    public sealed partial class VM
    {
        /// <summary>Eine native Funktion hat eine Exception ausgelöst, die schon in einen Handler umgeleitet wurde: der Aufruf liefert kein
        /// Ergebnis (siehe CallNativeGuarded).</summary>
        private bool _nativeRedirected;

        /// <summary>Die Klasse des Codes, der die Reflection-Bibliothek aufgerufen hat (null: Code außerhalb jeder Klasse).</summary>
        private RuntimeClass? ReflectionCallerClass()
        {
            foreach (var frame in _frames)
            {
                var rc = frame.ReturnChunk.OwnerClass;
                if (rc is not { IsReflectionHelper: true }) return rc;
            }
            return null;
        }

        /// <summary>Wirft eine `ReflectionException` (fangbar) und merkt die Umleitung für den nativen Aufruf.</summary>
        internal Value ReflectFail(string message) => NativeFail("ReflectionException", message);

        /// <summary>Löst aus einer NATIVEN Funktion heraus eine fangbare Skript-Exception der Klasse `className` (mit dem Konstruktor `(message)`) aus und
        /// merkt die Umleitung: der native Aufruf liefert dann kein Ergebnis (siehe CallNativeGuarded).</summary>
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
            ReflectFail($"{what}: erwartet ein Objekt, erhalten: {target.Kind}.");
            return false;
        }

        public RuntimeClass? ReflectFindClass(string name) => _classes.TryGetValue(name, out var rc) ? rc : null;

        public IEnumerable<string> ReflectClassNames() => _classes.Keys;

        /// <summary>Hat das Objekt ein Mitglied dieses Namens (Feld, Property-Accessor oder Methode)?</summary>
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
                return ReflectFail($"'{obj.ClassName}' hat kein lesbares Mitglied '{name}'.");
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
                    ? $"Die Property '{name}' von '{obj.ClassName}' hat keinen Setter (nur 'get')."
                    : $"'{obj.ClassName}' hat kein beschreibbares Mitglied '{name}'.");
            for (var c = rc; c != null; c = c.Base)
            {
                if (c.Meta == null) continue;
                foreach (var m in c.Meta.Members)
                    if (m.Kind == "field" && m.Name == name && !m.IsStatic)
                    {
                        if (m.IsReadonly) return ReflectFail($"Das Feld '{name}' von '{c.Name}' ist 'readonly' und lässt sich nicht zuweisen.");
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
                return ReflectFail($"'{obj.ClassName}' hat keine Methode '{name}' mit {args.Length} Parameter(n).");
            if (proto.IsStatic)
                return ReflectFail($"'{name}' ist eine statische Methode - Reflect.Call ruft nur Instanzmethoden auf.");
            var result = _threadBroker != null && _sectionDepth == 0 && obj.InGlobalsDomain
                ? CallGlobalsMethodInSection(obj, name, args)
                : CallMethodNested(obj, name, args);
            if (result == null) { _nativeRedirected = true; return default; }
            return result.Value;
        }

        public Value ReflectNew(string className, Value[] args)
        {
            if (!_classes.TryGetValue(className, out var rc))
                return ReflectFail($"Unbekannte Klasse '{className}'.");
            var ctor = rc.FindConstructor(args.Length);
            if (ctor == null)
                return ReflectFail($"'{className}' hat keinen Konstruktor mit {args.Length} Parameter(n).");
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(rc, ctor.Access ?? AccessModifier.Public))
            {
                ThrowAccessDenied($"Der Konstruktor von '{className}' ist {DescribeAccess(ctor.Access ?? AccessModifier.Public)} und von hier aus nicht aufrufbar.");
                _nativeRedirected = true;
                return default;
            }
            return Value.MakeClassRef(ConstructNested(rc, args));
        }
    }
}
