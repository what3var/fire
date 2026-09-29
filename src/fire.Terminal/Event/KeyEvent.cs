using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Terminal.Event
{
    public class KeyEvent : Event
    {
        public int KeyCode { get; init; }

        public int ScanCode { get; init; }
        
        
        public int Modifier { get; init; }

        public bool IsButtonDown { get; init; }

        public bool IsKeyRepeat { get; init; }
    }
}
