using System;
using System.Collections.Generic;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Die nativen Funktionen hinter der Reflection-Bibliothek (`#import "reflection"`, fire-Seite: <c>fire.Standard.ReflectionPrelude</c>).
    /// Alles, was Werte liest/schreibt/aufruft, geht über die VM des aufrufenden Threads (<see cref="VM.ReflectGet"/> &amp; Co.) und damit
    /// durch dieselben Prüfungen wie normaler Code; die Beschreibungen (Klassen, Mitglieder) entstehen hier aus den <see cref="RuntimeClass"/>-
    /// Daten und den vom Compiler mitgeschriebenen <see cref="ClassMeta"/>.
    /// </summary>
    public static class ReflectionNatives
    {
        public static void Register(NativeRegistry natives)
        {
            natives.Register("__refl_class_name", a => ClassName(a[0]));
            natives.Register("__refl_class_info", a => ClassInfo(a[0].AsString()));
            natives.Register("__refl_members", a => Members(a[0].AsString()));
            natives.Register("__refl_classes", a => Classes());
            natives.Register("__refl_is_sub", a => Value.MakeBool(IsSub(a[0].AsString(), a[1].AsString())));
            natives.Register("__refl_get", a => Vm().ReflectGet(a[0], a[1].AsString()));
            natives.Register("__refl_set", a => Vm().ReflectSet(a[0], a[1].AsString(), a[2]));
            natives.Register("__refl_call", a => Vm().ReflectCall(a[0], a[1].AsString(), ToArgs(a[2])));
            natives.Register("__refl_new", a => Vm().ReflectNew(a[0].AsString(), ToArgs(a[1])));
            natives.Register("__refl_has", a => a[0].Kind == ValueKind.Class
                ? Value.MakeBool(Vm().ReflectHas((ObjectInstance)a[0].AsObjectRef(), a[1].AsString()))
                : Value.MakeBool(false));
            natives.Register("__refl_selector_path", a => SelectorPath(a[0]));
            natives.Register("__refl_member_kind", a => MemberKind(a[0], a[1].AsString()));
            natives.Register("__refl_probe", a => Probe(a[0], a[1], a[2].AsString(), a[3]));
            natives.Register("__refl_silence", a => Silence(a[0], a[1]));
            natives.Register("__refl_silence_handle", a => SilenceHandle(a[0]));
        }

        private static VM Vm() =>
            VM.CurrentThreadVm ?? throw new InvalidOperationException("Reflection ist nur auf dem Thread einer laufenden VM möglich.");

        private static Value[] ToArgs(Value array)
        {
            if (array.Kind != ValueKind.Array) return Array.Empty<Value>();
            return (Value[])array.AsArray().Items.Clone();
        }

        private static Value Str(string s) => Value.MakeString(s);

        private static Value StringArray(IReadOnlyList<string> items)
        {
            var arr = new ScriptArray(items.Count);
            for (int i = 0; i < items.Count; i++) arr.Items[i] = Str(items[i]);
            return Value.MakeArray(arr);
        }

        private static Value ClassName(Value x)
        {
            if (x.Kind == ValueKind.Class) return Str(((ObjectInstance)x.AsObjectRef()).ClassName);
            if (x.Kind == ValueKind.String && Vm().ReflectFindClass(x.AsString()) is { } rc) return Str(rc.Name);
            return Value.MakeUndefined();
        }

        private static Value ClassInfo(string name)
        {
            var rc = Vm().ReflectFindClass(name);
            if (rc == null) return Value.MakeUndefined();
            var interfaces = new List<string>();
            if (rc.Meta != null)
                foreach (var b in rc.Meta.BaseNames)
                    if (rc.Base == null || !(rc.Base.Name == b || rc.Base.Name.EndsWith("." + b, StringComparison.Ordinal))) interfaces.Add(b);
            var info = new ScriptArray(4);
            info.Items[0] = Str(rc.Name);
            info.Items[1] = rc.Base != null ? Str(rc.Base.Name) : Value.MakeUndefined();
            info.Items[2] = Value.MakeBool(rc.IsActor);
            info.Items[3] = StringArray(interfaces);
            return Value.MakeArray(info);
        }

        private static Value Classes()
        {
            var names = new List<string>(Vm().ReflectClassNames());
            names.Sort(StringComparer.Ordinal);
            return StringArray(names);
        }

        private static bool IsSub(string name, string baseName)
        {
            for (var rc = Vm().ReflectFindClass(name); rc != null; rc = rc.Base)
                if (rc.Name == baseName) return true;
            return false;
        }

        /// <summary>Alle Mitglieder der Klasse samt geerbter (die abgeleitete Klasse verdeckt gleichnamige der Basis; Konstruktoren nur die eigenen),
        /// je als Array [Name, Art, Typ, Zugriff, static, readonly, lesbar, schreibbar, Einheit, deklariert in, Parameternamen, Parametertypen].</summary>
        private static Value Members(string className)
        {
            var rc = Vm().ReflectFindClass(className);
            if (rc == null) return Value.MakeUndefined();
            var seen = new HashSet<string>();
            var result = new List<Value>();
            for (var c = rc; c != null; c = c.Base)
            {
                if (c.Meta == null) continue;
                foreach (var m in c.Meta.Members)
                {
                    if (m.Kind == "constructor")
                    {
                        if (!ReferenceEquals(c, rc)) continue;
                    }
                    else
                    {
                        string key = m.Kind == "method" ? "m:" + m.Name + "/" + m.ParamNames.Count : "f:" + m.Name;
                        if (!seen.Add(key)) continue;
                    }
                    var d = new ScriptArray(12);
                    d.Items[0] = Str(m.Name);
                    d.Items[1] = Str(m.Kind);
                    d.Items[2] = Str(m.TypeName);
                    d.Items[3] = Str(m.Access);
                    d.Items[4] = Value.MakeBool(m.IsStatic);
                    d.Items[5] = Value.MakeBool(m.IsReadonly);
                    d.Items[6] = Value.MakeBool(m.CanRead);
                    d.Items[7] = Value.MakeBool(m.CanWrite);
                    d.Items[8] = Str(m.Unit);
                    d.Items[9] = Str(c.Name);
                    d.Items[10] = StringArray(m.ParamNames);
                    d.Items[11] = StringArray(m.ParamTypes);
                    result.Add(Value.MakeArray(d));
                }
            }
            var arr = new ScriptArray(result.Count);
            for (int i = 0; i < result.Count; i++) arr.Items[i] = result[i];
            return Value.MakeArray(arr);
        }

        /// <summary>`Reflect.Probe`: dasselbe wie `probe obj.name changed|changing handler` (`name` undefined = alle Mitglieder); liefert das Handle.</summary>
        private static Value Probe(Value obj, Value member, string kind, Value handler)
        {
            var vm = Vm();
            if (kind is not ("changed" or "changing"))
                return vm.ReflectFail($"Die Art einer Probe ist \"changed\" oder \"changing\", erhalten: \"{kind}\".");
            if (!vm.TryProbeAdd(obj, member.Kind == ValueKind.String ? member.AsString() : null, kind == "changing", handler, out long id, out string error))
                return vm.ReflectFail(error);
            return Value.MakeInt(id);
        }

        private static Value Silence(Value obj, Value member)
        {
            var vm = Vm();
            return vm.TrySilenceMember(obj, member.Kind == ValueKind.String ? member.AsString() : null, out string error)
                ? Value.MakeUndefined()
                : vm.ReflectFail(error);
        }

        private static Value SilenceHandle(Value handle)
        {
            var vm = Vm();
            return vm.TrySilenceValue(handle, out string error) ? Value.MakeUndefined() : vm.ReflectFail(error);
        }

        /// <summary>"field" (ein Feld der Instanz), "property" (Accessor), "method" oder undefined - ohne einen Wert zu lesen.</summary>
        private static Value MemberKind(Value obj, string name)
        {
            if (obj.Kind != ValueKind.Class) return Value.MakeUndefined();
            var instance = (ObjectInstance)obj.AsObjectRef();
            if (instance.HasFieldLocked(name)) return Str("field");
            var rc = Vm().ReflectFindClass(instance.ClassName);
            if (rc == null) return Value.MakeUndefined();
            if (rc.FindMethod("get_" + name, 0) != null || rc.FindMethod("set_" + name, 1) != null) return Str("property");
            for (var c = rc; c != null; c = c.Base)
                if (c.Methods.ContainsKey(name)) return Str("method");
            return Value.MakeUndefined();
        }

        private static Value SelectorPath(Value l)
        {
            if (l.Kind != ValueKind.Lambda)
                return Vm().ReflectFail($"Ein Selektor ('lambda member<...>' o.ä.) erwartet eine Lambda wie `c => c.radius`, erhalten: {l.Kind}.");
            var path = ((LambdaValue)l.AsLambda()).Proto.SelectorPath;
            if (path == null)
                return Vm().ReflectFail("Die Lambda ist kein Selektor: sie braucht genau einen Parameter, und ihr Körper darf nur eine Mitgliedskette darauf sein (`c => c.radius`, `p => p.address.city`).");
            return StringArray(path);
        }
    }
}
