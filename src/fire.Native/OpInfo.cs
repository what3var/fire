using fire.Bytecode;

namespace fire.Native
{
    /// <summary>A decoded instruction: address, opcode, operands (in the order of the ISA table), address of the next instruction.</summary>
    public sealed record Instr(int Addr, OpCode Op, int[] A, int Next);

    /// <summary>Operand sizes in bytes per opcode (1 = u8, 2 = u16 little endian), see docs/spec/BYTECODE.md.</summary>
    public static class OpInfo
    {
        private static IReadOnlyDictionary<OpCode, int[]> Sizes => OperandSizes.Table;

        /// <summary>Decodes the whole chunk into instructions.</summary>
        public static List<Instr> Decode(IReadOnlyList<byte> code)
        {
            var result = new List<Instr>();
            int ip = 0;
            while (ip < code.Count)
            {
                var op = (OpCode)code[ip];
                if (!Sizes.TryGetValue(op, out var sizes))
                    throw new NativeNotSupportedException($"unknown opcode {op} at {ip}");
                int at = ip++;
                var operands = new int[sizes.Length];
                for (int i = 0; i < sizes.Length; i++)
                {
                    // `catch threads()` without a type has no type operand
                    if (op == OpCode.RegisterThreadsCatch && i == 2 && operands[1] == 0) break;
                    if (sizes[i] == 1) operands[i] = code[ip];
                    else operands[i] = code[ip] | (code[ip + 1] << 8);
                    ip += sizes[i];
                }
                result.Add(new Instr(at, op, operands, ip));
            }
            return result;
        }
    }

    /// <summary>The program uses something the native backend does not translate yet (the message names it).</summary>
    public sealed class NativeNotSupportedException : Exception
    {
        public NativeNotSupportedException(string message) : base(message) { }
    }
}
