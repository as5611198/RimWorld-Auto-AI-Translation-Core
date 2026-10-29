using System.Diagnostics;
using System;
using UnityEngine;

namespace AutoTranslator_Core
{
    internal static class TranslationFrameBudget
    {
        [ThreadStatic] private static int synchronous;
        internal static IDisposable UnlimitedForSynchronousFixture() { synchronous++; return new Scope(); }
        internal static IDisposable ForSynchronousNativeStartup() { synchronous++; return new Scope(); }
        private sealed class Scope : IDisposable { public void Dispose() { synchronous--; } }
        private static Func<int> frameClock = () => Time.frameCount;
        internal static int CurrentFrame => frameClock();
        private static int frame = int.MinValue;
        private static long deadline;
        internal static bool HasTime
        {
            get
            {
                if (synchronous != 0) return true;
                int current = frameClock();
                if (current != frame)
                {
                    frame = current;
                    // Native enumerable loading events update at a lower cadence.
                    // Allow 20 ms there; interactive play keeps its 2 ms budget.
                    deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * (StartupTranslationGate.IsPumping ? 20 : 2) / 1000;
                }
                return Stopwatch.GetTimestamp() < deadline;
            }
        }
    }
}
