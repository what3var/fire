using System;
using System.Collections.Generic;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Records (docs/ROADMAP_TASKS.md step 9): `==` compares the content - the same class and equal fields (records in fields by their content
    /// too, the rest as `==` compares it); the hash of a key follows the content (fire_rt.h: recordEquals, recordHash).</summary>
    public static class RecordValues
    {
        public static bool IsRecord(Value v) => v.Kind == ValueKind.Class && v.AsObjectRef() is ObjectInstance { RtClass.IsRecord: true, IsDestroyed: false };

        /// <summary>Two records of the same class with equal fields (only when both are records; otherwise false).</summary>
        public static bool Equal(Value a, Value b) => Equal(a, b, 0);

        private static bool Equal(Value a, Value b, int depth)
        {
            if (!IsRecord(a) || !IsRecord(b)) return false;
            var x = (ObjectInstance)a.AsObjectRef();
            var y = (ObjectInstance)b.AsObjectRef();
            if (ReferenceEquals(x, y)) return true;
            if (!ReferenceEquals(x.RtClass, y.RtClass) || depth > 1000) return false;
            int n = x.RtClass!.FlattenedFieldNames.Count;
            for (int i = 0; i < n; i++)
            {
                var fx = x.Fields.GetAt(i);
                var fy = y.Fields.GetAt(i);
                if (IsRecord(fx) && IsRecord(fy)) { if (!Equal(fx, fy, depth + 1)) return false; }
                else if (!Value.OperatorEquals(fx, fy)) return false;
            }
            return true;
        }

        /// <summary>The hash of a record by its content (`keyHash` for each field).</summary>
        public static long Hash(Value v, Func<Value, long> keyHash, int depth = 0)
        {
            var obj = (ObjectInstance)v.AsObjectRef();
            ulong h = 1469598103934665603UL ^ (ulong)(obj.RtClass!.Name.GetHashCode() & 0x7fffffff);
            int n = obj.RtClass.FlattenedFieldNames.Count;
            for (int i = 0; i < n; i++)
            {
                var f = obj.Fields.GetAt(i);
                long fh = IsRecord(f) && depth < 1000 ? Hash(f, keyHash, depth + 1) : keyHash(f);
                h = (h ^ (ulong)fh) * 1099511628211UL;
            }
            return (long)h;
        }

        /// <summary>What a record may hold (checked when it is constructed): numbers, texts, chars, bools, enums, undefined, records, lambdas without captures.
        /// Null when everything fits, else the name of the first field that does not.</summary>
        public static string? FirstChangeableField(ObjectInstance record)
        {
            var names = record.RtClass!.FlattenedFieldNames;
            for (int i = 0; i < names.Count; i++)
            {
                var f = record.Fields.GetAt(i);
                bool ok = f.Kind switch
                {
                    ValueKind.Class => f.AsObjectRef() is ObjectInstance { RtClass.IsRecord: true } || f.AsObjectRef() is ObjectInstance { IsDestroyed: true },
                    ValueKind.Array or ValueKind.Buffer => false,
                    ValueKind.Lambda => f.AsLambda() is LambdaValue { Captures: null or { Length: 0 } },
                    _ => true,
                };
                if (!ok) return names[i];
            }
            return null;
        }
    }
}
