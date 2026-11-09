using System;
using System.Collections.Generic;
using System.Linq;
using fire.Bytecode;

namespace fire.Native
{
    // Gepackte Objekte (docs/spec/NATIVE_BACKEND.md): a field that is `defined` in every class that has it (SPEC 8.10b), with a static kind and unit, is no
    // Value slot but a C number behind the slots of the object (objRaw in fire_rt.h; Obj.nraw bytes) - FireInt, Real, FireWord, or a byte for a bool, aligned
    // to its size, the fields of a base class first. Only where nothing reads the fields of an object by their index without knowing the layout - the same
    // programs as the narrow fields (NarrowFieldsOn: no threads, reflection, probes, JSON, debugger, modules); copies copy the bytes (copyValue, the deep copier).
    public sealed partial class CppGenerator
    {
        private bool PackedOn => NarrowFieldsOn && ScalarsOn;
        private readonly Dictionary<string, int> _rawType = new(StringComparer.Ordinal);
        private readonly Dictionary<RuntimeClass, (int[] Off, int Bytes)> _rawLayout = new();

        /// <summary>The static type of a packed field `name` (a narrow int, a float, a bool or a word, never undefined), 0 when it is a Value slot.</summary>
        private int RawType(string name)
        {
            if (!PackedOn || _fieldAddressTaken.Contains(name)) return 0;
            if (_rawType.TryGetValue(name, out int known)) return known;
            int result = FieldType(name);
            byte kind = KindOfType(result);
            if (!(IsNarrowIntType(result) || kind is KindFloat or KindBool or KindWord)) result = 0;
            if (result != 0)
                foreach (var rc in _program.Program.Classes.Values)
                {
                    if (!rc.FieldIndex.ContainsKey(name)) continue;
                    FieldInfo? info = null;
                    for (var c = rc; c != null && info == null; c = c.Base) c.OwnFieldInfo.TryGetValue(name, out info);
                    if (info is not { IsStatic: false } || info.RequiredKind == 0 || !DeclaredKind.IsNonNull(info.RequiredKind)) { result = 0; break; }
                }
            _rawType[name] = result;
            return result;
        }

        /// <summary>The C type a packed field of this static type is stored as.</summary>
        private static string RawCType(int type) => KindOfType(type) switch { KindFloat => "Real", KindWord => "FireWord", KindBool => "uint8_t", _ => "FireInt" };
        private int RawSize(int type) => KindOfType(type) switch
        {
            KindFloat => _program.FloatWidth / 8, KindWord => _program.WordWidth / 8, KindBool => 1, _ => _program.IntWidth / 8,
        };

        /// <summary>The byte offset of each packed field of `rc` (by its index; -1: a Value slot) and the bytes of all of them (the base class first).</summary>
        private (int[] Off, int Bytes) RawLayoutOf(RuntimeClass rc)
        {
            if (_rawLayout.TryGetValue(rc, out var known)) return known;
            var names = rc.FlattenedFieldNames;
            var off = new int[names.Count];
            int at = 0;
            for (int i = 0; i < names.Count; i++)
            {
                int type = RawType(names[i]);
                if (type == 0) { off[i] = -1; continue; }
                int size = RawSize(type);
                at = (at + size - 1) / size * size;
                off[i] = at;
                at += size;
            }
            var layout = (off, (at + 7) / 8 * 8);
            _rawLayout[rc] = layout;
            return layout;
        }

        /// <summary>The C lvalue of the packed field `index` of the object `obj` (an Obj*), null when it is a Value slot.</summary>
        private string? RawRef(RuntimeClass rc, int index, string obj)
        {
            var cls = LayoutClass(rc, index);
            var (off, _) = RawLayoutOf(cls);
            if (index >= off.Length || off[index] < 0) return null;
            string type = RawCType(RawType(cls.FlattenedFieldNames[index]));
            // (every class the object can be has as many Value slots: the packed fields start at a constant offset - no multiplication at run time)
            var slots = InstancesOf(rc).Where(c => index < c.Rc.FlattenedFieldNames.Count).Select(SlotCount).Distinct().ToList();
            if (slots.Count == 1) return $"(*({type}*)((char*)({obj}) + FIRE_HDR(Obj) + {slots[0]} * sizeof(Value) + {off[index]}))";
            return $"(*({type}*)(objRaw({obj}) + {off[index]}))";
        }

        /// <summary>The C number of a checked Value `x` for a packed field of this static type.</summary>
        private static string RawFromValue(int type, string x) => KindOfType(type) switch
        {
            KindFloat => $"(Real){x}.f", KindWord => $"(FireWord){x}.w", KindBool => $"(uint8_t)({x}.i != 0)", _ => $"(FireInt){x}.i",
        };

        /// <summary>`newObject` of the class - with the bytes of its packed fields.</summary>
        private string NewObjectCall(ClassInfo cls, string owner)
        {
            int raw = PackedOn ? RawLayoutOf(cls.Rc).Bytes : 0;
            return raw > 0 ? $"newObjectR({ClassIdExpr(cls)}, {SlotCount(cls)}, {raw}, {owner})" : $"newObject({ClassIdExpr(cls)}, {SlotCount(cls)}, {owner})";
        }
    }
}
