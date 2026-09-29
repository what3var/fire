using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Terminal.Event
{
    public class Event : IEvent
    {
        public int SourceHandle { get; init; }

        public EventType Type { get; init; }
    }
}
