using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Terminal.Event
{
    public class MotionEvent : Event
    {
        public float X { get; init; }

        public float Xrel { get; init; }

        
        public float Y { get; init; }
        
        public float Yrel { get; init; }

        
        public int ButtonState { get; init; }
    }
}
