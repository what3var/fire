using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Who hears about a reference that outlives its object (the VM in Debug mode, see <see cref="DanglingWatch"/>).</summary>
    public interface IDanglingReporter
    {
        void ReportDangling(ObjectInstance dying, object holder, string? field);
    }

    /// <summary>
    /// The debug check for references that outlive their object. An object that belongs to a call (a local variable, an argument passed with `take`)
    /// dies when the call ends - also when a field or a list was given only a reference to it (`this.sources.Add(source)` without `take`). Then the
    /// next use of that field throws a DestroyedException, far away from the cause.
    /// In Debug mode the VM notes each store of such an object into a field or an array (<see cref="Note"/>); when the object dies, the holders that
    /// still point at it and live on are reported - at the end of the call that destroys it.
    /// </summary>
    public static class DanglingWatch
    {
        /// <summary>`target` was stored in a field (`field`) of the object `holder`, or in the array `holder` (field null).</summary>
        public static void Note(ObjectInstance target, object holder, string? field)
        {
            if (target.IsDestroyed || ReferenceEquals(target, holder)) return;
            if (ScopeOf(target) is not { IsGlobal: false } scope) return;
            // a holder that dies with the same scope is no problem (a local list of local objects)
            if (DiesWith(holder, scope)) return;
            target.AddReferrer(holder, field);
        }

        /// <summary>The scope the object dies with: the first scope up its chain of owners (null: it belongs to no scope, e.g. it was moved to a global).</summary>
        internal static Scope? ScopeOf(object node)
        {
            for (object? n = node; n != null; n = Up(n))
                if (n is Scope s) return s;
            return null;
        }

        private static object? Up(object node) => node switch
        {
            ObjectInstance o => o.Owner is DeadOwner ? null : o.Owner,
            ScriptArray a => a.LeafOwner,
            ByteBuffer b => b.LeafOwner,
            _ => null,
        };

        /// <summary>Does `holder` die no later than `scope` (it is destroyed already, or it belongs to that scope or a scope inside it)?</summary>
        internal static bool DiesWith(object holder, Scope scope)
        {
            for (object? n = holder; n != null; n = Up(n))
            {
                if (n is ObjectInstance { IsDestroyed: true } || n is ScriptArray { IsDestroyed: true }) return true;
                if (n is Scope s)
                {
                    for (Scope? p = s; p != null; p = p.Parent)
                        if (ReferenceEquals(p, scope)) return true;
                    return false;
                }
            }
            return false;
        }

        /// <summary>Does `holder` still point at `target`?</summary>
        internal static bool StillHolds(object holder, string? field, ObjectInstance target)
        {
            if (holder is ObjectInstance o)
                return field != null && o.Fields.TryGetValue(field, out var v) && v.Kind == ValueKind.Class && ReferenceEquals(v.AsObjectRef(), target);
            if (holder is ScriptArray a)
            {
                foreach (var item in a.Items)
                    if (item.Kind == ValueKind.Class && ReferenceEquals(item.AsObjectRef(), target)) return true;
            }
            return false;
        }

        /// <summary>Called when `dying` is destroyed (before it leaves its owner): reports the holders that still point at it and live on.</summary>
        internal static void Check(ObjectInstance dying, List<(object Holder, string? Field)> referrers, IDestructRunner runner)
        {
            if (runner is not IDanglingReporter reporter) return;
            if (ScopeOf(dying) is not { } scope) return;
            foreach (var (holder, field) in referrers)
                if (!DiesWith(holder, scope) && StillHolds(holder, field, dying)) reporter.ReportDangling(dying, holder, field);
        }

        /// <summary>The text of the warning.</summary>
        public static string Describe(ObjectInstance dying, object holder, string? field)
        {
            string where = holder switch
            {
                ObjectInstance o => $"the field '{field}' of {A(o.ClassName)}",
                ScriptArray { LeafOwner: ObjectInstance owner } array => ArrayPlace(array, owner),
                _ => "a list or array",
            };
            return $"{A(dying.ClassName)} is destroyed at the end of the call it belonged to, but {where} still points to it " +
                   "(it was given only a reference: pass the object on with `take`, e.g. `list.Add(take item)`, or let a longer-living owner take it).";
        }

        // "the array in the field 'slots' of a Shelf", or for the array inside a list: "the List in the field 'items' of a Shelf"
        private static string ArrayPlace(ScriptArray array, ObjectInstance owner)
        {
            if (owner.ClassName.StartsWith("List", StringComparison.Ordinal) && owner.Owner is ObjectInstance) return Place(owner);
            foreach (var kv in owner.Fields)
                if (kv.Value.Kind == ValueKind.Array && ReferenceEquals(kv.Value.AsArray(), array))
                    return owner.ClassName.StartsWith("List", StringComparison.Ordinal) ? A(owner.ClassName) : $"the array in the field '{kv.Key}' of {A(owner.ClassName)}";
            return $"an array of {A(owner.ClassName)}";
        }

        // "the List in the field 'items' of a Shelf"
        private static string Place(ObjectInstance obj)
        {
            if (obj.Owner is ObjectInstance parent)
                foreach (var kv in parent.Fields)
                    if (kv.Value.Kind == ValueKind.Class && ReferenceEquals(kv.Value.AsObjectRef(), obj))
                        return $"the {obj.ClassName} in the field '{kv.Key}' of {A(parent.ClassName)}";
            return A(obj.ClassName);
        }

        private static string A(string name) => (name.Length > 0 && "AEIOUaeiou".IndexOf(name[0]) >= 0 ? "an " : "a ") + name;
    }
}
