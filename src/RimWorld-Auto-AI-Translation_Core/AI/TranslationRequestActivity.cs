using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTranslator_Core
{
    internal static class TranslationRequestSignalWaiter
    {
        internal static async Task<bool> WaitAsync(Task signal, Func<bool> cancellationRequested)
        {
            if (signal == null) return false;

            while (!signal.IsCompleted)
            {
                if (IsCancellationRequested(cancellationRequested)) return false;
                await Task.Delay(100);
            }

            return true;
        }

        private static bool IsCancellationRequested(Func<bool> cancellationRequested)
        {
            if (cancellationRequested == null) return false;
            try { return cancellationRequested(); }
            catch { return true; }
        }
    }

    public enum TranslationRequestStage
    {
        Created,
        Queued,
        Dispatching,
        Sent,
        WaitingResponse,
        Completed,
        Failed,
        Cancelled
    }

    public struct TranslationRequestActivitySnapshot
    {
        public int Queued;
        public int Dispatching;
        public int Active;
        public int RetryWaiting;

        public int TotalOutstanding => Queued + Dispatching + Active + RetryWaiting;
    }

    internal static class TranslationRequestActivity
    {
        private static int _queued;
        private static int _dispatching;
        private static int _active;
        private static int _retryWaiting;

        internal static RequestLease CreateRequest()
        {
            return new RequestLease();
        }

        internal static IDisposable BeginRetryWait()
        {
            Interlocked.Increment(ref _retryWaiting);
            return new CounterLease(() => Interlocked.Decrement(ref _retryWaiting));
        }

        internal static TranslationRequestActivitySnapshot GetSnapshot()
        {
            return new TranslationRequestActivitySnapshot
            {
                Queued = Math.Max(0, Volatile.Read(ref _queued)),
                Dispatching = Math.Max(0, Volatile.Read(ref _dispatching)),
                Active = Math.Max(0, Volatile.Read(ref _active)),
                RetryWaiting = Math.Max(0, Volatile.Read(ref _retryWaiting))
            };
        }

        internal static async Task WaitForDrainAsync(
            int stableObservationCount = 3,
            int pollDelayMilliseconds = 50)
        {
            int requiredStableObservations = Math.Max(1, stableObservationCount);
            int safeDelay = Math.Max(1, pollDelayMilliseconds);
            int stableObservations = 0;
            while (stableObservations < requiredStableObservations)
            {
                if (GetSnapshot().TotalOutstanding == 0) stableObservations++;
                else stableObservations = 0;

                if (stableObservations < requiredStableObservations)
                    await Task.Delay(safeDelay);
            }
        }

        internal sealed class RequestLease : IDisposable
        {
            private readonly object _gate = new object();
            private TranslationRequestStage _stage = TranslationRequestStage.Created;
            private bool _disposed;

            internal TranslationRequestStage Stage
            {
                get { lock (_gate) return _stage; }
            }

            internal void MarkQueued() => TransitionTo(TranslationRequestStage.Queued);
            internal void MarkDispatching() => TransitionTo(TranslationRequestStage.Dispatching);
            internal void MarkSent() => TransitionTo(TranslationRequestStage.Sent);
            internal void MarkWaitingResponse() => TransitionTo(TranslationRequestStage.WaitingResponse);
            internal void MarkCompleted() => TransitionTo(TranslationRequestStage.Completed);
            internal void MarkFailed() => TransitionTo(TranslationRequestStage.Failed);
            internal void MarkCancelled() => TransitionTo(TranslationRequestStage.Cancelled);

            private void TransitionTo(TranslationRequestStage next)
            {
                lock (_gate)
                {
                    if (_disposed || IsTerminal(_stage)) return;
                    RemoveCounter(_stage);
                    _stage = next;
                    AddCounter(_stage);
                }
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    RemoveCounter(_stage);
                }
            }

            private static bool IsTerminal(TranslationRequestStage stage)
            {
                return stage == TranslationRequestStage.Completed ||
                       stage == TranslationRequestStage.Failed ||
                       stage == TranslationRequestStage.Cancelled;
            }

            private static void AddCounter(TranslationRequestStage stage)
            {
                if (stage == TranslationRequestStage.Queued) Interlocked.Increment(ref _queued);
                else if (stage == TranslationRequestStage.Dispatching) Interlocked.Increment(ref _dispatching);
                else if (stage == TranslationRequestStage.Sent || stage == TranslationRequestStage.WaitingResponse)
                    Interlocked.Increment(ref _active);
            }

            private static void RemoveCounter(TranslationRequestStage stage)
            {
                if (stage == TranslationRequestStage.Queued) Interlocked.Decrement(ref _queued);
                else if (stage == TranslationRequestStage.Dispatching) Interlocked.Decrement(ref _dispatching);
                else if (stage == TranslationRequestStage.Sent || stage == TranslationRequestStage.WaitingResponse)
                    Interlocked.Decrement(ref _active);
            }
        }

        private sealed class CounterLease : IDisposable
        {
            private Action _release;

            internal CounterLease(Action release)
            {
                _release = release;
            }

            public void Dispose()
            {
                Action release = Interlocked.Exchange(ref _release, null);
                release?.Invoke();
            }
        }
    }

    internal static class TranslationRequestConcurrencyGate
    {
        private static readonly object Gate = new object();
        private static readonly Queue<Waiter> Waiters = new Queue<Waiter>();
        private static int _active;
        private static int _limit = 1;

        internal static async Task<IDisposable> AcquireAsync(
            int maximumConcurrency,
            Func<bool> cancellationRequested)
        {
            Waiter waiter = new Waiter();
            lock (Gate)
            {
                _limit = Math.Max(1, maximumConcurrency);
                Waiters.Enqueue(waiter);
                PumpLocked();
            }

            while (!waiter.Completion.Task.IsCompleted)
            {
                if (IsCancellationRequested(cancellationRequested))
                {
                    bool cancelledBeforeGrant = false;
                    lock (Gate)
                    {
                        if (!waiter.Completion.Task.IsCompleted)
                        {
                            waiter.Cancelled = true;
                            cancelledBeforeGrant = true;
                            PumpLocked();
                        }
                    }
                    if (cancelledBeforeGrant) return null;
                    break;
                }

                await Task.WhenAny(waiter.Completion.Task, Task.Delay(100));
            }

            IDisposable lease = await waiter.Completion.Task;
            if (!IsCancellationRequested(cancellationRequested)) return lease;

            lease?.Dispose();
            return null;
        }

        private static void PumpLocked()
        {
            while (_active < _limit && Waiters.Count > 0)
            {
                Waiter waiter = Waiters.Dequeue();
                if (waiter.Cancelled) continue;
                _active++;
                waiter.Completion.TrySetResult(new SlotLease());
            }
        }

        private static bool IsCancellationRequested(Func<bool> cancellationRequested)
        {
            if (cancellationRequested == null) return false;
            try { return cancellationRequested(); }
            catch { return true; }
        }

        private sealed class Waiter
        {
            internal readonly TaskCompletionSource<IDisposable> Completion =
                new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal bool Cancelled;
        }

        private sealed class SlotLease : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                lock (Gate)
                {
                    _active = Math.Max(0, _active - 1);
                    PumpLocked();
                }
            }
        }
    }

    public static partial class AutoTranslatorAPI
    {
        public static TranslationRequestActivitySnapshot GetTranslationRequestActivity()
        {
            return TranslationRequestActivity.GetSnapshot();
        }

        public static bool HasOutstandingTranslationWork
        {
            get { return TranslationRequestActivity.GetSnapshot().TotalOutstanding > 0; }
        }

        public static Task WaitForTranslationRequestDrainAsync()
        {
            return TranslationRequestActivity.WaitForDrainAsync();
        }
    }
}
