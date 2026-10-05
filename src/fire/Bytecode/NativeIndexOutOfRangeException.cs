using System;

namespace fire.Bytecode
{
    /// <summary>
    /// Eine native Funktion (siehe <see cref="NativeFunction"/>) meldet damit einen ungültigen Index
    /// bzw. eine ungültige Länge. Die VM fängt sie direkt am Aufruf ab und macht daraus eine ganz
    /// normale, per `try`/`catch` fangbare `IndexOutOfBoundsException` des Skripts - eine native
    /// Funktion hat sonst keinen Zugriff auf die Skript-Exceptions (sie kennt die VM nicht).
    /// `What` steht am Anfang der Meldung ("String-Index", "Array-Index", ...).
    /// </summary>
    public sealed class NativeIndexOutOfRangeException : Exception
    {
        public long Index { get; }
        public int Length { get; }
        public string What { get; }

        public NativeIndexOutOfRangeException(long index, int length, string what = "Index")
            : base($"{what} {index} out of range (length {length}).")
        {
            Index = index;
            Length = length;
            What = what;
        }
    }
}
