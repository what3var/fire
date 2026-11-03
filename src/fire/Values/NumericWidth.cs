namespace fire.Values
{
    /// <summary>
    /// Bit width for int/float values. Default is always the highest precision
    /// (W64 = int64/double). Copying from a wider to a narrower
    /// declared width truncates the data (see Value.TruncateTo).
    ///
    /// For float W64 is a double, W32 IEEE754 binary32, W16 IEEE754 binary16
    /// (half precision) and W8 the OCP 8-bit format FP8 E4M3 (1 sign, 4 exponent
    /// bits with bias 7, 3 mantissa bits, largest value 448, saturating) - see
    /// Value.RoundFloatToWidth, the one place that rounds to them.
    /// </summary>
    public enum NumericWidth : byte { W8 = 8, W16 = 16, W32 = 32, W64 = 64 }
}
