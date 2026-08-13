using HarmonyLib;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責背景攔截佇列與過濾條件。
// EN: This file manages the background UI interception queue and filters.

namespace AutoTranslator_Core
{
    // 這個類別負責 UIInterceptor 的主要流程與狀態。
    // EN: This class manages the main workflow and state for UIInterceptor.
    public static partial class UIInterceptor
    {

        // 這個方法負責嘗試執行 Consume佇列Budget 並回報是否成功。
        // EN: This method tries to consume queue budget and reports whether it succeeded.
        private static bool TryConsumeQueueBudget()
        {
            int frame = Time.frameCount;
            lock (_frameBudgetLock)
            {
                if (_lastQueueFrame != frame)
                {
                    _lastQueueFrame = frame;
                    _queuedThisFrame = 0;
                }

                if (_queuedThisFrame >= MaxNewQueueItemsPerFrame) return false;
                _queuedThisFrame++;
                return true;
            }
        }

        private static bool TryConsumeClassificationFrameBudget()
        {
            int frame = Time.frameCount;
            lock (_classificationFrameBudgetLock)
            {
                if (_lastClassificationFrame != frame)
                {
                    _lastClassificationFrame = frame;
                    _classifiedThisFrame = 0;
                }

                if (_classifiedThisFrame >= MaxNewClassificationItemsPerFrame) return false;
                _classifiedThisFrame++;
                return true;
            }
        }

        private static bool TryConsumeNewTranslationScanBudget()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            lock (_newTranslationScanLock)
            {
                if (nowTicks >= _nextNewTranslationScanTicks)
                {
                    _nextNewTranslationScanTicks = nowTicks + NewTranslationScanInterval.Ticks;
                    _queuedThisScanWindow = 0;
                }

                if (_queuedThisScanWindow >= MaxNewQueueItemsPerScanWindow) return false;
                _queuedThisScanWindow++;
                return true;
            }
        }

