using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AutoTranslator_Core
{
    internal sealed class TranslationUsageRequestContext
    {
        public string PackageId;
        public string Purpose;
        public string ScopeId;
        public long SourceCharacters;
        public int ItemCount;
        public bool Exempt;
    }

    internal sealed class TranslationUsageReservationHandle
    {
        public string RequestId;
    }

    internal static class TranslationUsageCoordinator
    {
        private sealed class ContextRestore : IDisposable
        {
            private readonly TranslationUsageRequestContext _previous;
            private bool _disposed;

            public ContextRestore(TranslationUsageRequestContext previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                CurrentContext.Value = _previous;
            }
        }

        private static readonly object Gate = new object();
        private static readonly AsyncLocal<TranslationUsageRequestContext> CurrentContext =
            new AsyncLocal<TranslationUsageRequestContext>();
        private static TranslationUsageLedger _ledger;
        private static bool _wasResumed;

        internal static void BeginRun(
            string journalPath,
            string runKey,
            long maximumSourceCharacters,
            long maximumEstimatedTokens)
        {
            lock (Gate)
            {
                _ledger = TranslationUsageLedger.OpenOrCreate(
                    journalPath,
                    runKey,
                    maximumSourceCharacters,
                    maximumEstimatedTokens);
                _wasResumed = _ledger.WasResumed;
            }
        }

        internal static void EndRun(bool completed)
        {
            lock (Gate)
            {
                if (_ledger != null && completed && _ledger.GetSnapshot().Status != TranslationUsageRunStatus.PausedByBudget)
                    _ledger.Complete();
                _ledger = null;
                _wasResumed = false;
            }
        }

        internal static IDisposable PushRequestContext(
            string packageId,
            string purpose,
            string scopeId,
            long sourceCharacters,
            bool exempt = false,
            int itemCount = 0)
        {
            TranslationUsageRequestContext previous = CurrentContext.Value;
            CurrentContext.Value = new TranslationUsageRequestContext
            {
                PackageId = packageId ?? string.Empty,
                Purpose = purpose ?? "translation",
                ScopeId = scopeId ?? string.Empty,
                SourceCharacters = Math.Max(0L, sourceCharacters),
                ItemCount = Math.Max(0, itemCount),
                Exempt = exempt
            };
            return new ContextRestore(previous);
        }

        internal static bool TryReserve(
            TranslatorProvider provider,
            string jsonPayload,
            out TranslationUsageReservationHandle handle,
            out string denialReason)
        {
            handle = null;
            denialReason = string.Empty;
            TranslationUsageRequestContext context = CurrentContext.Value;
            TranslationUsageLedger ledger;
            lock (Gate) ledger = _ledger;
            if (ledger == null || context == null || context.Exempt) return true;

            string model = ReadModel(jsonPayload);
            long estimate = EstimateTokens(jsonPayload);
            string requestId = ComputeSha256(string.Join("\n", new[]
            {
                context.PackageId ?? string.Empty,
                context.Purpose ?? string.Empty,
                context.ScopeId ?? string.Empty,
                provider.ToString(),
                model,
                jsonPayload ?? string.Empty
            }));

            if (!ledger.TryReserve(
                    requestId,
                    context.PackageId,
                    context.Purpose,
                    provider.ToString(),
                    model,
                    context.SourceCharacters,
                    estimate,
                    out denialReason))
            {
                return false;
            }

            handle = new TranslationUsageReservationHandle { RequestId = requestId };
            return true;
        }

        internal static void Reconcile(
            TranslationUsageReservationHandle handle,
            bool requestWasDispatched,
            long httpCode,
            bool isSuccess,
            string responseBody)
        {
            if (handle == null) return;
            TranslationUsageLedger ledger;
            lock (Gate) ledger = _ledger;
            if (ledger == null) return;

            if (!requestWasDispatched)
            {
                ledger.Release(handle.RequestId);
                return;
            }

            if (!isSuccess && httpCode >= 400 && httpCode < 500 && httpCode != 408)
            {
                ledger.Release(handle.RequestId);
                return;
            }

            if (TryReadUsage(responseBody, out long? input, out long? output, out long? reasoning, out long? total))
            {
                ledger.Commit(handle.RequestId, input, output, reasoning, total);
                return;
            }

            if (isSuccess)
                ledger.Commit(handle.RequestId, null, null, null, null);
            else
                ledger.MarkAmbiguous(handle.RequestId);
        }

        internal static bool IsPausedByBudget
        {
            get
            {
                lock (Gate)
                {
                    return _ledger != null &&
                           _ledger.GetSnapshot().Status == TranslationUsageRunStatus.PausedByBudget;
                }
            }
        }

        internal static bool WasResumed
        {
            get { lock (Gate) return _wasResumed; }
        }

        internal static TranslationUsageSnapshot GetSnapshot()
        {
            lock (Gate) return _ledger?.GetSnapshot();
        }

        internal static long EstimateTokens(string jsonPayload)
        {
            string payload = jsonPayload ?? string.Empty;
            long inputEstimate = Math.Max(1L, (payload.Length + 3L) / 4L);
            long outputLimit = 0L;
            try
            {
                JObject obj = JObject.Parse(payload);
                outputLimit = Math.Max(0L, obj["max_tokens"]?.Value<long>() ??
                    obj["maxOutputTokens"]?.Value<long>() ?? 0L);
                if (outputLimit == 0L)
                    outputLimit = Math.Max(0L,
                        obj["generationConfig"]?["maxOutputTokens"]?.Value<long>() ?? 0L);
            }
            catch { }
            long conservativeOutput = outputLimit > 0L ? outputLimit : Math.Max(256L, inputEstimate);
            return SaturatingAdd(inputEstimate, conservativeOutput);
        }

        internal static long EstimateInputTokens(string jsonPayload)
        {
            string payload = jsonPayload ?? string.Empty;
            return Math.Max(1L, (payload.Length + 3L) / 4L);
        }

        internal static TranslationUsageRequestContext GetCurrentRequestContext()
        {
            TranslationUsageRequestContext context = CurrentContext.Value;
            if (context == null) return null;
            return new TranslationUsageRequestContext
            {
                PackageId = context.PackageId,
                Purpose = context.Purpose,
                ScopeId = context.ScopeId,
                SourceCharacters = context.SourceCharacters,
                ItemCount = context.ItemCount,
                Exempt = context.Exempt
            };
        }

        private static bool TryReadUsage(
            string responseBody,
            out long? input,
            out long? output,
            out long? reasoning,
            out long? total)
        {
            input = output = reasoning = total = null;
            try
            {
                JObject root = JObject.Parse(responseBody ?? string.Empty);
                JToken usage = root["usage"];
                if (usage != null)
                {
                    input = ReadNullableLong(usage["prompt_tokens"] ?? usage["input_tokens"]);
                    output = ReadNullableLong(usage["completion_tokens"] ?? usage["output_tokens"]);
                    reasoning = ReadNullableLong(usage["completion_tokens_details"]?["reasoning_tokens"]);
                    total = ReadNullableLong(usage["total_tokens"]);
                }
                else
                {
                    usage = root["usageMetadata"];
                    if (usage != null)
                    {
                        input = ReadNullableLong(usage["promptTokenCount"]);
                        output = ReadNullableLong(usage["candidatesTokenCount"]);
                        reasoning = ReadNullableLong(usage["thoughtsTokenCount"]);
                        total = ReadNullableLong(usage["totalTokenCount"]);
                    }
                }
                if (!total.HasValue && (input.HasValue || output.HasValue || reasoning.HasValue))
                    total = SaturatingAdd(SaturatingAdd(input ?? 0L, output ?? 0L), reasoning ?? 0L);
                return input.HasValue || output.HasValue || reasoning.HasValue || total.HasValue;
            }
            catch
            {
                return false;
            }
        }

        private static string ReadModel(string jsonPayload)
        {
            try { return JObject.Parse(jsonPayload ?? string.Empty)["model"]?.ToString() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private static long? ReadNullableLong(JToken token)
        {
            return token != null && long.TryParse(token.ToString(), out long value)
                ? (long?)Math.Max(0L, value)
                : null;
        }

        private static string ComputeSha256(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        private static long SaturatingAdd(long left, long right)
        {
            left = Math.Max(0L, left);
            right = Math.Max(0L, right);
            return left > long.MaxValue - right ? long.MaxValue : left + right;
        }
    }
}
