using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>The queue of a Channel (docs/ROADMAP_TASKS.md step 9): what travels belongs to <see cref="Holder"/> (a scope of no thread) until it is
    /// received. Counted: every end (Channel object) of the program holds one count; the last one lets go of what is still in it.</summary>
    internal sealed class ChannelNode
    {
        public int Refs = 1;
        public readonly object Gate = new();
        public readonly Queue<Value> Items = new();
        public int Capacity;
        public volatile bool Closed;
        public readonly Scope Holder = new(null);
    }

    /// <summary>The handles of the nodes (the int the prelude keeps): one table for all threads of the program.</summary>
    internal static class ChannelHandles
    {
        private static readonly ConcurrentDictionary<long, ChannelNode> Nodes = new();
        private static long _next;

        public static long Add(ChannelNode node) { long id = Interlocked.Increment(ref _next); Nodes[id] = node; return id; }
        public static ChannelNode? Get(long id) => id != 0 && Nodes.TryGetValue(id, out var n) ? n : null;
        public static long Ref(long id)
        {
            var n = Get(id);
            if (n == null) return 0;
            Interlocked.Increment(ref n.Refs);
            return id;
        }
        public static bool Release(long id, out ChannelNode? last)
        {
            last = null;
            var n = Get(id);
            if (n == null || Interlocked.Decrement(ref n.Refs) != 0) return false;
            Nodes.TryRemove(id, out _);
            last = n;
            return true;
        }
    }

    public sealed partial class VM
    {
        internal const string LentTakeText = "A lent record belongs to the thread that sent it: 'take' is for its owner - 'copy r' makes one of your own.";
        internal const string ChannelWordText = "Channel.Send: an object goes into a channel only with a word - 'copy x' (a copy) or 'take x' (moved) - or fresh ('new', the result of a call).";

        internal Value ChannelNative(string name, Value[] a)
        {
            long H(int i) => i < a.Length && a[i].Kind == ValueKind.Int ? a[i].AsInt() : 0;
            switch (name)
            {
                case "__chan_new":
                {
                    long cap = a.Length > 0 && a[0].Kind == ValueKind.Int ? a[0].AsInt() : 16;
                    if (cap < 1) return NativeFail("ArgumentException", "The capacity of a channel must be at least 1.");
                    return Value.MakeInt(ChannelHandles.Add(new ChannelNode { Capacity = (int)Math.Min(cap, int.MaxValue) }));
                }
                case "__chan_ref": return Value.MakeInt(ChannelHandles.Ref(H(0)));
                case "__chan_release":
                    if (ChannelHandles.Release(H(0), out var last) && last != null)
                    {
                        // (the last end: what was not received is destroyed - had a receiver taken it, it would be gone with it too)
                        lock (last.Gate) { last.Items.Clear(); last.Closed = true; }
                        last.Holder.Release(this);
                    }
                    return Value.MakeUndefined();
                case "__chan_close":
                    if (ChannelHandles.Get(H(0)) is { } closing) { closing.Closed = true; FireRuntime.WakeWaitingOwner(); VmScheduler.Wake(); }
                    return Value.MakeUndefined();
                case "__chan_closed": return Value.MakeBool(ChannelHandles.Get(H(0))?.Closed ?? true);
                case "__chan_count":
                {
                    var n = ChannelHandles.Get(H(0));
                    if (n == null) return Value.MakeInt(0);
                    lock (n.Gate) return Value.MakeInt(n.Items.Count);
                }
                case "__chan_send": return ChannelSend(ChannelHandles.Get(H(0)), a.Length > 1 ? a[1] : Value.MakeUndefined(), a.Length > 2 && a[2].Kind == ValueKind.Bool && a[2].AsBool());
                case "__chan_watch": return ChannelWatch(a.Length > 0 ? a[0] : Value.MakeUndefined(), H(1), a.Length > 2 ? a[2] : Value.MakeUndefined());
                case "__chan_receive":
                    return ChannelReceive(ChannelHandles.Get(H(0)), a.Length > 1 ? a[1] : Value.MakeUndefined(), a.Length <= 2 || a[2].Kind != ValueKind.Bool || a[2].AsBool());
            }
            return Value.MakeUndefined();
        }

        /// <summary>`ch.Send(v)`: waits while the channel is full; false when it is closed (nothing moved). An object, array, buffer or lambda goes only when the
        /// call owns it (`owned`: fresh, copied, taken) - it moves to the channel like `handover` (a record keeps its identity: nobody can change it).</summary>
        private Value ChannelSend(ChannelNode? node, Value v, bool owned)
        {
            if (node == null) return Value.MakeBool(false);
            bool moves = false;
            if (v.Kind is ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda
                && !(v.Kind == ValueKind.Class ? ((ObjectInstance)v.AsObjectRef()).IsDestroyed : IsDestroyedLeaf(v))
                && !(v.Kind == ValueKind.Class && v.AsObjectRef() is ObjectInstance { } actor && (actor.Mailbox != null || (actor.RtClass?.IsActor ?? false))))
            {
                if (!owned && v.Kind == ValueKind.Class && RecordValues.IsRecord(v)) Lend((ObjectInstance)v.AsObjectRef());   // (a record without a word: lent)
                else
                {
                    if (!owned) return NativeFail("ArgumentException", ChannelWordText);
                    ObjectHandover.RequireMovable(v, fromFireThread: IsFireThreadVm);
                    moves = true;
                }
            }
            if (!WaitChannel(node, () => node.Closed || node.Items.Count < node.Capacity, out var failure)) return failure;
            lock (node.Gate)
            {
                if (node.Closed) return Value.MakeBool(false);
                // (a record keeps its identity - unless it was lent: then the readers' references end, like the native code)
                if (moves) v = ObjectHandover.MoveAll(new[] { v }, node.Holder, retire: !RecordValues.IsRecord(v) || v.AsObjectRef() is ObjectInstance { LentBy: not null })[0];
                node.Items.Enqueue(v);
            }
            FireRuntime.WakeWaitingOwner();
            VmScheduler.Wake();
            return Value.MakeBool(true);
        }

        /// <summary>`__chan_watch(v, node, text)` (Net.Channel): 0 - v is a value (no object); 1 - a living record, lent from now on: when the lending ends (it
        /// dies, its owner moves it) `text` goes into the channel `node` (without waiting, whatever its capacity); 2 - another object, an array, a buffer, a
        /// lambda or something destroyed.</summary>
        private Value ChannelWatch(Value v, long handle, Value text)
        {
            if (v.Kind is not (ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda)) return Value.MakeInt(0);
            if (v.Kind != ValueKind.Class || v.AsObjectRef() is not ObjectInstance { IsDestroyed: false } record || !RecordValues.IsRecord(v)) return Value.MakeInt(2);
            Lend(record);
            long id = ChannelHandles.Ref(handle);
            var previous = record.LentEnd;
            record.LentEnd = () =>
            {
                previous?.Invoke();
                if (ChannelHandles.Get(id) is { } node)
                {
                    lock (node.Gate) { if (!node.Closed) node.Items.Enqueue(text); }
                    FireRuntime.WakeWaitingOwner();
                    VmScheduler.Wake();
                }
                if (ChannelHandles.Release(id, out var last) && last != null) { lock (last.Gate) { last.Items.Clear(); last.Closed = true; } }
            };
            return Value.MakeInt(1);
        }

        /// <summary>A record lent through a channel, with the records it holds: this thread owns it (unless a thread lent it before).</summary>
        private void Lend(ObjectInstance record)
        {
            if (record.LentBy != null) return;
            record.LentBy = this;
            foreach (var (_, field) in record.Fields)
                if (field.Kind == ValueKind.Class && field.AsObjectRef() is ObjectInstance { IsDestroyed: false } inner && RecordValues.IsRecord(field)) Lend(inner);
        }

        /// <summary>`ch.Receive(limit)` / `ch.TryReceive()` (wait false): the next value - it belongs to the receiving call (the scope of this call) - or
        /// undefined when the channel is closed and empty (TryReceive: also when nothing is there now).</summary>
        private Value ChannelReceive(ChannelNode? node, Value limit, bool wait)
        {
            if (node == null) return Value.MakeUndefined();
            if (wait && !WaitChannel(node, () => node.Closed || node.Items.Count > 0, out var failure, limit)) return failure;
            Value v;
            lock (node.Gate)
            {
                if (node.Items.Count == 0) return Value.MakeUndefined();
                v = node.Items.Dequeue();
                if (v.Kind == ValueKind.Class && v.AsObjectRef() is ObjectInstance { IsDestroyed: false } obj && ReferenceEquals(obj.Owner, node.Holder)) obj.ReparentTo(_currentScope.Temp);
                else if (LeafOf(v) is { IsDestroyed: false, LeafOwner: var leafOwner } leaf && ReferenceEquals(leafOwner, node.Holder)) LeafOwnership.Reparent(leaf, _currentScope.Temp);
            }
            FireRuntime.WakeWaitingOwner();
            VmScheduler.Wake();
            return v;
        }

        /// <summary>Waits - not deaf, like Sleep - until `ready` holds (read under the node's lock), the token of the task is cancelled or `limit` ran out
        /// (TimeoutException). False with the failure to return.</summary>
        private bool WaitChannel(ChannelNode node, Func<bool> ready, out Value failure, Value? limit = null)
        {
            failure = default;
            bool Ready() { lock (node.Gate) return ready(); }
            if (Ready()) return true;
            long deadline = 0;
            if (limit is { Kind: not ValueKind.Undefined } time)
            {
                if (!TimeNatives.TryTimeTicks(time, out long ticks, out var error)) { failure = NativeFail("ArgumentException", "Receive expects a time value (5s, 200ms) or undefined: " + error); return false; }
                deadline = Stopwatch.GetTimestamp() + (long)(ticks * (Stopwatch.Frequency / 10_000_000.0));
            }
            var token = CurrentToken;
            int held = YieldGlobalsSection();
            bool blocked = PoolTask != null;
            if (blocked) VmScheduler.BlockEnter();
            string? cause = null;
            try
            {
                while (!Ready())
                {
                    PollWhileWaiting();
                    if (_stopExecutionRequested) { failure = Value.MakeUndefined(); return false; }
                    if (CancelledClass() is { } cancelled) { cause = cancelled; break; }
                    long now = Stopwatch.GetTimestamp();
                    if (deadline != 0 && now >= deadline) { cause = "TimeoutException"; break; }
                    long until = deadline;
                    if (token != null && token.Deadline is var t and not 0 && (until == 0 || t < until)) until = t;
                    int ms = until == 0 ? 5 : (int)Math.Clamp((until - now) * 1000 / Stopwatch.Frequency, 1, 5);
                    FireRuntime.WaitForWake(ms);
                }
            }
            finally { if (blocked) VmScheduler.BlockLeave(); RegainGlobalsSection(held); }
            if (cause != null) { failure = NativeFail(cause, cause == "TimeoutException" ? "The time limit ran out." : "The task was cancelled."); return false; }
            return true;
        }
    }
}
