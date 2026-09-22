using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Terminal.Event
{
    public interface IEvent
    {
        
        int SourceHandle { get; }
        
        EventType Type { get; }
    }
}
