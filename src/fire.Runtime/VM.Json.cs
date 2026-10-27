using System;
using fire.Bytecode;
using fire.Standard;

namespace fire.Runtime
{
    /// <summary>The virtual machine as host of the JSON functions (fire.Standard.JsonMethods): the scope of the calling code owns what is read,
    /// the classes of the program, the type arguments of the generic method that runs (`Json.Deserialize&lt;Person&gt;`).</summary>
    public sealed partial class VM : IJsonHost
    {
        IOwner IJsonHost.CurrentOwner => _currentScope;

        RuntimeClass? IJsonHost.FindClass(string name) => _classes.TryGetValue(name, out var rc) ? rc : null;

        string[]? IJsonHost.CallTypeArgs
        {
            get
            {
                for (var s = _currentScope; s != null; s = s.Parent)
                    if (s.TypeArgs != null) return s.TypeArgs;
                return null;
            }
        }

        Exception IJsonHost.Error(string className, string message) => new ScriptErrorException(className, message);
    }
}
