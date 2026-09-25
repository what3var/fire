using System;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace fire.Values
{
    /// <summary>
    /// Manueller MemoryPack-Formatter für Value (SPEC "Programm-Serialisierung").
    /// Value ist ein `readonly struct` mit privatem Konstruktor und einem
    /// untypisierten `object?`-Feld für Klasseninstanzen/Lambdas/Arrays/Puffer
    /// (siehe Value-Klassendoku) - das kann MemoryPacks Standard-Quellgenerator
    /// nicht automatisch ableiten, deshalb von Hand.
    ///
    /// Serialisiert bewusst NUR die "reinen" Werte (Bool/Int/Float/Char/String/
    /// Undefined) - jede Objektreferenz (Class/Lambda/Pointer/Array/Buffer)
    /// wirft beim Serialisieren. Das ist eine bewusste Einschränkung, kein
    /// Versehen: diese Serialisierung ist für einen KOMPILIERUNGS-Cache gedacht
    /// (Bytecode.Chunk.Constants, FunctionProto.ParamDefaults, RuntimeClass.
    /// StaticFieldValues DIREKT nach dem Kompilieren, VOR jeder Ausführung) -
    /// zu diesem Zeitpunkt kann ein Value gar keine Objektreferenz enthalten
    /// (die entstehen erst zur Laufzeit, durch 'new'/Lambda-Ausdrücke/Array-
    /// Literale, nie als reine Compiler-Konstante). Taucht eine trotzdem auf,
    /// ist das ein Zeichen, dass entweder zum falschen Zeitpunkt (nach
    /// Programmstart statt direkt nach dem Kompilieren) serialisiert wird,
    /// oder der Cache für etwas anderes als seinen vorgesehenen Zweck benutzt
    /// wird - eine klare Exception ist hier besser als ein still falsch
    /// rekonstruierter Wert.
    /// </summary>
    public sealed class ValueFormatter : MemoryPackFormatter<Value>
    {
        /// <summary>Registriert sich selbst automatisch beim Laden dieser
        /// Assembly (ModuleInitializer, läuft vor jedem Nutzercode) - kein
        /// manueller Aufruf beim Programmstart nötig, der leicht vergessen
        /// werden könnte.</summary>
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
                        $"Value vom Typ {value.Kind} kann nicht serialisiert werden - nur reine, objektfreie Werte " +
                        "(Bool/Int/Float/Char/String/Undefined) sind als Compiler-Konstante/statischer Anfangswert " +
                        "zulässig (siehe ValueFormatter-Klassendoku).");
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
                        $"Unbekannte/nicht unterstützte ValueKind '{kind}' beim Deserialisieren eines Value " +
                        "(siehe ValueFormatter-Klassendoku - vermutlich eine neuere Programmversion als dieser Cache).");
            }
        }
    }
}
