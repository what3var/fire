using fire.Bytecode;

namespace fire.Native
{
    /// <summary>A decoded instruction: address, opcode, operands (in the order of the ISA table), address of the next instruction.</summary>
    public sealed record Instr(int Addr, OpCode Op, int[] A, int Next);

    /// <summary>Operand sizes in bytes per opcode (1 = u8, 2 = u16 little endian), see docs/BYTECODE.md.</summary>
    public static class OpInfo
    {
        private static readonly Dictionary<OpCode, int[]> Sizes = new()
        {
            [OpCode.LoadConst] = new[] { 2 },
            [OpCode.Pop] = Array.Empty<int>(),
            [OpCode.Dup] = Array.Empty<int>(),
            [OpCode.Swap] = Array.Empty<int>(),
            [OpCode.RotateUnderTop] = Array.Empty<int>(),
            [OpCode.IncDecIndex] = new[] { 1, 1 },
            [OpCode.LoadLocal] = new[] { 2, 2 },
            [OpCode.StoreLocal] = new[] { 2, 2 },
            [OpCode.LoadGlobal] = new[] { 2 },
            [OpCode.StoreGlobal] = new[] { 2 },
            [OpCode.DeclareLocal] = Array.Empty<int>(),
            [OpCode.Add] = Array.Empty<int>(), [OpCode.Sub] = Array.Empty<int>(), [OpCode.Mul] = Array.Empty<int>(),
            [OpCode.Div] = Array.Empty<int>(), [OpCode.Mod] = Array.Empty<int>(),
            [OpCode.BitAnd] = Array.Empty<int>(), [OpCode.BitOr] = Array.Empty<int>(), [OpCode.BitXor] = Array.Empty<int>(),
            [OpCode.ShiftLeft] = Array.Empty<int>(), [OpCode.ShiftRight] = Array.Empty<int>(), [OpCode.Power] = Array.Empty<int>(),
            [OpCode.FormatValue] = new[] { 2 },
            [OpCode.Neg] = Array.Empty<int>(), [OpCode.LogicalNot] = Array.Empty<int>(), [OpCode.BitNot] = Array.Empty<int>(),
            [OpCode.Eq] = Array.Empty<int>(), [OpCode.NotEq] = Array.Empty<int>(), [OpCode.Lt] = Array.Empty<int>(),
            [OpCode.LtEq] = Array.Empty<int>(), [OpCode.Gt] = Array.Empty<int>(), [OpCode.GtEq] = Array.Empty<int>(),
            [OpCode.CoerceUnit] = new[] { 2 },
            [OpCode.CoerceUnitDynamic] = Array.Empty<int>(),
            [OpCode.CoerceType] = new[] { 1 },
            [OpCode.CoerceTypeDynamic] = Array.Empty<int>(),
            [OpCode.Jump] = new[] { 2 }, [OpCode.JumpIfFalse] = new[] { 2 },
            [OpCode.JumpIfFalsePeek] = new[] { 2 }, [OpCode.JumpIfTruePeek] = new[] { 2 },
            [OpCode.EnterScope] = Array.Empty<int>(), [OpCode.ExitScope] = Array.Empty<int>(),
            [OpCode.CallNative] = new[] { 2, 1 }, [OpCode.CallTryableNative] = new[] { 2, 1 }, [OpCode.CallExtern] = new[] { 2, 1 },
            [OpCode.MakeLambda] = new[] { 2, 1 },
            [OpCode.Call] = new[] { 1 },
            [OpCode.Return] = Array.Empty<int>(),
            [OpCode.NewObject] = new[] { 2, 1 }, [OpCode.NewObjectOwned] = new[] { 2, 1 },
            [OpCode.GetField] = new[] { 2 }, [OpCode.SetField] = new[] { 2 },
            [OpCode.LoadThis] = Array.Empty<int>(),
            [OpCode.SetFieldOnThis] = new[] { 2 },
            [OpCode.CallMethod] = new[] { 2, 1 },
            [OpCode.CallBaseMethod] = new[] { 2, 2, 1 },
            [OpCode.ConstructBase] = new[] { 2, 1 },
            [OpCode.CallProtoWithThis] = new[] { 2, 1 },
            [OpCode.GetStaticField] = new[] { 2, 2 }, [OpCode.SetStaticField] = new[] { 2, 2 }, [OpCode.SetStaticFieldOnInit] = new[] { 2, 2 },
            [OpCode.CallStaticMethod] = new[] { 2, 2, 1 },
            [OpCode.AddressOfLocal] = new[] { 2, 2 }, [OpCode.AddressOfGlobal] = new[] { 2 }, [OpCode.AddressOfField] = new[] { 2 },
            [OpCode.PtrRead] = Array.Empty<int>(), [OpCode.PtrWrite] = Array.Empty<int>(),
            [OpCode.NewArray] = Array.Empty<int>(), [OpCode.MakeArrayLiteral] = new[] { 2 }, [OpCode.MakeBuffer] = Array.Empty<int>(),
            [OpCode.ArrayGet] = Array.Empty<int>(), [OpCode.ArraySet] = Array.Empty<int>(),
            [OpCode.RegisterHandler] = new[] { 2 }, [OpCode.UnregisterHandler] = Array.Empty<int>(), [OpCode.Throw] = Array.Empty<int>(),
            [OpCode.IsInUnit] = new[] { 2 }, [OpCode.IsOfType] = new[] { 2 }, [OpCode.IsFrom] = new[] { 1 },
            [OpCode.ResumeException] = Array.Empty<int>(), [OpCode.ClearPendingResume] = Array.Empty<int>(),
            [OpCode.CheckLambdaSignature] = new[] { 1 }, [OpCode.CheckUnit] = new[] { 2 },
            [OpCode.Fire] = new[] { 2, 2, 1, 1 }, [OpCode.Sync] = new[] { 1 },
            [OpCode.Leave] = Array.Empty<int>(), [OpCode.Terminate] = Array.Empty<int>(),
            [OpCode.RegisterThreadsCatch] = new[] { 2, 1, 2 }, [OpCode.RegisterTerminateCatch] = new[] { 2 },
            [OpCode.Process] = Array.Empty<int>(), [OpCode.TryProcess] = Array.Empty<int>(),
            [OpCode.Halt] = Array.Empty<int>(),
            [OpCode.CopyValue] = new[] { 1 }, [OpCode.CopyValueOwned] = new[] { 1 }, [OpCode.CopyArgs] = new[] { 2, 2, 2, 2 },
            [OpCode.SyncGlobals] = Array.Empty<int>(), [OpCode.SectionEnter] = Array.Empty<int>(), [OpCode.SectionExit] = Array.Empty<int>(),
            [OpCode.SetAutoSync] = new[] { 1 }, [OpCode.PostGlobal] = new[] { 1 },
            [OpCode.EnterFinallyNormal] = Array.Empty<int>(), [OpCode.PushJump] = new[] { 2 }, [OpCode.EndFinally] = Array.Empty<int>(),
            [OpCode.Probe] = new[] { 2, 1 }, [OpCode.SilenceMember] = new[] { 2, 1 }, [OpCode.SilenceValue] = Array.Empty<int>(),
            [OpCode.MakeLambdaCapturing] = new[] { 2, 1, 1 },
            [OpCode.StoreLocalPop] = new[] { 2, 2 }, [OpCode.StoreGlobalPop] = new[] { 2 },
            [OpCode.JumpIfNotLt] = new[] { 2 }, [OpCode.JumpIfNotLtEq] = new[] { 2 }, [OpCode.JumpIfNotGt] = new[] { 2 },
            [OpCode.JumpIfNotGtEq] = new[] { 2 }, [OpCode.JumpIfNotEq] = new[] { 2 }, [OpCode.JumpIfNotNotEq] = new[] { 2 },
            [OpCode.ArithLocalConstPop] = new[] { 2, 2, 2, 1 }, [OpCode.ArithGlobalConstPop] = new[] { 2, 2, 1 },
            [OpCode.SetTimeout] = Array.Empty<int>(),
            [OpCode.AddressOfIndex] = Array.Empty<int>(), [OpCode.NewJagged] = new[] { 1 }, [OpCode.OwnValue] = Array.Empty<int>(), [OpCode.MakeArrayLiteralParts] = new[] { 2, 2, 2 }, [OpCode.Delete] = Array.Empty<int>(), [OpCode.HoistValue] = Array.Empty<int>(), [OpCode.TakeToScope] = new[] { 2 }, [OpCode.TakeToObject] = Array.Empty<int>(), [OpCode.TakeToArray] = Array.Empty<int>(), [OpCode.TakeCheck] = Array.Empty<int>(), [OpCode.RequireRefParam] = new[] { 2, 2 },
        };

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
