using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// Runtime value of a lambda function. Needs - unlike classic
    /// closures - NO captured enclosing scope: per SPEC
    /// 4.2 lambdas see only their own scope (created anew on every call) plus
    /// the global scope anyway. A LambdaValue therefore only has to remember the compiled
    /// function body (proto) and the currently bound 'this' (OnTarget).
    ///
    /// Ownership (SPEC 4.2 / 2.1): lambda values are treated like
    /// object instances - the owner is the object on direct field assignment, otherwise the
    /// current scope. This policy (which owner for which assignment) is put in place
    /// by the evaluator/compiler at the respective assignment site; the
    /// owner field here is therefore optional and is not yet filled at this stage
    /// (function/call frames) - lambda values can already be
    /// created, bound and called, their ownership integration follows
    /// with the class/object stage.
    /// </summary>
    public sealed class LambdaValue
    {
        public FunctionProto Proto { get; }

        /// <summary>The object bound via 'on' (this context), or null
        /// if no 'on' was used.</summary>
        public object? OnTarget { get; }

        public IOwner? Owner { get; private set; }

        /// <summary>Values of the outer local variables used by the body, COPIED on creation (lambda captures, SPEC 4.2),
        /// or null. On a call they land as slots directly after the parameters.</summary>
        public Value[]? Captures { get; }

        public LambdaValue(FunctionProto proto, object? onTarget, IOwner? owner = null, Value[]? captures = null)
        {
            Proto = proto;
            OnTarget = onTarget;
            Owner = owner;
            Captures = captures;
        }

        /// <summary>`a on obj2` - creates a NEW lambda value with a different
        /// this context; `a` itself stays unchanged (SPEC 4.2). The same proto
        /// is reused (execution, not definition, changes).</summary>
        public LambdaValue WithOnTarget(object? newTarget, IOwner? owner = null) =>
            new(Proto, newTarget, owner, Captures);

        internal void SetOwnerInternal(IOwner newOwner) => Owner = newOwner;

        public bool IsOwnedBy(object ownerCandidate) => Owner != null && ReferenceEquals(Owner, ownerCandidate);

        public bool IsTransitivelyOwnedBy(object ownerCandidate)
        {
            IOwner? current = Owner;
            while (current != null)
            {
                if (ReferenceEquals(current, ownerCandidate)) return true;
                current = current is ObjectInstance oi ? oi.Owner : null;
            }
            return false;
        }
    }
}
