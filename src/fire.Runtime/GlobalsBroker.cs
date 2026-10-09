using System;
using System.Collections.Generic;
using System.Threading;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// The mediation between the main program (which owns the global variables) and its `fire` threads
    /// (docs/THREADING_DESIGN.md section 7):
    ///
    /// - Fire threads READ globals directly (no snapshot), protected by a shared lock (<see cref="Lock"/>, the same for
    ///   the values of the globals and for all objects that belong to the global scope).
    /// - They may CHANGE the shared area only within a SECTION: the thread registers (<see cref="EnterSection"/>) and
    ///   waits; the main program grants the sections one after the other when it calls `sync globals` (<see cref="Drain"/>) - only
    ///   one at a time, while it itself waits. The state of the globals is thus clear to the main program at all times, and there is
    ///   exactly one writer.
    /// - `fire global { ... }` instead queues a job (<see cref="PostJob"/>) which the main program executes at the next
    ///   `sync globals`; the thread does not wait.
    ///
    /// The order is FIFO. When the main program ends (<see cref="Close"/>), waiting and future sections are granted immediately
    /// - nobody hangs on an owner that no longer answers.
    /// </summary>
    public sealed class GlobalsBroker
    {
        /// <summary>The global scope of the main program.</summary>
        public Scope Scope { get; }

        /// <summary>Protects globals values and all objects of the shared area.</summary>
        public ThreadShareLock Lock { get; }

        /// <summary>The main program: only its VM executes `sync globals`.</summary>
        public VM Owner { get; }

        private abstract class Request { }

        private sealed class SectionRequest : Request
        {
            public readonly ManualResetEventSlim Granted = new(false);
            public readonly ManualResetEventSlim Done = new(false);
        }

        private sealed class JobRequest : Request
        {
            public LambdaValue Lambda = null!;
            public Value[] Args = Array.Empty<Value>();
            public Scope Holder = null!;
        }

        private readonly object _gate = new();
        private readonly Queue<Request> _queue = new();
        private bool _closed;

        public GlobalsBroker(VM owner, Scope scope)
        {
            Owner = owner;
            Scope = scope;
            Lock = new ThreadShareLock();
            scope.SharingLock = Lock;
        }

        /// <summary>Liegt etwas in der Warteschlange?</summary>
        public bool HasPending
        {
            get { lock (_gate) return _queue.Count > 0; }
        }

        public bool IsClosed
        {
            get { lock (_gate) return _closed; }
        }

        // -------------------------------------------------------------
        // Thread-Seite
        // -------------------------------------------------------------

        /// <summary>Registers the calling thread and waits until the main program grants the section. Afterwards it may change the shared
        /// area until it calls <see cref="ExitSection"/>. Returns a handle for the completion (or null if the
        /// main program has already ended - then it is granted immediately and there is nothing to complete).</summary>
        public object? EnterSection()
        {
            var request = new SectionRequest();
            lock (_gate)
            {
                if (_closed) return null;
                _queue.Enqueue(request);
            }
            VM.RaiseSignal(); // das Hauptprogramm bemerkt es an seinem naechsten sicheren Punkt (siehe VM.AutoSyncNow)
            FireRuntime.WakeWaitingOwner();
            request.Granted.Wait();
            return request;
        }

        /// <summary>Ends the section that <see cref="EnterSection"/> granted.</summary>
        public void ExitSection(object? handle)
        {
            if (handle is SectionRequest request) request.Done.Set();
        }

        /// <summary>`fire global { ... }`: queues the job and returns immediately. `holder` owns the objects copied for the job
        /// and is released as soon as it has run.</summary>
        public void PostJob(LambdaValue lambda, Value[] args, Scope holder)
        {
            lock (_gate)
            {
                if (_closed) { holder.Release(NullDestructRunner.Instance); return; }
                _queue.Enqueue(new JobRequest { Lambda = lambda, Args = args, Holder = holder });
            }
            VM.RaiseSignal(); // das Hauptprogramm bemerkt es an seinem naechsten sicheren Punkt (siehe VM.AutoSyncNow)
            FireRuntime.WakeWaitingOwner();
        }

        // -------------------------------------------------------------
        // Owner side
        // -------------------------------------------------------------

        /// <summary>`sync globals`: processes everything that is in the queue up to now (also what is added
        /// meanwhile), in order. Returns the number of processed entries. Call only on the owner's thread.</summary>
        public int Drain()
        {
            int handled = 0;
            while (true)
            {
                Request? next;
                lock (_gate)
                {
                    if (_queue.Count == 0) return handled;
                    next = _queue.Dequeue();
                }
                handled++;
                switch (next)
                {
                    case SectionRequest section:
                        section.Granted.Set();
                        section.Done.Wait(); // the thread works, the main program waits
                        break;
                    case JobRequest job:
                        Owner.RunGlobalsJob(job.Lambda, job.Args);
                        job.Holder.Release(Owner);
                        break;
                }
            }
        }

        /// <summary>The main program has ended: waiting sections are granted immediately (without waiting for their end), future ones
        /// likewise, queued jobs lapse.</summary>
        public void Close()
        {
            List<Request> pending;
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                pending = new List<Request>(_queue);
                _queue.Clear();
            }
            foreach (var request in pending)
            {
                if (request is SectionRequest section) section.Granted.Set();
                else if (request is JobRequest job) job.Holder.Release(NullDestructRunner.Instance);
            }
        }
    }
}
