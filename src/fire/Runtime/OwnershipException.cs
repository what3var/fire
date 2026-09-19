using System;

namespace ScriptLang.Runtime
{
    public sealed class OwnershipException : Exception
    {
        public OwnershipException(string message) : base(message) { }
    }
}
