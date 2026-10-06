using System;
using System.Collections.Generic;
using System.Text;
using fire.Runtime;
using fire.Values;

namespace fire.Standard
{
    /// <summary>What <see cref="ResourceMethods.NativeName"/> is asked for (fixed values, the prelude and the native backend know them).</summary>
    public enum ResourceOp
    {
        Count = 0,
        Length = 1,
        Bytes = 2,
        Name = 3,
        Text = 4,
    }

    /// <summary>
    /// The class <c>Resource</c> of the prelude and the native function behind it. `new Resource("images/logo.png")` is resolved when the program is compiled: the
    /// preprocessor reads the file (the path is relative to the source file that mentions it), stores it in the program (<see cref="CompiledProgram.Resources"/>) and puts the
    /// number of the resource where the path was - the program that runs (the VM, a packed file) or is built natively carries the bytes itself, nothing is read at run time.
    /// </summary>
    public static class ResourceMethods
    {
        public const string NativeName = "__ResourceCall";

        public static string PreludeSource { get; } = """

            // An embedded file (docs/RESOURCES.md): `new Resource("images/logo.png")` - the compiler reads the file, embeds it and replaces the path by the number of the resource.
            class Resource {
                int id

                construct(id) { this.id = id }

                // the path as it was written in the source
                string Name() { return __ResourceCall(3, this.id) }
                int Length() { return __ResourceCall(1, this.id) }
                // the content as a byte buffer
                Bytes() { return __ResourceCall(2, this.id) }
                // the content as text (UTF-8)
                string Text() { return __ResourceCall(4, this.id) }
            }

            """;

        /// <summary>The native function <c>__ResourceCall(op, id)</c> over the resources of the program.</summary>
        public static Value Call(Value[] args, IReadOnlyList<ResourceEntry>? resources)
        {
            if (args.Length != 2 || args[0].Kind != ValueKind.Int)
                throw new InvalidOperationException($"{NativeName}(op, id) expects two arguments.");
            var op = (ResourceOp)args[0].AsInt();
            if (op == ResourceOp.Count) return Value.MakeInt(resources?.Count ?? 0);
            if (args[1].Kind != ValueKind.Int)
                throw new InvalidOperationException("A Resource needs a path that is a string literal: new Resource(\"images/logo.png\") - the compiler embeds the file.");
            long id = args[1].AsInt();
            if (resources == null || id < 0 || id >= resources.Count)
                throw new InvalidOperationException($"No resource with the number {id}.");
            var entry = resources[(int)id];
            return op switch
            {
                ResourceOp.Length => Value.MakeInt(entry.Data.Length),
                ResourceOp.Bytes => Value.MakeBuffer(new ByteBuffer((byte[])entry.Data.Clone(), ByteConversions.HostByteOrder)),
                ResourceOp.Name => Value.MakeString(entry.Name),
                ResourceOp.Text => Value.MakeString(Encoding.UTF8.GetString(entry.Data)),
                _ => throw new InvalidOperationException($"{NativeName}: unknown operation {(int)op}.")
            };
        }
    }
}
