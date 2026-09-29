using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Terminal.Event
{
    public class TextEvent : Event
    {
        public string? Text { get; init; }
    }
}
