using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>The value of a Completion (docs/ROADMAP_TASKS.md section 6): set once - by any task - and read by every wait as a copy. The value lives
    /// in <see cref="Holder"/>, a scope of no thread (a copy of what Set got). Counted: every Completion object of the program holds one count.</summary>
    internal sealed class CompletionNode
    {
        public int Refs = 1;
        public int Claim;                       // the first Set/Fail takes it
        public int Done;                        // 1 set, 2 failed (Value is the exception)
        public Value Value = Value.MakeUndefined();
        public readonly Scope Holder = new(null);
    }

    /// <summary>The handles of the nodes (the int the prelude keeps): one table for all threads of the program.</summary>
    internal static class CompletionHandles
    {
        private static readonly ConcurrentDictionary<long, CompletionNode> Nodes = new();
        private static long _next;

        public static long Add(CompletionNode node) { long id = Interlocked.Increment(ref _next); Nodes[id] = node; return id; }
        public static CompletionNode? Get(long id) => id != 0 && Nodes.TryGetValue(id, out var n) ? n : null;
        public static long Ref(long id)
        {
            var n = Get(id);
            if (n == null) return 0;
            Interlocked.Increment(ref n.Refs);
            return id;
        }
        public static void Release(long id)
        {
            var n = Get(id);
            if (n != null && Interlocked.Decrement(ref n.Refs) == 0) Nodes.TryRemove(id, out _);
        }
    }

    public sealed partial class VM
    {
        /// <summary>A copy of a value for a scope of another thread (strings and numbers are values; objects, arrays and buffers are copied deep; an actor stays
        /// itself - its reference may go to any thread).</summary>
        private static Value CompletionCopy(Value v, IOwner owner)
        {
            if (v.Kind == ValueKind.Class && v.AsObjectRef() is ObjectInstance obj && (obj.Mailbox != null || (obj.RtClass?.IsActor ?? false))) return v;   // (an actor: its reference, like `fire with`)
            return v.Kind is ValueKind.Class or ValueKind.Array or ValueKind.Buffer ? ObjectCloner.Clone(v, owner, deep: true) : v;
        }

        internal Value CompletionNative(string name, Value[] a)
        {
            long H(int i) => i < a.Length && a[i].Kind == ValueKind.Int ? a[i].AsInt() : 0;
            switch (name)
            {
                case "__completion_new": return Value.MakeInt(CompletionHandles.Add(new CompletionNode()));
                case "__completion_ref": return Value.MakeInt(CompletionHandles.Ref(H(0)));
                case "__completion_release": CompletionHandles.Release(H(0)); return Value.MakeUndefined();
                case "__completion_done": return Value.MakeBool(Volatile.Read(ref (CompletionHandles.Get(H(0)) ?? new CompletionNode()).Done) != 0);
                case "__completion_set":
                {
                    var node = CompletionHandles.Get(H(0));
                    if (node == null || a.Length < 3) return Value.MakeBool(false);
                    if (a[1].Kind == ValueKind.Class) ObjectCopier.RequireNoCopyFree(a[1], "Completion.Set");
                    if (Interlocked.Exchange(ref node.Claim, 1) != 0) return Value.MakeBool(false);
                    lock (node) node.Value = CompletionCopy(a[1], node.Holder);
                    Volatile.Write(ref node.Done, a[2].Kind == ValueKind.Bool && a[2].AsBool() ? 2 : 1);
                    FireRuntime.WakeWaitingOwner();   // (every wait looks again)
                    return Value.MakeBool(true);
                }
                case "__completion_wait": return CompletionWait(CompletionHandles.Get(H(0)), a.Length > 1 ? a[1] : Value.MakeUndefined());
            }
            return Value.MakeUndefined();
        }

        /// <summary>`Completion.Wait(limit)`: waits - not deaf, like Sleep - until the value is there, the token of the task is cancelled or `limit` ran out
        /// (TimeoutException); then a copy of the value for this scope, or the copy of the exception is thrown.</summary>
        private Value CompletionWait(CompletionNode? node, Value limit)
        {
            if (node == null) return Value.MakeUndefined();
            long deadline = 0;
            if (limit.Kind != ValueKind.Undefined)
            {
                if (!TimeNatives.TryTimeTicks(limit, out long ticks, out var error))
                    return NativeFail("ArgumentException", "Wait expects a time value (5s, 200ms) or undefined: " + error);
                deadline = Stopwatch.GetTimestamp() + (long)(ticks * (Stopwatch.Frequency / 10_000_000.0));
            }
            if (Volatile.Read(ref node.Done) == 0)
            {
                var token = CurrentToken;
                int held = YieldGlobalsSection();
                bool blocked = PoolTask != null;   // (a task of the pool waits on its worker: another one may start)
                if (blocked) VmScheduler.BlockEnter();
                string? failure = null;
                try
                {
                    while (Volatile.Read(ref node.Done) == 0)
                    {
                        PollWhileWaiting();
                        if (_stopExecutionRequested) return Value.MakeUndefined();
                        if (CancelledClass() is { } cancelled) { failure = cancelled; break; }
                        long now = Stopwatch.GetTimestamp();
                        if (deadline != 0 && now >= deadline) { failure = "TimeoutException"; break; }
                        long until = deadline;
                        if (token != null && token.Deadline is var t and not 0 && (until == 0 || t < until)) until = t;
                        int ms = until == 0 ? 5 : (int)Math.Clamp((until - now) * 1000 / Stopwatch.Frequency, 1, 5);
                        FireRuntime.WaitForWake(ms);
                    }
                }
                finally { if (blocked) VmScheduler.BlockLeave(); RegainGlobalsSection(held); }
                if (failure != null) return NativeFail(failure, failure == "TimeoutException" ? "The time limit ran out." : "The task was cancelled.");
            }
            Value result;
            lock (node) result = CompletionCopy(node.Value, _currentScope.Temp);
            if (Volatile.Read(ref node.Done) == 2 && result.Kind == ValueKind.Class)
            {
                ThrowException(result);
                _nativeRedirected = true;
                return default;
            }
            return result;
        }
    }
}
