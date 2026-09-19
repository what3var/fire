namespace fire.Values
{
    /// <summary>
    /// Bitbreite für int/float-Werte. Default ist immer die höchste Genauigkeit
    /// (W64 = int64/double). Kopieren von einer breiteren in eine schmalere
    /// deklarierte Breite schneidet die Daten ab (siehe Value.TruncateTo).
    ///
    /// Für float ist nur W32 (float) und W64 (double) ein echtes IEEE754-Format;
    /// W16 wird als IEEE754-binary16 (System.Half) abgebildet. Für W8 gibt es
    /// kein verbreitetes Standardformat - hier wird ein einfaches, klar
    /// dokumentiertes Minifloat (1 Vorzeichen-, 4 Exponenten-, 3 Mantissenbits,
    /// "E4M3"-artig, wie z.B. in ML-Kontexten gebräuchlich) verwendet. Das ist
    /// eine bewusste, aber arbiträre Wahl - falls ein anderes 8-Bit-Format
    /// gebraucht wird, ist das hier der einzige Ort, der sich ändern müsste.
    /// </summary>
    public enum NumericWidth { W8 = 8, W16 = 16, W32 = 32, W64 = 64 }
}
