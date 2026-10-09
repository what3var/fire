namespace fire.Values
{
    /// <summary>
    /// Bit width for int/float values. Default is always the highest precision
    /// (W64 = int64/double). Copying from a wider to a narrower
    /// declared width truncates the data (see Value.TruncateTo).
    ///
    /// For float only W32 (float) and W64 (double) are a real IEEE754 format;
    /// W16 is mapped as IEEE754 binary16 (System.Half). For W8 there is
    /// no widespread standard format - here a simple, clearly
    /// documented minifloat (1 sign, 4 exponent, 3 mantissa bits,
    /// "E4M3"-like, as commonly used e.g. in ML contexts) is used. This is
    /// a deliberate but arbitrary choice - if another 8-bit format
    /// is needed, this is the only place that would have to change.
    /// </summary>
    public enum NumericWidth { W8 = 8, W16 = 16, W32 = 32, W64 = 64 }
}
