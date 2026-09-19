namespace ScriptLang.Values
{
    /// <summary>Little- oder Big-Endian. Beeinflusst NUR die Interpretation
    /// von ToLittleEndian()/ToBigEndian() auf einem ByteBuffer (siehe dort) -
    /// die tatsächlich gespeicherten Bytes ändern sich nie implizit von
    /// selbst, nur durch einen EXPLIZITEN Aufruf einer dieser beiden
    /// Methoden.</summary>
    public enum ByteOrder { Little, Big }

    /// <summary>
    /// Ein roher Byte-Puffer fester Größe - bewusst GETRENNT von ScriptArray
    /// (das boxte Value[]-Elemente hält, ein Overhead von mehreren Bytes pro
    /// Element): ein ByteBuffer ist ein echtes, kompaktes byte[], gedacht für
    /// Binärdaten aus IO (seriell, Netzwerk, Dateien). Absichtlich auf
    /// einfachste C#-Bausteine (Array, einfache Schleifen) beschränkt, KEINE
    /// LINQ/Reflection/High-Level-.NET-Features - diese Klasse soll sich
    /// später 1:1 in eine C++-VM übertragen lassen.
    ///
    /// Trägt eine ByteOrder als veränderliche Eigenschaft: bei der
    /// Erzeugung entweder explizit gesetzt oder (Default) von der
    /// Host-Architektur übernommen (siehe VM.HostByteOrder - zur Laufzeit
    /// per Bit-Trick ermittelt, kein Compile-Flag).
    /// </summary>
    public sealed class ByteBuffer
    {
        public byte[] Bytes { get; }
        public ByteOrder Order { get; set; }
        public int Length => Bytes.Length;

        public ByteBuffer(int length, ByteOrder order)
        {
            Bytes = new byte[length];
            Order = order;
        }

        public ByteBuffer(byte[] bytes, ByteOrder order)
        {
            Bytes = bytes;
            Order = order;
        }

        /// <summary>Liefert `false` bei ungültigem Index (`value` dann 0),
        /// statt zu werfen - siehe ScriptArray.TryGet für dieselbe
        /// Begründung (C++-Portierbarkeit ohne Exceptions im Hot Path).</summary>
        public bool TryGet(long index, out byte value)
        {
            if (index < 0 || index >= Bytes.Length)
            {
                value = 0;
                return false;
            }
            value = Bytes[index];
            return true;
        }

        public bool TrySet(long index, byte value)
        {
            if (index < 0 || index >= Bytes.Length) return false;
            Bytes[index] = value;
            return true;
        }

        /// <summary>Wie TryGet/TrySet, aber OHNE die Bounds-Prüfung (siehe
        /// Bytecode.VmExecutionMode.Performance) - ein ungültiger Index führt
        /// zu einer rohen, UNGEFANGENEN .NET-IndexOutOfRangeException. Nur
        /// von VM-Opcode-Handlern im Performance-Modus aufgerufen, nie
        /// direkt aus Skript-Code heraus wählbar.</summary>
        public byte GetUnchecked(long index) => Bytes[(int)index];

        public void SetUnchecked(long index, byte value) => Bytes[(int)index] = value;

        /// <summary>Kopie mit denselben Bytes, aber eigenständigem
        /// Backing-Array (Mutationen der Kopie wirken sich nicht auf das
        /// Original aus, wie bei jeder anderen "Wert wird kopiert"-Stelle
        /// dieser Sprache).</summary>
        public ByteBuffer Clone()
        {
            var copy = new byte[Bytes.Length];
            System.Array.Copy(Bytes, copy, Bytes.Length);
            return new ByteBuffer(copy, Order);
        }

        /// <summary>Liefert eine Kopie mit vertauschter Byte-Reihenfolge -
        /// GESAMTER Puffer als EIN zusammenhängender Block gespiegelt (nicht
        /// etwa Element für Element in fester Breite), da ein ByteBuffer
        /// selbst keine Elementbreite kennt - für ein einzelnes Mehrbyte-Feld
        /// (z.B. ein 4-Byte int) IST das exakt die gewünschte Bedeutung;
        /// für einen Puffer mit mehreren gleich breiten Feldern muss die
        /// Spiegelung pro Feld selbst vorgenommen werden (z.B. über
        /// ReadU32/WriteU32 mit expliziter Order, siehe Prelude).</summary>
        public ByteBuffer Reversed(ByteOrder newOrder)
        {
            var copy = new byte[Bytes.Length];
            for (int i = 0; i < Bytes.Length; i++)
                copy[i] = Bytes[Bytes.Length - 1 - i];
            return new ByteBuffer(copy, newOrder);
        }
    }
}
