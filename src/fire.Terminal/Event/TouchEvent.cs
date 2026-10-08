namespace fire.Terminal.Event
{
    /// <summary>Ein Finger auf dem Touchscreen (TouchDown/TouchMove/TouchUp). Die Position ist in Pixeln des Framebuffers (wie bei der Maus); `Finger` unterscheidet mehrere gleichzeitige Finger,
    /// `Pressure` ist der Druck von 0 bis 1 (Geräte ohne Druckmessung melden 1).</summary>
    public class TouchEvent : Event
    {
        public long Finger { get; init; }
        public float X { get; init; }
        public float Y { get; init; }
        public float Pressure { get; init; }
    }
}
