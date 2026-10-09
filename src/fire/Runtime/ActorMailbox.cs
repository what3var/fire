using System.Collections.Concurrent;
using System.Threading;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>A single message in an actor mailbox: method name +
    /// arguments, just like a normal method call - only not
    /// executed immediately, but only on `process`/`try process` (see
    /// Ast.ProcessStmt/TryProcessExpr, VM.ProcessOneMessage).</summary>
    public readonly struct ActorMessage
    {
        public string MethodName { get; }
        public Value[] Args { get; }

        public ActorMessage(string methodName, Value[] args)
        {
            MethodName = methodName;
            Args = args;
        }
    }

    /// <summary>The mailbox of ONE actor instance (docs/THREADING_DESIGN.md
    /// section 2) - each actor instance (see Runtime.ObjectInstance.
    /// Mailbox) gets exactly one of its own on `new`. `Enqueue` is called from
    /// ANY thread (whenever anywhere in the program
    /// a method is called on an actor reference, see
    /// VM.CallMethod), `TryProcessOne` ONLY from the actor's "home" thread
    /// (through `process`/`try process` - this restriction is deliberately
    /// NOT technically enforced here, see Resolver.ResolveProcessTarget for
    /// the actual check).
    ///
    /// The `SemaphoreSlim` keeps the "how many messages are waiting" counter
    /// ALWAYS exactly in sync with the queue: each Enqueue raises it by
    /// exactly 1, each SUCCESSFUL TryProcessOne (blocking or not)
    /// lowers it by exactly 1 - so `try process` can simply try `Wait(0)`
    /// (non-blocking asking "is anything there right now?"), without
    /// counter and actual queue content ever being able to drift
    /// apart.</summary>
    public sealed class ActorMailbox
    {
        private readonly ConcurrentQueue<ActorMessage> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);

        public void Enqueue(ActorMessage message)
        {
            _queue.Enqueue(message);
            _signal.Release();
        }

        /// <summary>`blocking=true` (see Ast.ProcessStmt): waits until
        /// at least one message is there, and then ALWAYS returns `true`.
        /// `blocking=false` (see Ast.TryProcessExpr): returns `false`
        /// immediately if nothing is waiting right now.</summary>
        public bool TryProcessOne(bool blocking, out ActorMessage message)
        {
            if (blocking)
                _signal.Wait();
            else if (!_signal.Wait(0))
            {
                message = default;
                return false;
            }

            // The counter was already lowered above (Wait has always run through
            // successfully here) - the queue MUST have at least one element at this point
            // (see the class comment on the
            // synchronicity of counter and queue content).
            bool ok = _queue.TryDequeue(out message);
            System.Diagnostics.Debug.Assert(ok, "ActorMailbox: signal without a matching message - counter and queue have diverged.");
            return ok;
        }
    }
}