        private static bool TryConsumeNewClassificationScanBudget()
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            lock (_newClassificationScanLock)
            {
                if (nowTicks >= _nextNewClassificationScanTicks)
                {
                    _nextNewClassificationScanTicks = nowTicks + NewClassificationScanInterval.Ticks;
                    _classifiedThisScanWindow = 0;
                }

                if (_classifiedThisScanWindow >= MaxNewClassificationItemsPerScanWindow) return false;
                _classifiedThisScanWindow++;
                return true;
            }
        }

        internal static bool QueueForClassification(string text)
        {
            // Emergency stop applies to the legacy UI interception pipeline as
            // well.  Do not let render hooks refill the queue while the shared
            // translation cancellation flag is waiting for the next explicit
            // user action to reset it.
            if (AutoTranslatorSettings.IsCancellationRequested) return false;
            if (System.Threading.Volatile.Read(ref _classificationApproxCount) >= MaxQueuedClassifications) return false;
            if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return false;
            if (ShouldBypassUIPatchText(text)) return false;
            if (!TryConsumeNewClassificationScanBudget()) return false;
            if (!TryConsumeClassificationFrameBudget()) return false;

            string cacheKey = BuildCacheKey(text);
            if (PendingClassifications.TryAdd(cacheKey, true))
            {
                ClassificationQueue.Enqueue(text);
                System.Threading.Interlocked.Increment(ref _classificationApproxCount);
                return true;
            }

            return true;
        }


        // 這個方法負責排入 For翻譯 佇列。
        // EN: This method queues for translation.
        public static bool QueueForTranslation(string text)
        {
            return QueueForTranslationInternal(text, true);
        }

        private static bool QueueForTranslationFromBackground(string text)
        {
            return QueueForTranslationInternal(text, false);
        }

        private static bool QueueForTranslationInternal(string text, bool consumeFrameBudget)
        {
            if (AutoTranslatorSettings.IsCancellationRequested) return false;
            if (!AutoTranslatorMod.Settings.EnableUINewTranslation) return false;
            if (System.Threading.Volatile.Read(ref _queuedApproxCount) >= MaxQueuedTranslations) return false;

            if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return false;
            if (!ShouldInterceptText(text))
            {
                return false;
            }


            if (IsIgnored(text)) return false;


            string lookupText = GetTranslationLookupText(text);
            string cacheKey = BuildCacheKey(lookupText);
            if (PendingTranslations.ContainsKey(cacheKey)) return true;


            string languageSample = GetLanguageDetectionSample(lookupText);
            if (string.IsNullOrWhiteSpace(languageSample) || languageSample.All(char.IsDigit) || !LetterRegex.IsMatch(languageSample))
            {
                RememberIgnored(text);
                return false;
            }

            var targetLang = AutoTranslatorMod.Settings.TargetLang;
            bool isForeignText = false;


            bool hasEnglish = EnglishRegex.IsMatch(languageSample);
            bool hasCyrillic = CyrillicRegex.IsMatch(languageSample);
            bool hasKana = KanaRegex.IsMatch(languageSample);
            bool hasHangul = HangulRegex.IsMatch(languageSample);
            bool hasCJK = CJKRegex.IsMatch(languageSample);

            switch (targetLang)
            {
                case TargetLanguage.Traditional:
                case TargetLanguage.Simplified:
                    if (hasEnglish || hasCyrillic || hasKana || hasHangul) isForeignText = true;
                    break;
                case TargetLanguage.Japanese:
                    if (hasEnglish || hasCyrillic || hasHangul || (hasCJK && !hasKana)) isForeignText = true;
                    break;
                case TargetLanguage.Korean:
                    if (hasEnglish || hasCyrillic || hasKana || hasCJK) isForeignText = true;
                    break;
                case TargetLanguage.English:
                    if (hasCJK || hasKana || hasHangul || hasCyrillic) isForeignText = true;
                    break;
                case TargetLanguage.Russian:
                case TargetLanguage.Ukrainian:
                    if (hasEnglish || hasCJK || hasKana || hasHangul) isForeignText = true;
                    break;
                default:
                    if (hasEnglish || hasCJK) isForeignText = true;
                    break;
            }


            if (!isForeignText)
            {
                RememberIgnored(text);
                return false;
            }

            if (!TryConsumeNewTranslationScanBudget()) return false;
            if (consumeFrameBudget && !TryConsumeQueueBudget()) return false;

            if (PendingTranslations.TryAdd(cacheKey, true))
            {
                TranslationQueue.Enqueue(lookupText);
                System.Threading.Interlocked.Increment(ref _queuedApproxCount);
                return true;
            }

            return true;
        }

        private static async Task BackgroundClassificationWorker()
        {
            var token = _workerCts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(250, token);

                    if (AutoTranslatorSettings.IsCancellationRequested)
                    {
                        bool hadPendingClassifications =
                            System.Threading.Volatile.Read(ref _classificationApproxCount) > 0 ||
                            !PendingClassifications.IsEmpty;
                        DiscardQueuedClassifications();
                        if (hadPendingClassifications) ClearRenderDecisionCache();
                        continue;
                    }

                    int batchGeneration;
                    TargetLanguage batchTargetLanguage;
                    List<string> batch = new List<string>();
                    lock (_cachePersistenceLock)
                    {
                        batchGeneration = System.Threading.Volatile.Read(ref _uiLanguageGeneration);
                        batchTargetLanguage = AutoTranslatorMod.Settings.TargetLang;
                        while (batch.Count < 80 && ClassificationQueue.TryDequeue(out string text))
                        {
                            System.Threading.Interlocked.Decrement(ref _classificationApproxCount);
                            batch.Add(text);
                        }
                    }

                    if (batch.Count == 0) continue;

                    bool changedRenderDecision = false;
                    lock (_cachePersistenceLock)
                    {
                        if (!IsUILanguageContextCurrent(batchGeneration, batchTargetLanguage))
                        {
                            foreach (string original in batch)
                            {
                                PendingClassifications.TryRemove(BuildCacheKey(batchTargetLanguage, original), out _);
                            }
                            continue;
                        }

                        foreach (string original in batch)
                        {
                            string classificationKey = BuildCacheKey(batchTargetLanguage, original);
                            try
                            {
                                ClassifyUIRenderText(original);
                                changedRenderDecision = true;
                            }
                            catch (Exception ex)
                            {
                                RememberRenderDecision(
                                    BuildRenderDecisionKey(original),
                                    UIRenderDecisionKind.Pending,
                                    null,
                                    DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(3).Ticks);
                                Log.Warning($"[AutoTranslationCore] UI classification failed: {ex.Message}");
                            }
                            finally
                            {
                                PendingClassifications.TryRemove(classificationKey, out _);
                            }
                        }

                        if (changedRenderDecision) SaveCacheIfDue();
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warning($"[AutoTranslationCore] BackgroundClassificationWorker error: {ex.Message}");

                    try { await Task.Delay(3000, token); } catch (TaskCanceledException) { break; }
                }
            }
        }

        private static void ClassifyUIRenderText(string original)
        {
            if (string.IsNullOrWhiteSpace(original) || ShouldBypassUIPatchText(original)) return;

            string renderKey = BuildRenderDecisionKey(original);
            if (!ShouldInterceptText(original))
            {
                RememberRenderDecision(renderKey, UIRenderDecisionKind.PassThrough, null, 0L);
                return;
            }

            if (IsIgnored(original))
            {
                RememberRenderDecision(renderKey, UIRenderDecisionKind.PassThrough, null, 0L);
                return;
            }

            if (TryGetCachedTranslationKnownSafe(original, out string translated))
            {
                RememberRenderDecision(renderKey, UIRenderDecisionKind.Translated, translated, 0L);
                return;
            }

            if (!AutoTranslatorMod.Settings.EnableUINewTranslation)
            {
                RememberRenderDecision(renderKey, UIRenderDecisionKind.PassThrough, null, 0L);
                return;
            }

            bool queued = QueueForTranslationFromBackground(original);
            RememberRenderDecision(
                renderKey,
                queued ? UIRenderDecisionKind.Pending : UIRenderDecisionKind.PassThrough,
                null,
                queued ? DateTime.UtcNow.Ticks + RenderPendingRetryInterval.Ticks : 0L);
        }


        // 這個方法負責處理 Background翻譯Worker 相關流程。
        // EN: This method handles background translation worker.
        private static async Task BackgroundTranslationWorker()
        {
            var token = _workerCts.Token;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2000, token);
                    if (AutoTranslatorSettings.IsCancellationRequested)
                    {
                        DiscardQueuedTranslations();
                        SaveCacheIfDue();
                        continue;
                    }

                    if (!AutoTranslatorMod.Settings.EnableUINewTranslation)
                    {
                        DiscardQueuedTranslations();
                        SaveCacheIfDue();
                        continue;
                    }

                    if (TranslationQueue.Count > 0 && AutoTranslatorAPI.HasAnyReadyConfig())
                    {
                        int batchGeneration;
                        TargetLanguage batchTargetLanguage;
                        List<string> batch = new List<string>();
                        lock (_cachePersistenceLock)
                        {
                            batchGeneration = System.Threading.Volatile.Read(ref _uiLanguageGeneration);
                            batchTargetLanguage = AutoTranslatorMod.Settings.TargetLang;
                            while (!AutoTranslatorSettings.IsCancellationRequested &&
                                   batch.Count < 20 &&
                                   TranslationQueue.TryDequeue(out string text))
                            {
                                System.Threading.Interlocked.Decrement(ref _queuedApproxCount);
                                batch.Add(text);
                            }
                            if (batch.Count > 0)
                                System.Threading.Interlocked.Increment(ref _activeUiTranslationBatchCount);
                        }
                        if (batch.Count > 0)
                        {
                            try
                            {
                                var translatedBatch = await AutoTranslatorAPI.TranslateBatchAsync(batch);
                                if (translatedBatch != null && translatedBatch.Count == batch.Count)
                                {
                                    lock (_cachePersistenceLock)
                                    {
                                        if (!IsUILanguageContextCurrent(batchGeneration, batchTargetLanguage))
                                        {
                                            continue;
                                        }

                                        bool hasNewCache = false;
                                        for (int i = 0; i < batch.Count; i++)
                                        {
                                            string original = batch[i];
                                            string originalCacheKey = BuildCacheKey(batchTargetLanguage, original);
                                            string translated = SanitizeUITranslationResult(original, translatedBatch[i]);
                                            if (!string.IsNullOrEmpty(translated) && original != translated)
                                            {
                                                Cache[originalCacheKey] = translated;
                                                hasNewCache = true;
                                            }
                                            else
                                            {
                                                RememberIgnored(original);
                                            }
                                        }
                                        if (hasNewCache) ClearRenderDecisionCache();
                                        if (hasNewCache) MarkCacheDirty();
                                        // Persist on the normal timer (and on shutdown)
                                        // instead of parsing and rewriting the complete
                                        // UI cache after every translation batch.
                                        SaveCacheIfDue();
                                    }
                                }
                            }
                            finally
                            {
                                foreach (string original in batch)
                                {
                                    PendingTranslations.TryRemove(
                                        BuildCacheKey(batchTargetLanguage, original),
                                        out _);
                                }
                                System.Threading.Interlocked.Decrement(ref _activeUiTranslationBatchCount);
                            }
                        }
                    }
                    SaveCacheIfDue();
                }
                catch (TaskCanceledException)
                {

                    break;
                }
                catch (Exception ex)
                {

                    Log.Warning($"[AutoTranslationCore] BackgroundTranslationWorker error: {ex.Message}");

                    try { await Task.Delay(5000, token); } catch (TaskCanceledException) { break; }
                }
            }
        }

        // 這個方法負責處理 DiscardQueuedTranslations 相關流程。
        // EN: This method handles discard queued translations.
        private static void DiscardQueuedTranslations()
        {
            while (TranslationQueue.TryDequeue(out string text))
            {
                System.Threading.Interlocked.Decrement(ref _queuedApproxCount);
                PendingTranslations.TryRemove(BuildCacheKey(text), out _);
            }

            if (System.Threading.Volatile.Read(ref _queuedApproxCount) < 0)
            {
                System.Threading.Interlocked.Exchange(ref _queuedApproxCount, 0);
            }
        }

        private static void DiscardQueuedClassifications()
        {
            while (ClassificationQueue.TryDequeue(out string text))
            {
                System.Threading.Interlocked.Decrement(ref _classificationApproxCount);
                PendingClassifications.TryRemove(BuildCacheKey(text), out _);
            }

            PendingClassifications.Clear();

            if (System.Threading.Volatile.Read(ref _classificationApproxCount) < 0)
            {
                System.Threading.Interlocked.Exchange(ref _classificationApproxCount, 0);
            }
            else if (ClassificationQueue.IsEmpty)
            {
                System.Threading.Interlocked.Exchange(ref _classificationApproxCount, 0);
            }
        }

        /// <summary>
        /// Reports upstream UI interception work that has not yet entered the
        /// shared API request lifecycle.  Once a batch reaches the API, the
        /// request activity tracker becomes the authoritative counter instead.
        /// </summary>
        public static bool HasOutstandingTranslationWork
        {
            get
            {
                return System.Threading.Volatile.Read(ref _activeUiTranslationBatchCount) > 0 ||
                       GetQueueCount() > 0 ||
                       GetPendingCount() > 0;
            }
        }

        /// <summary>
        /// Applies the shared emergency-stop boundary to both legacy UI queues.
        /// An already completed network response may still be accepted by its
        /// worker, but no queued or newly intercepted text may start a new batch.
        /// </summary>
        public static void CancelPendingTranslationWork()
        {
            lock (_cachePersistenceLock)
            {
                bool hadPendingClassifications =
                    System.Threading.Volatile.Read(ref _classificationApproxCount) > 0 ||
                    !PendingClassifications.IsEmpty;
                DiscardQueuedClassifications();
                DiscardQueuedTranslations();
                PendingClassifications.Clear();
                PendingTranslations.Clear();
                if (hadPendingClassifications) ClearRenderDecisionCache();
                SaveCacheIfDue();
            }
        }

        public static async Task WaitForPendingTranslationWorkDrainAsync()
        {
            while (System.Threading.Volatile.Read(ref _activeUiTranslationBatchCount) > 0)
            {
                await Task.Delay(25);
            }
        }
    }
}
