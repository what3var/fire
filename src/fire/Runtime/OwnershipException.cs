using System;

namespace fire.Runtime
{
    public sealed class OwnershipException : Exception
    {
        public OwnershipException(string message) : base(message) { }
    }
}
