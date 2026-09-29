using fire.Terminal.Event;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Terminal
{
    public class WindowPumpResult
    {
        public bool StillOpen { get; init; }

        public IEnumerable<IEvent> Events { get; init; }
    }
}
