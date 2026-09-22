using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Terminal.Event
{
    public class ClickEvent : Event
    {
        public float X { get; init; }

        public float Y { get; init; }
        
        
        public int Button { get; init; }

        public bool IsButtonDown { get; init; }
    }
}
