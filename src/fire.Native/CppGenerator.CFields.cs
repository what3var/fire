using System;
using fire.Bytecode;

namespace fire.Native
{
    // Felder als C-Typen (docs/spec/NATIVE_BACKEND.md): a field declared `defined int` / `defined float` / `defined bool` / `defined word` (SPEC 8.10b) with a static
    // unit holds a number of that kind in that unit from its initializer on (its zero) - kind and unit of its slot never change. A direct access (an object of a
    // known class) reads and writes the number alone: `.i` / `.f` / `.w` of its Value, or the payload of its narrow half (nvGetI/nvPutI) - straight from and into
    // the scalar shadows, without a Value in between. The layout stays the same: reflection, JSON, copies and the debugger see the Values as before.
    public sealed partial class CppGenerator
    {
        /// <summary>The place of a field that is a C number and its static type (a narrow int, a float, a bool or a word, never undefined); null for any other.</summary>
        private (int Slot, int Half, int Type)? CFieldOf(RuntimeClass rc, string name, Func from)
        {
            if (!ScalarsOn || DirectFieldIndex(rc, name, from) is not int index) return null;
            byte declared = rc.FindFieldRequiredKind(name);
            if (declared == 0 || !DeclaredKind.IsNonNull(declared)) return null;
            int type = FieldType(name);
            byte kind = KindOfType(type);
            if (!(IsNarrowIntType(type) || kind is KindFloat or KindBool or KindWord)) return null;
            var (slot, half) = FieldPlace(rc, index);
            if (half >= 0 && kind == KindWord) return null;
            return (slot, half, type);
        }

        /// <summary>The C number of such a field of the object `obj` (an Obj*).</summary>
        private static string CFieldRead((int Slot, int Half, int Type) field, string obj)
        {
            string place = $"objFields({obj})[{field.Slot}]";
            return (KindOfType(field.Type), field.Half >= 0) switch
            {
                (KindFloat, false) => $"{place}.f",
                (KindWord, false) => $"(FireWord){place}.w",
                (KindBool, false) => $"({place}.i != 0)",
                (_, false) => $"(FireInt){place}.i",
                (KindFloat, true) => $"(Real)nvGetF(&{place}, {field.Half})",
                (KindBool, true) => $"(nvGetI(&{place}, {field.Half}) != 0)",
                _ => $"(FireInt)nvGetI(&{place}, {field.Half})",
            };
        }

        /// <summary>The store of the C number `x` into such a field of the object `obj` (an Obj*).</summary>
        private static string CFieldWrite((int Slot, int Half, int Type) field, string obj, string x)
        {
            string place = $"objFields({obj})[{field.Slot}]";
            return (KindOfType(field.Type), field.Half >= 0) switch
            {
                (KindFloat, false) => $"{place}.f = {x};",
                (KindWord, false) => $"{place}.w = {x};",
                (KindBool, false) => $"{place}.i = ({x}) ? 1 : 0;",
                (_, false) => $"{place}.i = {x};",
                (KindFloat, true) => $"nvPutF(&{place}, {field.Half}, (float)({x}));",
                (KindBool, true) => $"nvPutI(&{place}, {field.Half}, ({x}) ? 1 : 0);",
                _ => $"nvPutI(&{place}, {field.Half}, (int32_t)({x}));",
            };
        }

        /// <summary>Does slot type `valueType` certainly hold a number a C field of type `fieldType` takes as it is (its kind, its unit; an int for a float)?</summary>
        private static bool CFieldTakes(int fieldType, int valueType) => KindOfType(fieldType) switch
        {
            KindInt => IsNarrowIntType(valueType) && UnitOfType(valueType) == UnitOfType(fieldType),
            KindFloat => (KindOfType(valueType) == KindFloat || IsNarrowIntType(valueType)) && UnitOfType(valueType) == UnitOfType(fieldType),
            KindBool => KindOfType(valueType) == KindBool,
            _ => KindOfType(valueType) == KindWord,
        };

        /// <summary>The class of a slot that holds an object of a known class (not undefined), null otherwise.</summary>
        private RuntimeClass? KnownObjClass(int type) =>
            BaseKindOfType(type) == KindObj && (type & (TypeNone | NullableBit)) == 0 && InterfaceOfType(type) == null ? ObjClass(type) : null;

        /// <summary>A GetField / SetField / SetFieldClaim / SetFieldOnThis that reads or writes a C field (CFieldOf) - such an instruction keeps the scalars of
        /// the code (it takes and gives C numbers). Null when it is not one.</summary>
        private (int Slot, int Half, int Type)? CFieldAccess(Func f, Flow st, Instr ins)
        {
            if (!ScalarsOn || st.Region != null) return null;
            // (the initializer of the fields stores their zeros - with kind and unit - as Values: only from then on the number alone changes)
            if (f.Kind is FuncKind.Init or FuncKind.Ctor && ins.Op != OpCode.GetField) return null;
            int d = st.Depth;
            int Ty(int k) => k < 0 || k >= 64 ? TypeUnknown : st.Types[k];
            string Name() => f.Chunk.Constants[ins.A[0]].AsString();
            switch (ins.Op)
            {
                case OpCode.GetField when d >= 1 && d < 63 && KnownObjClass(Ty(d - 1)) is { } rc:
                    return CFieldOf(rc, Name(), f);
                case OpCode.SetField or OpCode.SetFieldClaim when !_usesProbes && d >= 2 && d < 63 && KnownObjClass(Ty(d - 2)) is { } rc
                    && CFieldOf(rc, Name(), f) is { } field && CFieldTakes(field.Type, Ty(d - 1)):
                    return field;
                case OpCode.SetFieldOnThis when !_usesProbes && d >= 1 && d < 63 && f.HasSelf && !IsExtensionMethod(f.Proto) && f.Chunk.OwnerClass is { } own
                    && CFieldOf(own, Name(), f) is { } thisField && CFieldTakes(thisField.Type, Ty(d - 1)):
                    return thisField;
            }
            return null;
        }
    }
}
