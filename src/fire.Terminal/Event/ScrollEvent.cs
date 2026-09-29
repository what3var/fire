using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Terminal.Event
{
    public class ScrollEvent : Event
    {
        public float X { get; init; }

        public float Y { get; init; }
        
        
        public float ScrollX { get; init; }

        public float ScrollY { get; init; }
    }
}
