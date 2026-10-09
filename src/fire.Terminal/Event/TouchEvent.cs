namespace fire.Terminal.Event
{
    /// <summary>A finger on the touchscreen (TouchDown/TouchMove/TouchUp). The position is in pixels of the framebuffer (as with the mouse); `Finger` distinguishes several simultaneous fingers,
    /// `Pressure` is the pressure from 0 to 1 (devices without pressure sensing report 1).</summary>
    public class TouchEvent : Event
    {
        public long Finger { get; init; }
        public float X { get; init; }
        public float Y { get; init; }
        public float Pressure { get; init; }
    }
}
