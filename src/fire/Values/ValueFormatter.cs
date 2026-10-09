using System;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace fire.Values
{
    /// <summary>
    /// Manual MemoryPack formatter for Value (SPEC "Program serialisation").
    /// Value is a `readonly struct` with a private constructor and an
    /// untyped `object?` field for class instances/lambdas/arrays/buffers
    /// (see Value class docs) - MemoryPack's default source generator
    /// cannot derive that automatically, hence by hand.
    ///
    /// Deliberately serialises ONLY the "pure" values (bool/int/float/char/string/
    /// undefined) - every object reference (class/lambda/pointer/array/buffer)
    /// throws on serialisation. That is a deliberate restriction, not an
    /// oversight: this serialisation is intended for a COMPILATION cache
    /// (Bytecode.Chunk.Constants, FunctionProto.ParamDefaults, RuntimeClass.
    /// StaticFieldValues DIRECTLY after compiling, BEFORE any execution) -
    /// at that point a Value cannot contain an object reference at all
    /// (those arise only at runtime, through 'new'/lambda expressions/array
    /// literals, never as a pure compiler constant). If one shows up anyway,
    /// that is a sign that either serialisation happens at the wrong time (after
    /// program start instead of right after compiling),
    /// or the cache is used for something other than its intended purpose
    /// - a clear exception is better here than a silently wrongly
    /// reconstructed value.
    /// </summary>
    public sealed class ValueFormatter : MemoryPackFormatter<Value>
    {
        /// <summary>Registers itself automatically when this
        /// assembly is loaded (ModuleInitializer, runs before any user code) - no
        /// manual call at program start needed, which could easily
        /// be forgotten.</summary>
        [ModuleInitializer]
        internal static void RegisterSelf()
        {
            MemoryPackFormatterProvider.Register(new ValueFormatter());
        }

        public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref Value value)
        {
            writer.WriteValue((byte)value.Kind);
            switch (value.Kind)
            {
                case ValueKind.Bool:
                    writer.WriteValue(value.AsBool());
                    break;
                case ValueKind.Int:
                    writer.WriteValue(value.AsInt());
                    writer.WriteValue((byte)value.Width);
                    writer.WriteValue(value.Unit);
                    break;
                case ValueKind.Float:
                    writer.WriteValue(value.AsFloat());
                    writer.WriteValue((byte)value.Width);
                    writer.WriteValue(value.Unit);
                    break;
                case ValueKind.Char:
                    writer.WriteValue(value.AsChar());
                    break;
                case ValueKind.String:
                    writer.WriteValue(value.AsString());
                    break;
                case ValueKind.Undefined:
                    writer.WriteValue(value.Unit);
                    break;
                default:
                    throw new NotSupportedException(
                        $"A value of type {value.Kind} cannot be serialized - only pure, object-free values " +
                        "(Bool/Int/Float/Char/String/Undefined) are allowed as a compiler constant/static initial value " +
                        "(see the ValueFormatter class documentation).");
            }
        }

        public override void Deserialize(ref MemoryPackReader reader, scoped ref Value value)
        {
            var kind = (ValueKind)reader.ReadValue<byte>();
            switch (kind)
            {
                case ValueKind.Bool:
                    value = Value.MakeBool(reader.ReadValue<bool>());
                    break;
                case ValueKind.Int:
                {
                    long i = reader.ReadValue<long>();
                    var width = (NumericWidth)reader.ReadValue<byte>();
                    var unit = reader.ReadValue<Unit?>();
                    value = Value.MakeInt(i, unit, width);
                    break;
                }
                case ValueKind.Float:
                {
                    double f = reader.ReadValue<double>();
                    var width = (NumericWidth)reader.ReadValue<byte>();
                    var unit = reader.ReadValue<Unit?>();
                    value = Value.MakeFloat(f, unit, width);
                    break;
                }
                case ValueKind.Char:
                    value = Value.MakeChar(reader.ReadValue<char>());
                    break;
                case ValueKind.String:
                    value = Value.MakeString(reader.ReadValue<string>() ?? "");
                    break;
                case ValueKind.Undefined:
                {
                    var unit = reader.ReadValue<Unit?>();
                    value = Value.MakeUndefined(unit);
                    break;
                }
                default:
                    throw new NotSupportedException(
                        $"Unknown/unsupported ValueKind '{kind}' while deserializing a value " +
                        "(see the ValueFormatter class documentation - probably a newer program version than this cache).");
            }
        }
    }
}
