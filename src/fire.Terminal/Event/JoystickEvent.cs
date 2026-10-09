namespace fire.Terminal.Event
{
    /// <summary>An event of a joystick. `Joystick` is the number of the device (stays as long as it is plugged in). Depending on the type, `Index` is the axis (JoystickAxis), the button (JoystickButtonDown/Up) or
    /// the hat (JoystickHat); `Value` is for an axis the position from -1 to 1 (0 is the centre, the axes are not debounced: the rest position may be off by a few percent) and for the hat a
    /// bit mask of the pressed directions (<see cref="HatUp"/>, <see cref="HatRight"/>, <see cref="HatDown"/>, <see cref="HatLeft"/>; 0 is the centre). For JoystickAdded/Removed, Index and Value are 0.</summary>
    public class JoystickEvent : Event
    {
        public const int HatUp = 1, HatRight = 2, HatDown = 4, HatLeft = 8;

        public int Joystick { get; init; }
        public int Index { get; init; }
        public float Value { get; init; }
    }
}
