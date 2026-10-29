using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AutoTranslator_Core
{
    internal static class TranslationValidationQueue
    {
        private sealed class Job
        {
            internal string Type;
            internal string[] Keys;
            internal int Index, Epoch, Cancellation;
            internal Verse.LoadedLanguage Language;
            internal TargetLanguage Target;
            internal DateTime Deadline;
            internal readonly Dictionary<string, DefTranslationPathResult> Results = new Dictionary<string, DefTranslationPathResult>();
            internal readonly TaskCompletionSource<Dictionary<string, DefTranslationPathResult>> Completion =
                new TaskCompletionSource<Dictionary<string, DefTranslationPathResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private static readonly ConcurrentQueue<Job> jobs = new ConcurrentQueue<Job>();
        internal static async Task<Dictionary<string, DefTranslationPathResult>> CaptureAsync(string type, IEnumerable<string> keys)
        {
            var job = new Job { Type = type, Keys = keys.ToArray(), Epoch = AutoTranslatorScanner.GameDataEpoch,
                Language = Verse.LanguageDatabase.activeLanguage, Target = AutoTranslatorMod.Settings.TargetLang,
                Cancellation = AutoTranslatorSettings.PipelineCancellationGeneration, Deadline = DateTime.UtcNow.AddMinutes(2) };
            jobs.Enqueue(job);
            var completed = await Task.WhenAny(job.Completion.Task, Task.Delay(TimeSpan.FromMinutes(2))).ConfigureAwait(false);
            if (completed != job.Completion.Task) { job.Completion.TrySetCanceled(); throw new TimeoutException("Live translation validation timed out."); }
            return await job.Completion.Task.ConfigureAwait(false);
        }
        internal static void Pump()
        {
            while (TranslationFrameBudget.HasTime && jobs.TryPeek(out var job))
            {
                if (job.Completion.Task.IsCompleted || job.Epoch != AutoTranslatorScanner.GameDataEpoch ||
                    !ReferenceEquals(job.Language, Verse.LanguageDatabase.activeLanguage) || job.Target != AutoTranslatorMod.Settings.TargetLang ||
                    job.Cancellation != AutoTranslatorSettings.PipelineCancellationGeneration ||
                    AutoTranslatorSettings.IsCancellationRequested || DateTime.UtcNow > job.Deadline)
                { jobs.TryDequeue(out _); job.Completion.TrySetCanceled(); continue; }
                try
                {
                    if (job.Index < job.Keys.Length)
                    {
                        string key = job.Keys[job.Index++];
                        job.Results[key] = AutoTranslatorScanner.ValidateDefTranslation(job.Type, key);
                    }
                    else { jobs.TryDequeue(out _); job.Completion.TrySetResult(job.Results); }
                }
                catch (Exception ex) { jobs.TryDequeue(out _); job.Completion.TrySetException(ex); }
            }
        }
    }
}
