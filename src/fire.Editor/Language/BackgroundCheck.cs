using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace fire.Editor
{
    /// <summary>
    /// A piece of work that is asked for again and again while the user types (the analysis of a document, its highlighting): the newest request wins.
    /// <list type="bullet">
    /// <item>Debounce: a request waits `delay` before its work starts; a newer request during the wait (or while it waits for the gate) replaces it - the older one never runs.</item>
    /// <item>The work runs on the thread pool, never on the thread that asked; with a shared <see cref="SemaphoreSlim"/> gate the checks of several documents run one after the
    /// other (at most one analysis at a time, whatever the number of open documents).</item>
    /// <item>Work that is running when a newer request comes gets its token cancelled (it may stop early) - if it finishes anyway, its result is still delivered when
    /// <see cref="DeliverSuperseded"/> is on (errors of the text of a moment ago are better than none while the newer check is still waiting), unless a newer result was delivered
    /// already: results arrive in the order of their requests, a stale result never overwrites a newer one.</item>
    /// <item>The newest request is always delivered (or reported to <see cref="Failed"/>), unless <see cref="Cancel"/> came after it - so after the user stops typing the
    /// errors of the final text always show.</item>
    /// </list>
    /// Results are handed to `post` (the UI thread's dispatcher in the editor, the calling thread in the tests) and checked once more there: a request made between the end of the work
    /// and the delivery on the UI thread still wins.
    /// </summary>
    public sealed class BackgroundCheck<T>
    {
        private readonly object _lock = new();
        private readonly Action<Action> _post;
        private readonly SemaphoreSlim? _gate;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private CancellationTokenSource? _newest;
        private long _requested, _delivered, _cancelledUpTo, _running;

        /// <param name="post">Runs the delivery (Completed/Failed) where the caller wants it - the UI thread.</param>
        /// <param name="gate">Shared by the checks that must not run at the same time (null: no limit).</param>
        /// <param name="delay">The wait before the work (tests: a fake clock); default Task.Delay.</param>
        public BackgroundCheck(Action<Action> post, SemaphoreSlim? gate = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _post = post;
            _gate = gate;
            _delay = delay ?? ((span, token) => Task.Delay(span, token));
        }

        /// <summary>The result of a request: (result, generation of the request). Called through `post`.</summary>
        public event Action<T, long>? Completed;

        /// <summary>The work of a request threw (not a cancellation): (exception, generation). Called through `post`.</summary>
        public event Action<Exception, long>? Failed;

        /// <summary>Whether the result of work that was superseded while it ran is delivered (when nothing newer was delivered yet). Default on.</summary>
        public bool DeliverSuperseded { get; set; } = true;

        /// <summary>The generation of the newest request (0: none yet).</summary>
        public long Requested { get { lock (_lock) return _requested; } }

        /// <summary>The generation of the newest result delivered (0: none yet).</summary>
        public long Delivered { get { lock (_lock) return _delivered; } }

        /// <summary>A request is waiting or running whose result has not been delivered yet.</summary>
        public bool IsBusy { get { lock (_lock) return _requested > _delivered && _requested > _cancelledUpTo; } }

        /// <summary>The number of works running right now (for the tests and the timing log).</summary>
        public int Running { get { lock (_lock) return (int)_running; } }

        /// <summary>Asks for the work: it starts after `delay` unless another request comes first. Returns the generation of the request.</summary>
        public long Request(Func<CancellationToken, T> work, TimeSpan delay) => Request(() => work, delay, prepareOnPost: false);

        /// <summary>Like Request(work, delay), but what the work works on is taken only when it is about to start (after the wait and the gate), by `prepare` through `post` - on the
        /// UI thread, where the text of the document and the other open documents can be read: a keystroke costs nothing but the request, the snapshot is taken once per pause.
        /// `prepare` returns null when there is nothing to do.</summary>
        public long Request(Func<Func<CancellationToken, T>?> prepare, TimeSpan delay) => Request(prepare, delay, prepareOnPost: true);

        private long Request(Func<Func<CancellationToken, T>?> prepare, TimeSpan delay, bool prepareOnPost)
        {
            CancellationTokenSource source;
            long generation;
            lock (_lock)
            {
                _newest?.Cancel();
                _newest = source = new CancellationTokenSource();
                generation = ++_requested;
            }
            _ = RunAsync(prepare, prepareOnPost, delay, generation, source);
            return generation;
        }

        /// <summary>Forgets every request made so far (the document was closed, the text replaced): nothing of them is delivered any more.</summary>
        public void Cancel()
        {
            lock (_lock)
            {
                _newest?.Cancel();
                _newest = null;
                _cancelledUpTo = _requested;
            }
        }

        private bool Superseded(long generation)
        {
            lock (_lock) return generation != _requested || generation <= _cancelledUpTo;
        }

        private Task<Func<CancellationToken, T>?> OnPost(Func<Func<CancellationToken, T>?> prepare)
        {
            var done = new TaskCompletionSource<Func<CancellationToken, T>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _post(() =>
            {
                try { done.SetResult(prepare()); }
                catch (Exception ex) { done.SetException(ex); }
            });
            return done.Task;
        }

        private async Task RunAsync(Func<Func<CancellationToken, T>?> prepare, bool prepareOnPost, TimeSpan delay, long generation, CancellationTokenSource source)
        {
            var token = source.Token;
            bool gated = false;
            try
            {
                if (delay > TimeSpan.Zero) await _delay(delay, token).ConfigureAwait(false);
                if (Superseded(generation)) return;   // (a newer request came during the wait: it does the work)
                if (_gate != null)
                {
                    await _gate.WaitAsync(token).ConfigureAwait(false);
                    gated = true;
                    if (Superseded(generation)) return;
                }
                var work = prepareOnPost ? await OnPost(prepare).ConfigureAwait(false) : prepare();
                if (work == null || Superseded(generation)) return;   // (nothing to do; or a newer request came while the snapshot was taken: it starts after this)
                lock (_lock) _running++;
                T result;
                try { result = await Task.Run(() => work(token), CancellationToken.None).ConfigureAwait(false); }
                finally { lock (_lock) _running--; }
                if (gated) { _gate!.Release(); gated = false; }
                Deliver(generation, () => Completed?.Invoke(result, generation));
            }
            catch (OperationCanceledException) { /* superseded or cancelled: the newer request delivers */ }
            catch (Exception ex)
            {
                if (gated) { _gate!.Release(); gated = false; }
                Deliver(generation, () => Failed?.Invoke(ex, generation));
            }
            finally
            {
                if (gated) _gate!.Release();
                // (the source is not disposed: a newer request may still cancel it, and without a timer it holds nothing)
            }
        }

        private void Deliver(long generation, Action deliver)
        {
            if (!Deliverable(generation)) return;
            _post(() =>
            {
                lock (_lock)
                {
                    if (!Deliverable(generation)) return;
                    _delivered = generation;
                }
                deliver();
            });
        }

        // (under _lock or not: the fields are read as a whole; the decision that counts is the one in the posted delivery)
        private bool Deliverable(long generation)
        {
            lock (_lock)
            {
                if (generation <= _cancelledUpTo || generation <= _delivered) return false;   // cancelled, or a newer result is shown already
                return DeliverSuperseded || generation == _requested;
            }
        }
    }

    /// <summary>A change of a text: at `Offset` `Removed` characters were replaced by `Inserted` characters.</summary>
    public readonly record struct TextEdit(int Offset, int Removed, int Inserted);

    /// <summary>
    /// The changes of a document since some version, to carry places of an older text into the current one: a result that was computed for the text of a moment ago (the
    /// highlighting, the errors of the analysis that ran while the user typed on) is shown where its text is now, not where it was. Version n is the text after n changes.
    /// Only the last <see cref="Capacity"/> changes are kept; a place older than that cannot be carried (<see cref="CanMap"/>).
    /// </summary>
    public sealed class EditLog
    {
        public const int Capacity = 4096;
        private readonly List<TextEdit> _edits = new();
        private long _first;   // the version before _edits[0]

        /// <summary>The version of the current text.</summary>
        public long Version => _first + _edits.Count;

        public void Add(TextEdit edit)
        {
            _edits.Add(edit);
            if (_edits.Count > Capacity) { int drop = _edits.Count - Capacity / 2; _edits.RemoveRange(0, drop); _first += drop; }
        }

        /// <summary>Forgets every change (the text was replaced as a whole): older places cannot be carried any more.</summary>
        public void Reset()
        {
            _first += _edits.Count + 1;
            _edits.Clear();
        }

        public bool CanMap(long fromVersion) => fromVersion >= _first && fromVersion <= Version;

        /// <summary>An offset in the text of version `fromVersion` -> the same place in the current text. A place inside removed text goes to where the removal was;
        /// text inserted exactly at the place comes after it.</summary>
        public int MapOffset(int offset, long fromVersion)
        {
            for (long v = Math.Max(fromVersion, _first); v < Version; v++) offset = Map(offset, _edits[(int)(v - _first)]);
            return offset;
        }

        public static int Map(int offset, TextEdit edit)
        {
            if (offset <= edit.Offset) return offset;
            if (offset >= edit.Offset + edit.Removed) return offset - edit.Removed + edit.Inserted;
            return edit.Offset;
        }

        /// <summary>The spans of highlighting computed for the text of `fromVersion`, moved through the changes since: a span after a change moves with it, a span that the change
        /// touches grows or shrinks with it (its color stays until the next highlighting), a span that was removed as a whole goes.</summary>
        public List<HighlightSpan> MapSpans(IReadOnlyList<HighlightSpan> spans, long fromVersion)
        {
            var result = spans as List<HighlightSpan> ?? new List<HighlightSpan>(spans);
            for (long v = Math.Max(fromVersion, _first); v < Version; v++) result = Shift(result, _edits[(int)(v - _first)]);
            return result;
        }

        /// <summary>The spans after one change (sorted spans stay sorted).</summary>
        public static List<HighlightSpan> Shift(List<HighlightSpan> spans, TextEdit edit)
        {
            int delta = edit.Inserted - edit.Removed;
            int changeEnd = edit.Offset + edit.Removed;
            var result = new List<HighlightSpan>(spans.Count);
            foreach (var s in spans)
            {
                int start = s.Start, end = s.Start + s.Length;
                if (end <= edit.Offset) { result.Add(s); continue; }                                                  // before the change
                if (start >= changeEnd) { result.Add(new HighlightSpan(start + delta, s.Length, s.Category, s.Rgb)); continue; }   // after it (text inserted right before it, too)
                // the change touches the span: what is left of it before and after the change, with the inserted text (typing inside a comment keeps it a comment)
                int newStart = start < edit.Offset ? start : edit.Offset + edit.Inserted;
                int newEnd = end >= changeEnd ? end + delta : edit.Offset + edit.Inserted;
                if (newEnd > newStart) result.Add(new HighlightSpan(newStart, newEnd - newStart, s.Category, s.Rgb));   // (a span removed as a whole goes)
            }
            return result;
        }
    }
}
