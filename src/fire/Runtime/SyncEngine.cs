using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Result of sync/try sync/sync flat/try sync flat (see
    /// docs/THREADING_DESIGN.md section 4.1) - `LockBusy` can occur only with the
    /// `try` variants (the blocking variants wait
    /// instead until the lock is free). With the later connection
    /// to the language it becomes 1:1 true/false/undefined.</summary>
    public enum SyncResult
    {
        Success,
        LockBusy,
        TargetGone,
    }

    /// <summary>
    /// Implements `sync`/`try sync`/`sync flat`/`try sync flat` (see
    /// docs/THREADING_DESIGN.md section 4). `copy` must be a copy created via
    /// ObjectCopier.Take (SyncOrigin set) - `sync`
    /// ALWAYS writes from the copy (source) to the original (target), never
    /// the other way round, last-writer-wins, no conflict resolution.
    /// </summary>
    public static class SyncEngine
    {
        public static SyncResult Sync(ObjectInstance copy, bool blocking, IDestructRunner? runner = null) =>
            Run(copy, blocking, flat: false, runner ?? NullDestructRunner.Instance);

        public static SyncResult SyncFlat(ObjectInstance copy, bool blocking, IDestructRunner? runner = null) =>
            Run(copy, blocking, flat: true, runner ?? NullDestructRunner.Instance);

        private static SyncResult Run(ObjectInstance copy, bool blocking, bool flat, IDestructRunner runner)
        {
            var target = copy.SyncOrigin
                ?? throw new System.InvalidOperationException(
                    "sync: this object is not a 'taking' copy (no SyncOrigin set).");
            var treeLock = target.ThreadLock
                ?? throw new System.InvalidOperationException(
                    "sync: internal error - the target has no ThreadLock (it should have been set by 'taking').");

            if (blocking) treeLock.Enter();
            else if (!treeLock.TryEnter()) return SyncResult.LockBusy;

            try
            {
                // Check only AFTER acquiring the lock (not before) - a
                // concurrent Destroy could otherwise happen exactly between the check and
                // acquiring the lock.
                if (target.IsDestroyed) return SyncResult.TargetGone;

                SyncFields(copy, target, flat, runner);
                return SyncResult.Success;
            }
            finally
            {
                treeLock.Exit();
            }
        }

        /// <summary>Transfers all fields from `source` (the copy/the
        /// respective subtree node of the copy) to `target` (original) -
        /// with `flat: false` (full sync) called again recursively for case C,
        /// with `flat: true` (sync flat) exactly once, without descending into
        /// existing object references.</summary>
        private static void SyncFields(ObjectInstance source, ObjectInstance target, bool flat, IDestructRunner runner)
        {
            // Snapshot of the source fields - source belongs exclusively
            // to the calling thread (it is ITS OWN taking copy), a copy of the
            // enumeration is only necessary here if source itself should later (with
            // a nested taking) be shared after all.
            foreach (var (name, srcVal) in new List<KeyValuePair<string, Value>>(source.Fields))
            {
                target.Fields.TryGetValue(name, out var tgtVal);
                target.Fields[name] = SyncSingleValue(srcVal, tgtVal, target, flat, runner);
            }
        }

        /// <summary>A single field/array-element value, according to case A/B/C
        /// (see docs/THREADING_DESIGN.md 4.3) for object references, with
        /// special handling for arrays (4.4) and primitive values (taken
        /// over directly).</summary>
        private static Value SyncSingleValue(
            Value srcVal, Value tgtVal, ObjectInstance containingTarget, bool flat, IDestructRunner runner)
        {
            bool srcIsObj = srcVal.Kind == ValueKind.Class;
            bool tgtIsObj = tgtVal.Kind == ValueKind.Class;

            if (srcIsObj && tgtIsObj)
            {
                // Case C: both present - reference stays in place. With a
                // full (non-flat) sync the synchronisation
                // NEVERTHELESS descends recursively into both (that is the only
                // difference between 'sync' and 'sync flat' - flat leaves
                // case C completely untouched, full sync keeps syncing here
                // too).
                var srcChild = (ObjectInstance)srcVal.AsObjectRef();
                var tgtChild = (ObjectInstance)tgtVal.AsObjectRef();
                if (!flat)
                    SyncFields(srcChild, tgtChild, flat: false, runner);
                return tgtVal;
            }

            if (srcIsObj && !tgtIsObj)
            {
                // Case A: source present, target not - create a complete,
                // independent copy (NO reference link to
                // the source - from now on the two diverge again, until the
                // next sync). The owner of the new copy is the object in
                // whose field it lands. It must join the shared tree lock
                // (ActivateThreadSharing) - it is from now on part of
                // the already shared target tree, every future access
                // must therefore respect the same lock as the rest of the
                // tree, otherwise exactly at this point an
                // unguarded gap would arise.
                var srcChild = (ObjectInstance)srcVal.AsObjectRef();
                var newCopy = PlainDeepCopy(srcChild, containingTarget);
                newCopy.ActivateThreadSharing(containingTarget.ThreadLock!);
                return Value.MakeClassRef(newCopy);
            }

            if (!srcIsObj && tgtIsObj)
            {
                // Case B: source empty, target had an object - target is
                // emptied (takes over srcVal, usually 'undefined'). If the
                // referenced object thereby loses its (only) owner, the
                // destruct cascade runs.
                var tgtChild = (ObjectInstance)tgtVal.AsObjectRef();
                if (ReferenceEquals(tgtChild.Owner, containingTarget))
                {
                    containingTarget.RemoveOwned(tgtChild);
                    tgtChild.Destroy(runner);
                }
                return srcVal;
            }

            if (srcVal.Kind == ValueKind.Array)
            {
                var srcArr = (ScriptArray)srcVal.AsArray();
                var oldArr = tgtVal.Kind == ValueKind.Array ? (ScriptArray)tgtVal.AsArray() : null;

                // Target becomes as exact a 1:1 copy of the source as possible -
                // new array of the source length, then element by element the same
                // case A/B/C rules as for normal fields (array elements
                // are the "direct children" of an array, see SPEC/
                // THREADING_DESIGN.md 4.4).
                var newArr = new ScriptArray(srcArr.Length);
                for (int i = 0; i < srcArr.Length; i++)
                {
                    Value oldElem = (oldArr != null && i < oldArr.Length) ? oldArr.Items[i] : Value.MakeUndefined();
                    newArr.Items[i] = SyncSingleValue(srcArr.Items[i], oldElem, containingTarget, flat, runner);
                }
                return Value.MakeArray(newArr);
            }

            if (srcVal.Kind == ValueKind.Lambda || srcVal.Kind == ValueKind.Pointer)
                throw new TakingViolationException(
                    "'sync' rejected: lambda/pointer values are not supported at this stage " +
                    "(the same limitation as for 'taking', see the ObjectCopier class comment).");

            // Primitive values (bool/int/float/char/string/undefined) - value-like, taken over directly.
            return srcVal;
        }

        /// <summary>Pure, independent deep copy (without SyncOrigin/locking -
        /// the caller handles that, see case A above) for objects freshly arising in a
        /// sync. Deliberately a SEPARATE, simpler copy
        /// than ObjectCopier.Take (which is meant for 'taking' and additionally
        /// rejects tree-foreign references/activates thread sharing) - here the
        /// target is already part of an established, shared tree, the
        /// tree-membership check of ObjectCopier would not be
        /// sensibly applicable here (the source lives in the CHILD thread, not in the
        /// target tree itself).</summary>
        private static ObjectInstance PlainDeepCopy(ObjectInstance node, IOwner owner)
        {
            var copy = new ObjectInstance(node.ClassName, owner, node.RtClass);
            foreach (var (name, val) in node.Fields)
            {
                if (val.Kind == ValueKind.Class)
                {
                    var child = (ObjectInstance)val.AsObjectRef();
                    copy.Fields[name] = Value.MakeClassRef(PlainDeepCopy(child, copy));
                }
                else if (val.Kind == ValueKind.Array)
                {
                    var arr = (ScriptArray)val.AsArray();
                    var newArr = new ScriptArray(arr.Length);
                    for (int i = 0; i < arr.Length; i++)
                    {
                        var elem = arr.Items[i];
                        newArr.Items[i] = elem.Kind == ValueKind.Class
                            ? Value.MakeClassRef(PlainDeepCopy((ObjectInstance)elem.AsObjectRef(), copy))
                            : elem;
                    }
                    copy.Fields[name] = Value.MakeArray(newArr);
                }
                else
                {
                    copy.Fields[name] = val;
                }
            }
            return copy;
        }
    }
}
