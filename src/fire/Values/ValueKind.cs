namespace fire.Values
{
    public enum ValueKind
    {
        Bool,
        Int,
        Float,
        Char,
        String,
        Class,      // Reference to an object instance
        Lambda,     // Reference to a lambda value (Runtime.LambdaValue)
        Pointer,    // Reference to a scope slot or an object field (Values.PointerTarget)
        Array,      // Reference to an array (Values.ScriptArray)
        Buffer,     // Reference to a raw byte buffer (Values.ByteBuffer)
        Undefined,
    }
}
