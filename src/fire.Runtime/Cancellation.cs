using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>A node of the tree of cancellation (docs/ROADMAP_TASKS.md section 6): cancelled itself, by a time limit, or by one of its parents (a task
    /// that awaits with `until` follows its caller's token and the given ones). Counted: the handles of the program and the tasks that follow it.</summary>
    public sealed class CancelNode
    {
        private int _state;                 // 0, 1 cancelled, 2 the time limit ran out
        private long _deadline;             // Stopwatch timestamp, 0: none
        private readonly List<CancelNode> _parents = new();
        internal int Refs = 1;   // (public for FireRuntime.FireVmTaking; nothing outside the runtime makes one)

        public void Link(CancelNode? parent) { if (parent != null) lock (_parents) _parents.Add(parent); }

        public void Cancel()
        {
            Interlocked.CompareExchange(ref _state, 1, 0);
            FireRuntime.WakeWaitingOwner();   // (every wait looks at its token again)
        }

        public void CancelAfter(long ticks)
        {
            long at = Stopwatch.GetTimestamp() + (long)(ticks * (Stopwatch.Frequency / 10_000_000.0));
            long old = Interlocked.Read(ref _deadline);
            if (old == 0 || at < old) Interlocked.Exchange(ref _deadline, at);
        }

        /// <summary>0: not cancelled; 1 cancelled, 2 a time limit ran out - its own or of a parent.</summary>
        public int State => StateAt(0);
        private int StateAt(int depth)
        {
            int s = Volatile.Read(ref _state);
            if (s != 0) return s;
            long d = Interlocked.Read(ref _deadline);
            if (d != 0 && Stopwatch.GetTimestamp() >= d) { Interlocked.CompareExchange(ref _state, 2, 0); return Volatile.Read(ref _state); }
            if (depth > 64) return 0;
            CancelNode[] parents;
            lock (_parents) parents = _parents.ToArray();
            foreach (var p in parents) { int ps = p.StateAt(depth + 1); if (ps != 0) return ps; }
            return 0;
        }

        /// <summary>The earliest time limit of the node and its parents (Stopwatch timestamp), 0: none.</summary>
        public long Deadline => DeadlineAt(0);
        private long DeadlineAt(int depth)
        {
            long d = Interlocked.Read(ref _deadline);
            if (depth > 64) return d;
            CancelNode[] parents;
            lock (_parents) parents = _parents.ToArray();
            foreach (var p in parents) { long pd = p.DeadlineAt(depth + 1); if (pd != 0 && (d == 0 || pd < d)) d = pd; }
            return d;
        }
    }

    /// <summary>The handles of the nodes (the int the prelude keeps): one table for all threads of the program.</summary>
    internal static class CancelHandles
    {
        private static readonly ConcurrentDictionary<long, CancelNode> Nodes = new();
        private static long _next;

        public static long Add(CancelNode node) { long id = Interlocked.Increment(ref _next); Nodes[id] = node; return id; }
        public static CancelNode? Get(long id) => id != 0 && Nodes.TryGetValue(id, out var n) ? n : null;
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
        /// <summary>The token of this thread (`fire until x`; null: none) and the frames of `await ... until x` (each with the number of handlers when it
        /// began: a catch below it leaves the frame).</summary>
        internal CancelNode? ThreadToken;
        private readonly List<(CancelNode Node, int Handlers)> _untilFrames = new();
        /// <summary>`fire ... until x`: the node the next thread follows (OpCode.CancelPush with bit 7, taken by OpCode.Fire).</summary>
        private CancelNode? _pendingFireToken;

        internal CancelNode? CurrentToken => _untilFrames.Count > 0 ? _untilFrames[^1].Node : ThreadToken;

        /// <summary>OpCode.CancelPush: the node of `until` - linked to the given ones (handles from __Cancel.Limit) and, for `await`, the caller's.</summary>
        private void UntilPush(int count, bool forFire)
        {
            var node = new CancelNode();
            var handles = new long[count];
            for (int i = count - 1; i >= 0; i--) handles[i] = Pop().AsInt();
            if (!forFire) node.Link(CurrentToken);
            foreach (var h in handles) { node.Link(CancelHandles.Get(h)); CancelHandles.Release(h); }
            if (forFire) _pendingFireToken = node;
            else _untilFrames.Add((node, _handlers.Count));
        }

        private void UntilPop() { if (_untilFrames.Count > 0) _untilFrames.RemoveAt(_untilFrames.Count - 1); }

        /// <summary>A catch with this many handlers below it: the frames of `until` that began inside its `try` are left.</summary>
        private void UntilCaught(int handlers)
        {
            while (_untilFrames.Count > 0 && _untilFrames[^1].Handlers > handlers) _untilFrames.RemoveAt(_untilFrames.Count - 1);
        }

        /// <summary>The cancellation of the running task, for a wait that ends because of it: the exception (TimeoutException for a time limit).</summary>
        internal string? CancelledClass()
        {
            int s = CurrentToken?.State ?? 0;
            return s == 2 ? "TimeoutException" : s == 1 ? "CancelledException" : null;
        }

        /// <summary>`handover x` in a lambda (fire.Standard.LambdaHandover): x and what it reaches move - every reference from before is dead. Who owns them
        /// until the lambda lets them go is open (docs/ROADMAP_TASKS.md): for now the global scope of the program (they live as long as it).</summary>
        internal Value LambdaHandoverNow(Value[] a)
        {
            if (a.Length != 1 || a[0].Kind is not (ValueKind.Class or ValueKind.Array or ValueKind.Buffer)) return a.Length > 0 ? a[0] : Value.MakeUndefined();
            ObjectHandover.RequireMovable(a[0], fromFireThread: IsFireThreadVm);
            return ObjectHandover.MoveAll(new[] { a[0] }, _globalScope)[0];
        }

        internal Value CancelNative(string name, Value[] a)
        {
            long H(int i) => i < a.Length && a[i].Kind == ValueKind.Int ? a[i].AsInt() : 0;
            switch (name)
            {
                case "__cancel_new":
                {
                    var node = new CancelNode();
                    if (a.Length > 0 && a[0].Kind == ValueKind.Bool && a[0].AsBool()) node.Link(CurrentToken);
                    return Value.MakeInt(CancelHandles.Add(node));
                }
                case "__cancel_release": CancelHandles.Release(H(0)); return Value.MakeUndefined();
                case "__cancel_state": return Value.MakeInt(CancelHandles.Get(H(0))?.State ?? 0);
                case "__cancel_current":
                {
                    var node = CurrentToken;
                    if (node == null) return Value.MakeInt(0);
                    Interlocked.Increment(ref node.Refs);
                    return Value.MakeInt(CancelHandles.Add(node));
                }
                case "__cancel_ref": return Value.MakeInt(CancelHandles.Ref(H(0)));
                case "__cancel_cancel": CancelHandles.Get(H(0))?.Cancel(); return Value.MakeUndefined();
                case "__cancel_after":
                case "__cancel_time":
                {
                    var t = a.Length > (name == "__cancel_after" ? 1 : 0) ? a[name == "__cancel_after" ? 1 : 0] : Value.MakeUndefined();
                    if (!TimeNatives.TryTimeTicks(t, out long ticks, out var error))
                        return NativeFail("ArgumentException", "until/CancelAfter expects a token, a source or a time value: " + error);
                    if (name == "__cancel_after") { CancelHandles.Get(H(0))?.CancelAfter(ticks); return Value.MakeUndefined(); }
                    var node = new CancelNode();
                    node.CancelAfter(ticks);
                    return Value.MakeInt(CancelHandles.Add(node));
                }
            }
            return Value.MakeUndefined();
        }
    }
}
