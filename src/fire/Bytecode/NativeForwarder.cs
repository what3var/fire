using System;
using fire.Values;

namespace fire.Bytecode
{
    /// <summary>
    /// A method that does nothing more than call a native function with `this.field` and its own parameters:
    ///
    /// <code>Print(string text) { __GRPHRndPrint(this.id, text) }</code>
    ///
    /// This is how all methods of the bridge preludes (graphics, devices, files, ...) are built. For such methods the VM saves
    /// the whole call apparatus (scope, slots, frame, return) and calls the native function directly (see VM.OpCallMethod:
    /// fast path of the inline caches). This is detected once per method on the finished bytecode:
    ///
    /// <c>LoadThis; GetField f; LoadLocal 0,0 ... LoadLocal 0,n-1; CallNative i, n+1; (Return | Pop; LoadConst undefined; Return)</c>
    ///
    /// The native function itself is - as everywhere - jumped to via its INDEX, which is fixed at translation time.
    /// </summary>
    public sealed class NativeForwarder
    {
        public int NativeIndex { get; }

        /// <summary>The field whose value is the first argument (`id`, `handle`).</summary>
        public string FieldName { get; }

        /// <summary>Number of parameters of the method (the native function gets one more argument: the field).</summary>
        public int ParamCount { get; }

        /// <summary>The method returns the result of the native function (otherwise `undefined`).</summary>
        public bool ReturnsResult { get; }

        private NativeForwarder(int nativeIndex, string fieldName, int paramCount, bool returnsResult)
        {
            NativeIndex = nativeIndex;
            FieldName = fieldName;
            ParamCount = paramCount;
            ReturnsResult = returnsResult;
        }

        /// <summary>Checks the body of a method; null if it does not have exactly this pattern.</summary>
        public static NativeForwarder? TryCreate(Chunk chunk, int paramCount)
        {
            var code = chunk.Code;
            int i = 0;

            bool Next(OpCode expected)
            {
                if (i >= code.Count || (OpCode)code[i] != expected) return false;
                i++;
                return true;
            }
            int U16() { int v = code[i] | (code[i + 1] << 8); i += 2; return v; }
            bool Has(int bytes) => i + bytes <= code.Count;

            if (!Next(OpCode.LoadThis)) return null;
            if (!Has(3) || !Next(OpCode.GetField)) return null;
            int fieldConst = U16();
            if (fieldConst >= chunk.Constants.Count || chunk.Constants[fieldConst].Kind != ValueKind.String) return null;
            string fieldName = chunk.Constants[fieldConst].AsString();

            for (int k = 0; k < paramCount; k++)
            {
                if (!Has(5) || !Next(OpCode.LoadLocal)) return null;
                int depth = U16(), slot = U16();
                if (depth != 0 || slot != k) return null;
            }

            if (!Has(4) || !Next(OpCode.CallNative)) return null;
            int nativeIndex = U16();
            int argc = code[i++];
            if (argc != paramCount + 1) return null;

            bool returnsResult;
            if (Next(OpCode.Return)) returnsResult = true;
            else
            {
                // Pop; LoadConst <undefined>; Return
                if (!Next(OpCode.Pop) || !Has(3) || !Next(OpCode.LoadConst)) return null;
                int c = U16();
                if (c >= chunk.Constants.Count || chunk.Constants[c].Kind != ValueKind.Undefined) return null;
                if (!Next(OpCode.Return)) return null;
                returnsResult = false;
            }

            return new NativeForwarder(nativeIndex, fieldName, paramCount, returnsResult);
        }
    }
}
