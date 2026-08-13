using AutoTranslator_Core;
using System;
using System.Threading.Tasks;

namespace TranslationRequestActivitySelfTest
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                AssertEmpty("initial state");

                using (TranslationRequestActivity.RequestLease first = TranslationRequestActivity.CreateRequest())
                {
                    first.MarkQueued();
                    AssertCounts(1, 0, 0, 0, "queued");
                    first.MarkDispatching();
                    AssertCounts(0, 1, 0, 0, "dispatching");
                    first.MarkSent();
                    AssertCounts(0, 0, 1, 0, "sent");
                    first.MarkWaitingResponse();
                    AssertCounts(0, 0, 1, 0, "waiting response");
                    first.MarkFailed();
                    AssertEmpty("failed request releases its slot");
                }

                using (TranslationRequestActivity.RequestLease successor = TranslationRequestActivity.CreateRequest())
                {
                    successor.MarkQueued();
                    successor.MarkSent();
                    successor.MarkCompleted();
                    AssertEmpty("successor completes independently");
                }

                using (TranslationRequestActivity.BeginRetryWait())
                {
                    AssertCounts(0, 0, 0, 1, "retry wait visible");
                    if (!AutoTranslatorAPI.HasOutstandingTranslationWork)
                        throw new InvalidOperationException("outstanding work flag was false");
                }

                AssertEmpty("retry wait released");
                TestConcurrencyQueue().GetAwaiter().GetResult();
                TestStopDrain().GetAwaiter().GetResult();
                Console.WriteLine("PASS: translation request activity lifecycle");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
        }

        private static async Task TestConcurrencyQueue()
        {
            IDisposable first = await TranslationRequestConcurrencyGate.AcquireAsync(1, () => false);
            Task<IDisposable> secondTask = TranslationRequestConcurrencyGate.AcquireAsync(1, () => false);
            await Task.Delay(5200);
            if (secondTask.IsCompleted)
                throw new InvalidOperationException("second request bypassed the concurrency queue");

            first.Dispose();
            IDisposable second = await secondTask;
            if (second == null)
                throw new InvalidOperationException("successor did not receive the released slot");
            second.Dispose();

            IDisposable blocker = await TranslationRequestConcurrencyGate.AcquireAsync(1, () => false);
            bool cancelled = false;
            Task<IDisposable> cancelledTask = TranslationRequestConcurrencyGate.AcquireAsync(1, () => cancelled);
            cancelled = true;
            IDisposable cancelledLease = await cancelledTask;
            if (cancelledLease != null)
                throw new InvalidOperationException("cancelled queued request received a slot");
            blocker.Dispose();

            var dispatchSignal = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> dispatchWait = TranslationRequestSignalWaiter.WaitAsync(
                dispatchSignal.Task,
                () => false);
            await Task.Delay(5200);
            if (dispatchWait.IsCompleted)
                throw new InvalidOperationException("unsent request timed out while waiting for main-thread dispatch");
            dispatchSignal.TrySetResult(true);
            if (!await dispatchWait)
                throw new InvalidOperationException("dispatch signal was not observed");

            bool cancelDispatch = false;
            var neverSignals = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool> cancelledDispatch = TranslationRequestSignalWaiter.WaitAsync(
                neverSignals.Task,
                () => cancelDispatch);
            cancelDispatch = true;
            if (await cancelledDispatch)
                throw new InvalidOperationException("cancelled dispatch wait reported success");
        }

        private static async Task TestStopDrain()
        {
            bool cancelled = false;
            IDisposable blocker = await TranslationRequestConcurrencyGate.AcquireAsync(1, () => false);
            TranslationRequestActivity.RequestLease queued = TranslationRequestActivity.CreateRequest();
            queued.MarkQueued();
            Task<IDisposable> queuedSlot = TranslationRequestConcurrencyGate.AcquireAsync(1, () => cancelled);
            TranslationRequestActivity.RequestLease active = TranslationRequestActivity.CreateRequest();
            active.MarkSent();
            IDisposable retry = TranslationRequestActivity.BeginRetryWait();
            AssertCounts(1, 0, 1, 1, "stop precondition");

            Task drain = TranslationRequestActivity.WaitForDrainAsync(2, 10);
            await Task.Delay(30);
            if (drain.IsCompleted)
                throw new InvalidOperationException("pipeline reported stopped before outstanding work drained");

            cancelled = true;
            IDisposable cancelledSlot = await queuedSlot;
            if (cancelledSlot != null)
                throw new InvalidOperationException("stop did not cancel the queued slot");
            queued.MarkCancelled();
            active.MarkCancelled();
            retry.Dispose();
            blocker.Dispose();
            queued.Dispose();
            active.Dispose();
            await drain;
            AssertEmpty("stop drains queued, active, and retry activity");
        }

        private static void AssertEmpty(string name)
        {
            AssertCounts(0, 0, 0, 0, name);
        }

        private static void AssertCounts(int queued, int dispatching, int active, int retry, string name)
        {
            TranslationRequestActivitySnapshot snapshot = TranslationRequestActivity.GetSnapshot();
            if (snapshot.Queued != queued || snapshot.Dispatching != dispatching ||
                snapshot.Active != active || snapshot.RetryWaiting != retry)
            {
                throw new InvalidOperationException(
                    name + ": expected " + queued + "/" + dispatching + "/" + active + "/" + retry +
                    ", got " + snapshot.Queued + "/" + snapshot.Dispatching + "/" +
                    snapshot.Active + "/" + snapshot.RetryWaiting);
            }
        }
    }
}
