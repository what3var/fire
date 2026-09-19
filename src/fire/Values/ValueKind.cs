namespace ScriptLang.Values
{
    public enum ValueKind
    {
        Bool,
        Int,
        Float,
        Char,
        String,
        Class,      // Referenz auf eine Objektinstanz
        Lambda,     // Referenz auf einen Lambda-Wert (Runtime.LambdaValue)
        Pointer,    // Referenz auf einen Scope-Slot oder ein Objekt-Feld (Values.PointerTarget)
        Array,      // Referenz auf ein Array (Values.ScriptArray)
        Buffer,     // Referenz auf einen rohen Byte-Puffer (Values.ByteBuffer)
        Undefined,
    }
}
