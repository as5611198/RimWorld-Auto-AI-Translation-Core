using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AutoTranslator_Core
{
    internal sealed class TranslationBatchItemResult
    {
        public string Value;
        public string FailureReason;
        public string Detail;

        public bool IsSuccess
        {
            get { return string.IsNullOrWhiteSpace(FailureReason); }
        }
    }

    internal static class TranslationBatchFaultGuard
    {
        public static async Task RunChunkAsync(
            IEnumerable<string> sources,
            IDictionary<string, TranslationBatchItemResult> results,
            string failureReason,
            Func<Task> action,
            Action<Exception> onError)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            if (action == null) throw new ArgumentNullException(nameof(action));

            try
            {
                await action();
            }
            catch (Exception ex)
            {
                RecordMissingFailures(
                    sources,
                    results,
                    failureReason,
                    "Translation batch task failed: " + ex.Message);
                if (onError != null)
                {
                    try
                    {
                        onError(ex);
                    }
                    catch
                    {
                    }
                }
            }
        }

        public static void RecordMissingFailures(
            IEnumerable<string> sources,
            IDictionary<string, TranslationBatchItemResult> results,
            string failureReason,
            string detail)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            lock (results)
            {
                foreach (string source in sources ?? Enumerable.Empty<string>())
                {
                    string key = source ?? string.Empty;
                    if (results.ContainsKey(key)) continue;
                    results[key] = new TranslationBatchItemResult
                    {
                        FailureReason = failureReason ?? string.Empty,
                        Detail = detail ?? string.Empty
                    };
                }
            }
        }

        public static List<TranslationBatchItemResult> CreateOrderedResults(
            IEnumerable<string> inputs,
            IDictionary<string, TranslationBatchItemResult> results,
            string missingFailureReason,
            string missingDetail)
        {
            if (results == null) throw new ArgumentNullException(nameof(results));
            List<TranslationBatchItemResult> ordered = new List<TranslationBatchItemResult>();
            lock (results)
            {
                foreach (string input in inputs ?? Enumerable.Empty<string>())
                {
                    string key = input ?? string.Empty;
                    TranslationBatchItemResult result;
                    if (results.TryGetValue(key, out result) && result != null)
                    {
                        ordered.Add(result);
                    }
                    else
                    {
                        ordered.Add(new TranslationBatchItemResult
                        {
                            FailureReason = missingFailureReason ?? string.Empty,
                            Detail = missingDetail ?? string.Empty
                        });
                    }
                }
            }
            return ordered;
        }
    }
}
