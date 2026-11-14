using System.Collections.Concurrent;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>The calls other threads send to a fire thread (docs/ROADMAP_TASKS.md step 8 (e)): a lambda bound with `on` to an object of the thread runs
    /// there, whoever calls it. The caller puts the call here and waits; the thread runs it at its safe points (the jump back of a loop) and while it waits
    /// itself (a reply, `Sleep`) - so two threads that call each other's lambdas do not block each other. When the thread ends, what is still here (and
    /// every later call) fails: the target lives no more.</summary>
    internal sealed class HomeInbox
    {
        private readonly ConcurrentQueue<(LambdaValue Lambda, Value[] Args, ActorReply Reply)> _queue = new();
        private readonly object _gate = new();
        private bool _closed;

        public bool HasPending => !_queue.IsEmpty;

        public const string EndedText = "The lambda is bound to an object of a fire thread that has ended: its target lives no more.";

        /// <summary>Puts a call in; false when the thread has ended (the caller fails it itself).</summary>
        public bool Post(LambdaValue lambda, Value[] args, ActorReply reply)
        {
            lock (_gate)
            {
                if (_closed) return false;
                _queue.Enqueue((lambda, args, reply));
            }
            VM.RaiseSignal();       // (the thread looks at its safe points)
            VmScheduler.Wake();     // (a task of the pool that waits)
            return true;
        }

        /// <summary>Runs what is there (on the thread itself).</summary>
        public void Drain(VM vm)
        {
            while (_queue.TryDequeue(out var call)) vm.RunGlobalsJob(call.Lambda, call.Args, call.Reply);
        }

        /// <summary>The thread ends: every call that is still here fails, later ones are refused.</summary>
        public void Close()
        {
            lock (_gate) _closed = true;
            while (_queue.TryDequeue(out var call))
            {
                call.Reply.Failure = EndedText;
                call.Reply.Done.Set();
            }
            VmScheduler.Wake();
        }
    }
}
