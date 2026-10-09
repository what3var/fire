namespace fire.Bytecode
{
    /// <summary>
    /// Inline cache for ONE call site in the bytecode (method/field access, see VM.OpCallMethod,
    /// OpGetField, ...): remembers what the site resolved to last time, so that the
    /// next pass with the same class can skip the name lookups (dictionary with string hash).
    /// Immutable - a thread replaces the whole entry instead of changing it,
    /// so another thread never sees a half-written state.
    ///
    /// An entry arises ONLY after all checks of the slow route have passed for exactly this site
    /// (access modifier, argument count, unit requirement of a field): they depend only on the site
    /// (its chunk/owner) and the class of the receiver, both of which are recorded in the entry. What can change at
    /// runtime (a thread lock on the object, an actor mailbox) is checked by the fast path itself.
    /// </summary>
    public sealed class SiteCache
    {
        /// <summary>The class of the receiver the entry applies to (method/field access on instances).</summary>
        public readonly RuntimeClass? Class;

        /// <summary>The method to call (method calls).</summary>
        public readonly FunctionProto? Proto;

        /// <summary>The field index in <c>FieldStore</c> (field accesses).</summary>
        public readonly int FieldIndex;

        /// <summary>If the method is a pure forwarding to a native function (see NativeForwarder), the
        /// forwarding including the index of its field (<c>ForwarderFieldIndex</c>) in this class - otherwise null.</summary>
        public readonly NativeForwarder? Forwarder;
        public readonly int ForwarderFieldIndex;

        public SiteCache(RuntimeClass? @class, FunctionProto? proto, int fieldIndex, NativeForwarder? forwarder = null, int forwarderFieldIndex = -1)
        {
            Class = @class;
            Proto = proto;
            FieldIndex = fieldIndex;
            Forwarder = forwarder;
            ForwarderFieldIndex = forwarderFieldIndex;
        }
    }
}
