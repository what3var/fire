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
        // (every program: in one of modules - a library, a plugin, a program of them - by the declaration alone, RawTypeAt)
        private bool PackedOn => true;
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

        /// <summary>Is the field `index` of `rc` packed, and its static type (0: a Value slot)? In a program of one piece by the name (RawType: every class that has
        /// the field agrees); in one of modules by the declaration of the field alone - `defined`, a static kind and unit - which every module that knows the class
        /// sees alike (a class derived in another module, a plugin, lays out its base like the base's module does).</summary>
        private int RawTypeAt(RuntimeClass rc, int index)
        {
            var names = rc.FlattenedFieldNames;
            if (index < 0 || index >= names.Count) return 0;
            if (!Modular) return RawType(names[index]);
            FieldInfo? info = null;
            for (var c = rc; c != null && info == null; c = c.Base) c.OwnFieldInfo.TryGetValue(names[index], out info);
            if (info is not { IsStatic: false } || info.RequiredKind == 0 || !DeclaredKind.IsNonNull(info.RequiredKind)) return 0;
            int type = DeclaredValueType(info.RequiredKind, info.RequiredUnit);
            byte kind = KindOfType(type);
            return IsNarrowIntType(type) || kind is KindFloat or KindBool or KindWord ? type : 0;
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
                int type = RawTypeAt(rc, i);
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
            string type = RawCType(RawTypeAt(cls, index));
            // (every class the object can be has as many Value slots: the packed fields start at a constant offset - no multiplication at run time; not
            // with modules: a class of another module, a plugin, may derive from it)
            var slots = InstancesOf(rc).Where(c => index < c.Rc.FlattenedFieldNames.Count).Select(SlotCount).Distinct().ToList();
            if (slots.Count == 1 && !Modular) return $"(*({type}*)((char*)({obj}) + FIRE_HDR(Obj) + {slots[0]} * sizeof(Value) + {off[index]}))";
            return $"(*({type}*)(objRaw({obj}) + {off[index]}))";
        }

        /// <summary>The C number of a checked Value `x` for a packed field of this static type.</summary>
        private static string RawFromValue(int type, string x) => KindOfType(type) switch
        {
            KindFloat => $"(Real){x}.f", KindWord => $"(FireWord){x}.w", KindBool => $"(uint8_t)({x}.i != 0)", _ => $"(FireInt){x}.i",
        };

        /// <summary>The kind (FIRE_RAW_*) and the offset of a packed field for the tables of JSON and the debugger ("0, 0" for a Value slot).</summary>
        private string RawPlaceOf(RuntimeClass rc, int index)
        {
            var (off, _) = RawLayoutOf(rc);
            if (index >= off.Length || off[index] < 0) return "0, 0";
            int type = RawTypeAt(rc, index);
            string raw = KindOfType(type) switch { KindFloat => "FIRE_RAW_REAL", KindBool => "FIRE_RAW_BOOL", KindWord => "FIRE_RAW_WORD", _ => "FIRE_RAW_INT" };
            return $"{raw}, {off[index]}";
        }

        /// <summary>The FirePlace of the field `index` of `rc` (the debugger).</summary>
        private string PlaceOf(RuntimeClass rc, int index)
        {
            var (slot, half) = FieldPlace(rc, index);
            int unit = RawTypeAt(rc, index) is var t and not 0 ? UnitOfType(t) : 0;
            return $"{{{Math.Max(0, slot)}, {half}, {RawPlaceOf(rc, index)}, {(unit == 0 ? "0" : UnitC(unit))}}}";
        }

        /// <summary>The slot of a field for the member tables of the modules (FireField.index): -1 for a packed one (its module's helpers read it).</summary>
        private int TableSlotOf(RuntimeClass rc, string name) =>
            rc.FieldIndex.TryGetValue(name, out int index) && RawTypeAt(rc, index) == 0 ? FieldPlace(rc, index).Slot : -1;

        /// <summary>`newObject` of the class - with the bytes of its packed fields.</summary>
        private string NewObjectCall(ClassInfo cls, string owner)
        {
            int raw = PackedOn ? RawLayoutOf(cls.Rc).Bytes : 0;
            return raw > 0 ? $"newObjectR({ClassIdExpr(cls)}, {SlotCount(cls)}, {raw}, {owner})" : $"newObject({ClassIdExpr(cls)}, {SlotCount(cls)}, {owner})";
        }
    }
}
