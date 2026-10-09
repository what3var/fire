namespace fire.Terminal.Event
{
    /// <summary>Ein Ereignis eines Joysticks. `Joystick` ist die Nummer des Geräts (bleibt, solange es angesteckt ist). Je nach Typ ist `Index` die Achse (JoystickAxis), der Knopf (JoystickButtonDown/Up) oder
    /// das Hat (JoystickHat); `Value` ist bei einer Achse die Stellung von -1 bis 1 (0 ist die Mitte, die Achsen sind nicht entprellt: die Ruhelage kann um ein paar Prozent daneben liegen) und beim Hat eine
    /// Bitmaske der gedrückten Richtungen (<see cref="HatUp"/>, <see cref="HatRight"/>, <see cref="HatDown"/>, <see cref="HatLeft"/>; 0 ist die Mitte). Bei JoystickAdded/Removed sind Index und Value 0.</summary>
    public class JoystickEvent : Event
    {
        public const int HatUp = 1, HatRight = 2, HatDown = 4, HatLeft = 8;

        public int Joystick { get; init; }
        public int Index { get; init; }
        public float Value { get; init; }
    }
}
