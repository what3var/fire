using System;

namespace fire.Runtime
{
    /// <summary>A violation of the ownership rules found at run time (SPEC 2.2: `TakeUpwards` without a scope above, a `TakeTo` or `take` that would make
    /// a value an owner of its own owners, `Take...` of a destroyed value, a mode that is no `Takes`). The VM turns it into the catchable exception of the
    /// standard library named by <see cref="FireClass"/> (VM.RaiseScriptError) - natively the same class and text (fire_rt.h ownError).</summary>
    public sealed class OwnershipException : Exception
    {
        /// <summary>The class of the script exception: `DestroyedException` for a destroyed value, else `ArgumentException`.</summary>
        public string FireClass { get; }
        public OwnershipException(string message) : base(message) { FireClass = "ArgumentException"; }
        public OwnershipException(string fireClass, string message) : base(message) { FireClass = fireClass; }

        /// <summary>`Take...` of a destroyed object, array, buffer or lambda.</summary>
        public static OwnershipException Destroyed(string what) => new("DestroyedException", $"A destroyed {what} cannot change its owner.");
    }
}
