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
// 這個檔案負責 UI 快取清理與熱重載。
// EN: This file manages UI translation cache clearing and hot reload.

namespace AutoTranslator_Core
{
    // 這個類別負責 UIInterceptor 的主要流程與狀態。
    // EN: This class manages the main workflow and state for UIInterceptor.
    public static partial class UIInterceptor
    {
        // 這個方法負責讀取 快取 資料。
        // EN: This method loads cache.
        private static void LoadCache()
        {
            if (!EnsureCacheFileReadable(CacheFilePath)) return;

            try
            {
                if (File.Exists(CacheFilePath))
                {
                    string json = File.ReadAllText(CacheFilePath);
                    var loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    if (loaded != null)
                    {
                        bool droppedUnsafeEntries = false;
                        foreach (var kvp in loaded)
                        {
                            if (!TryGetOriginalTextFromCacheKey(kvp.Key, out string original, out TargetLanguage? keyLanguage))
                            {
                                droppedUnsafeEntries = true;
                                continue;
                            }

                            if (keyLanguage.HasValue && keyLanguage.Value != AutoTranslatorMod.Settings.TargetLang)
                            {
                                continue;
                            }

                            // A small set of legacy keys contains both old
                            // placeholders and formerly unrecognized units
                            // before them, for example "(5MB) {num1}". Their
                            // slot order cannot be migrated safely without the
                            // original source/value pair, so let those labels
                            // rebuild once instead of caching a wrong mapping.
                            if (UIDynamicNumberTemplate.HasMixedPersistedTemplate(original))
                            {
                                droppedUnsafeEntries = true;
                                continue;
                            }

                            string cleanOriginal = GetTranslationLookupText(original);
                            string cleanStoredValue = GetTranslationLookupText(kvp.Value);
                            if (!ShouldLoadCachedText(original) || ShouldSkipUITranslationText(cleanStoredValue))
                            {
                                if (!string.IsNullOrWhiteSpace(cleanOriginal)) RememberIgnored(cleanOriginal);
                                droppedUnsafeEntries = true;
                                continue;
                            }

                            string cleanTranslated = SanitizeUITranslationResult(cleanOriginal, cleanStoredValue);
                            if (string.IsNullOrWhiteSpace(cleanTranslated) || string.Equals(cleanOriginal, cleanTranslated, StringComparison.Ordinal))
                            {
                                RememberIgnored(cleanOriginal);
                                droppedUnsafeEntries = true;
                                continue;
                            }

                            string cleanKey = BuildCacheKey(cleanOriginal);
                            Cache[cleanKey] = cleanTranslated;
                            if (!string.Equals(cleanKey, kvp.Key, StringComparison.Ordinal) ||
                                !string.Equals(cleanTranslated, kvp.Value, StringComparison.Ordinal))
                            {
                                droppedUnsafeEntries = true;
                            }
                        }
                        if (droppedUnsafeEntries) MarkCacheDirty();
                    }
                    Log.Message("[AutoTranslationCore] 📦 " + "ATC_Log_UICacheLoaded".Translate(Cache.Count));
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[AutoTranslationCore] ⚠️ " + "ATC_LogError_UICacheLoadFailed".Translate(ex.Message));
            }
        }

        // 這個方法負責讀取 Ignored快取 資料。
        // EN: This method loads ignored cache.
        private static void LoadIgnoredCache()
        {
            try
            {
                if (!File.Exists(IgnoredCacheFilePath)) return;

                string json = File.ReadAllText(IgnoredCacheFilePath);
                var loaded = JsonConvert.DeserializeObject<List<string>>(json);
                if (loaded == null) return;

                foreach (string key in loaded)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (IgnoredCache.Count >= MaxIgnoredCacheSize) break;

                    if (TryGetOriginalTextFromCacheKey(key, out string original, out TargetLanguage? keyLanguage) &&
                        (!keyLanguage.HasValue || keyLanguage.Value == AutoTranslatorMod.Settings.TargetLang))
                    {
                        string cleanOriginal = GetTranslationLookupText(original);
                        if (!string.IsNullOrWhiteSpace(cleanOriginal))
                        {
                            IgnoredCache[BuildCacheKey(cleanOriginal)] = true;
                            if (!keyLanguage.HasValue) MarkIgnoredCacheDirty();
                        }
                    }
                }

                Log.Message("[AutoTranslationCore] UI ignored cache loaded: " + IgnoredCache.Count);
            }
            catch (Exception ex)
            {
                QuarantineBrokenCacheFile(IgnoredCacheFilePath);
                Log.Warning("[AutoTranslationCore] UI ignored cache load failed: " + ex.Message);
            }
        }


        // 這個方法負責保存 快取 資料。
        // EN: This method saves cache.
        public static void SaveCache()
        {
            lock (_cachePersistenceLock)
            {
                try
                {
                    int saveVersion = System.Threading.Volatile.Read(ref _cacheChangeVersion);
                    var dictToSave = LoadExistingCacheFileForMerge(CacheFilePath, false);
                    RemoveCurrentLanguageEntries(dictToSave);
                    foreach (var kvp in Cache)
                    {
                        dictToSave[kvp.Key] = kvp.Value;
                    }

                    string json = JsonConvert.SerializeObject(dictToSave, Formatting.Indented);
                    WriteAllTextAtomic(CacheFilePath, json);
                    _cacheDirty = false;
                    if (System.Threading.Volatile.Read(ref _cacheChangeVersion) != saveVersion) _cacheDirty = true;
                    System.Threading.Interlocked.Exchange(ref _lastCacheSaveTicks, DateTime.UtcNow.Ticks);
                }
                catch (Exception ex)
                {
                    Log.Warning("[AutoTranslationCore] ⚠️ " + "ATC_LogError_UICacheSaveFailed".Translate(ex.Message));
                }
            }
        }

        // 這個方法負責保存 Ignored快取 資料。
        // EN: This method saves ignored cache.
        private static void SaveIgnoredCache()
        {
            lock (_cachePersistenceLock)
            {
                try
                {
                    int saveVersion = System.Threading.Volatile.Read(ref _ignoredCacheChangeVersion);
                    var merged = LoadExistingIgnoredCacheFileForMerge(IgnoredCacheFilePath);
                    RemoveCurrentLanguageEntries(merged);
                    foreach (string key in IgnoredCache.Keys)
                    {
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            merged[key] = true;
                        }
                    }

                    var keys = merged.Keys
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .OrderBy(k => k, StringComparer.Ordinal)
                        .Take(MaxIgnoredCacheSize)
                        .ToList();

                    string json = JsonConvert.SerializeObject(keys, Formatting.Indented);
                    WriteAllTextAtomic(IgnoredCacheFilePath, json);
                    _ignoredCacheDirty = false;
                    if (System.Threading.Volatile.Read(ref _ignoredCacheChangeVersion) != saveVersion) _ignoredCacheDirty = true;
                    System.Threading.Interlocked.Exchange(ref _lastCacheSaveTicks, DateTime.UtcNow.Ticks);
                }
                catch (Exception ex)
                {
                    Log.Warning("[AutoTranslationCore] UI ignored cache save failed: " + ex.Message);
                }
            }
        }

        // 這個方法負責讀取 Existing快取FileForMerge 資料。
        // EN: This method loads existing cache file for merge.
        private static Dictionary<string, string> LoadExistingCacheFileForMerge(string path, bool quarantineOnError)
        {
            try
            {
                if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.Ordinal);

                var loaded = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path));
                return loaded != null
                    ? new Dictionary<string, string>(loaded, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                if (quarantineOnError) QuarantineBrokenCacheFile(path);
                Log.Warning("[AutoTranslationCore] UI cache merge read failed: " + ex.Message);
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        // 這個方法負責讀取 ExistingIgnored快取FileForMerge 資料。
        // EN: This method loads existing ignored cache file for merge.
        private static Dictionary<string, bool> LoadExistingIgnoredCacheFileForMerge(string path)
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);

            try
            {
                if (!File.Exists(path)) return result;

                var loaded = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path));
                if (loaded == null) return result;

                foreach (string key in loaded)
                {
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        result[key] = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[AutoTranslationCore] UI ignored cache merge read failed: " + ex.Message);
            }

            return result;
        }

        // 這個欄位保存 RemoveCurrent語言Entries 的執行狀態或快取資料。
        // EN: This field stores remove current language entries runtime state or cached data.
        private static void RemoveCurrentLanguageEntries<T>(IDictionary<string, T> dict)
        {
            string prefix = AutoTranslatorMod.Settings.TargetLang + "|";
            var keysToRemove = dict.Keys
                .Where(k => string.IsNullOrEmpty(k) || k.StartsWith(prefix, StringComparison.Ordinal) || !HasLanguagePrefix(k))
                .ToList();

            foreach (string key in keysToRemove)
            {
                dict.Remove(key);
            }
        }

        // 這個方法負責判斷 Has語言Prefix 條件是否成立。
        // EN: This method checks has language prefix.
        private static bool HasLanguagePrefix(string cacheKey)
        {
            if (string.IsNullOrEmpty(cacheKey)) return false;

            foreach (TargetLanguage lang in Enum.GetValues(typeof(TargetLanguage)))
            {
                if (cacheKey.StartsWith(lang + "|", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // 這個方法負責保存 AllTextAtomic 資料。
        // EN: This method saves all text atomic.
        private static void WriteAllTextAtomic(string path, string text)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, text);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tempPath, path);
        }

        // 這個方法負責確保 快取FileReadable 已準備完成。
        // EN: This method ensures cache file readable is ready.
        private static bool EnsureCacheFileReadable(string path)
        {
            if (!File.Exists(path)) return false;

            try
            {
                string json = File.ReadAllText(path);
                JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                return true;
            }
            catch (Exception ex)
            {
                QuarantineBrokenCacheFile(path);
                Log.Warning("[AutoTranslationCore] UI cache file was unreadable: " + ex.Message);
                return false;
            }
        }

        // 這個方法負責處理 QuarantineBroken快取File 相關流程。
        // EN: This method handles quarantine broken cache file.
        private static void QuarantineBrokenCacheFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;

                string backupPath = path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                File.Move(path, backupPath);
                Log.Warning("[AutoTranslationCore] UI cache was unreadable and has been backed up: " + backupPath);
            }
            catch (Exception moveEx)
            {
                Log.Warning("[AutoTranslationCore] UI cache quarantine failed: " + moveEx.Message);
            }
        }

        // 這個方法負責處理 Flush快取 相關流程。
        // EN: This method handles flush cache.
        public static void FlushCache()
        {
            lock (_cachePersistenceLock)
            {
                if (_cacheDirty)
                {
                    SaveCache();
                }

                if (_ignoredCacheDirty)
                {
                    SaveIgnoredCache();
                }

                if (_sourceOwnersDirty)
                {
                    SaveSourceOwners();
                }
            }
        }


        // 這個方法負責取得 佇列Count 資料。
        // EN: This method gets queue count.
        public static int GetQueueCount()
        {
            return Math.Max(0, System.Threading.Volatile.Read(ref _queuedApproxCount))
                + Math.Max(0, System.Threading.Volatile.Read(ref _classificationApproxCount));
        }
        // 這個方法負責取得 PendingCount 資料。
        // EN: This method gets pending count.
        public static int GetPendingCount() { return PendingTranslations.Count + PendingClassifications.Count; }
        // 這個方法負責取得 IgnoredCount 資料。
        // EN: This method gets ignored count.
        public static int GetIgnoredCount() { return IgnoredCache.Count; }

        // 這個方法負責建立 快取Key 所需資料。
        // EN: This method builds cache key.
        public static string BuildCacheKey(string text)
        {
            return BuildCacheKey(AutoTranslatorMod.Settings.TargetLang, text);
        }

        private static string BuildCacheKey(TargetLanguage targetLanguage, string text)
        {
            return $"{targetLanguage}|{text}";
        }

        // 這個方法負責嘗試執行 GetOriginalTextFrom快取Key 並回報是否成功。
        // EN: This method tries to get original text from cache key and reports whether it succeeded.
        private static bool TryGetOriginalTextFromCacheKey(string cacheKey, out string original)
        {
            return TryGetOriginalTextFromCacheKey(cacheKey, out original, out _);
        }

        // 這個方法負責嘗試執行 GetOriginalTextFrom快取Key 並回報是否成功。
        // EN: This method tries to get original text from cache key and reports whether it succeeded.
        private static bool TryGetOriginalTextFromCacheKey(string cacheKey, out string original, out TargetLanguage? keyLanguage)
        {
            original = cacheKey;
            keyLanguage = null;
            if (string.IsNullOrEmpty(cacheKey)) return false;

            foreach (TargetLanguage lang in Enum.GetValues(typeof(TargetLanguage)))
            {
                string prefix = lang + "|";
                if (cacheKey.StartsWith(prefix, StringComparison.Ordinal))
                {
                    original = cacheKey.Substring(prefix.Length);
                    keyLanguage = lang;
                    return true;
                }
            }

            return true;
        }

        // 這個方法負責判斷 IsIgnored 條件是否成立。
        // EN: This method checks is ignored.
        public static bool IsIgnored(string text)
        {
            return IgnoredCache.ContainsKey(BuildCacheKey(GetTranslationLookupText(text)));
        }

        public static void PrepareForLanguageChange()
        {
            lock (_cachePersistenceLock)
            {
                System.Threading.Interlocked.Increment(ref _uiLanguageGeneration);
                FlushCache();
                DiscardQueuedClassifications();
                DiscardQueuedTranslations();
                PendingClassifications.Clear();
                PendingTranslations.Clear();
            }
        }

        private static bool IsUILanguageContextCurrent(int generation, TargetLanguage targetLanguage)
        {
            return System.Threading.Volatile.Read(ref _uiLanguageGeneration) == generation
                && AutoTranslatorMod.Settings.TargetLang == targetLanguage;
        }

        // 這個方法負責處理 ReloadFor語言Change 相關流程。
        // EN: This method handles reload for language change.
        public static void ReloadForLanguageChange()
        {
            lock (_cachePersistenceLock)
            {
                System.Threading.Interlocked.Increment(ref _uiLanguageGeneration);
                Cache.Clear();
                IgnoredCache.Clear();
                SourceOwners.Clear();
                FastBypassDecisionCache.Clear();
                TextDecisionCache.Clear();
                ClearRenderDecisionCache();
                Patch_GUI_Label_GUIContent.ClearCache();
                DiscardQueuedClassifications();
                DiscardQueuedTranslations();
                PendingClassifications.Clear();
                PendingTranslations.Clear();
                System.Threading.Interlocked.Exchange(ref _classificationApproxCount, 0);
                System.Threading.Interlocked.Exchange(ref _queuedApproxCount, 0);
                _cacheDirty = false;
                _ignoredCacheDirty = false;
                _sourceOwnersDirty = false;

                LoadCache();
                LoadIgnoredCache();
                LoadSourceOwners();
            }
        }

        // 這個方法負責嘗試執行 GetCached翻譯 並回報是否成功。
        // EN: This method tries to get cached translation and reports whether it succeeded.
        public static bool TryGetCachedTranslation(string text, out string translated)
        {
            translated = null;
            if (!ShouldInterceptText(text))
            {
                return false;
            }

            return TryGetCachedTranslationKnownSafe(text, out translated);
        }

        public static bool TryGetCachedTranslationKnownSafe(string text, out string translated)
        {
            translated = null;

            string lookupText = GetTranslationLookupText(text);
            string cacheKey = BuildCacheKey(lookupText);
            if (Cache.TryGetValue(cacheKey, out translated))
            {
                if (TryNormalizeCachedTranslation(cacheKey, lookupText, translated, out translated))
                {
                    translated = RestoreTranslationDisplayText(text, translated);
                    return true;
                }

                return false;
            }

            if (Cache.TryGetValue(lookupText, out translated)
                && IsCachedTranslationCompatibleWithCurrentLanguage(translated))
            {
                if (TryNormalizeCachedTranslation(lookupText, lookupText, translated, out translated))
                {
                    Cache[cacheKey] = translated;
                    MarkCacheDirty();
                    ClearRenderDecisionCache();
                    translated = RestoreTranslationDisplayText(text, translated);
                    return true;
                }
            }

            return false;
        }

        internal static bool TryResolveRenderText(string original, out string translated)
        {
            return TryResolveRenderTextCore(original, false, out translated);
        }

        // Callers that already performed the bypass check can use this path to
        // avoid paying for the same dictionary lookup twice per GUI draw.
        internal static bool TryResolveRenderTextKnownNotBypassed(string original, out string translated)
        {
            return TryResolveRenderTextCore(original, true, out translated);
        }

        private static bool TryResolveRenderTextCore(string original, bool bypassAlreadyChecked, out string translated)
        {
            translated = null;
            EnsureRenderDecisionSettingsCurrent();
            string renderKey = BuildRenderDecisionKey(original);

            // A render decision is the hot-path cache. Check it before the
            // validated translation cache so stable labels do not repeatedly
            // run regex, placeholder, and language-safety validation every
            // frame. Cache writes invalidate this snapshot via
            // ClearRenderDecisionCache().
            if (RenderDecisionCache.TryGetValue(renderKey, out UIRenderDecision decision)
                && decision.Version == _renderDecisionVersion)
            {
                switch (decision.Kind)
                {
                    case UIRenderDecisionKind.Translated:
                        translated = decision.TranslatedText;
                        return !string.IsNullOrEmpty(translated);
                    case UIRenderDecisionKind.PassThrough:
                    case UIRenderDecisionKind.Classifying:
                        return false;
                    case UIRenderDecisionKind.Pending:
                        if (DateTime.UtcNow.Ticks < decision.RetryAfterTicks) return false;
                        break;
                }
            }

            if (!bypassAlreadyChecked && ShouldBypassUIPatchText(original))
            {
                RememberRenderDecision(renderKey, UIRenderDecisionKind.PassThrough, null, 0L);
                return false;
            }

            CaptureSourceOwnerIfNeeded(original);
            if (IsIgnored(original) || IsSourceOwnerBlacklisted(original))
            {
                RememberRenderDecision(renderKey, UIRenderDecisionKind.PassThrough, null, 0L);
                return false;
            }

            if (TryGetCachedTranslationKnownSafe(original, out translated))
            {
                if (!HasRenderableUICharacters(translated))
                {
                    translated = null;
                    return false;
                }
                RememberRenderDecision(renderKey, UIRenderDecisionKind.Translated, translated, 0L);
                return true;
            }

            if (QueueForClassification(original))
            {
                RememberRenderDecision(renderKey, UIRenderDecisionKind.Classifying, null, 0L);
            }
            else
            {
                RememberRenderDecision(renderKey, UIRenderDecisionKind.Pending, null, DateTime.UtcNow.Ticks + RenderPendingRetryInterval.Ticks);
            }
            return false;
        }

        internal static string BuildRenderDecisionKey(string text)
        {
            return text ?? string.Empty;
        }

        internal static void RememberRenderDecision(string renderKey, UIRenderDecisionKind kind, string translated, long retryAfterTicks)
        {
            if (string.IsNullOrEmpty(renderKey)) return;
            if (RenderDecisionCache.Count >= MaxRenderDecisionCacheSize) RenderDecisionCache.Clear();

            RenderDecisionCache[renderKey] = new UIRenderDecision
            {
                Kind = kind,
                TranslatedText = translated,
                Version = _renderDecisionVersion,
                RetryAfterTicks = retryAfterTicks
            };
        }

        private static void EnsureRenderDecisionSettingsCurrent()
        {
            TargetLanguage targetLang = AutoTranslatorMod.Settings.TargetLang;
            bool errorLogInterception = AutoTranslatorMod.Settings.EnableUIErrorLogInterception;
            bool newTranslation = AutoTranslatorMod.Settings.EnableUINewTranslation;

            if (_renderDecisionSettingsInitialized
                && _renderDecisionTargetLang == targetLang
                && _renderDecisionErrorLogInterception == errorLogInterception
                && _renderDecisionNewTranslation == newTranslation)
            {
                return;
            }

            lock (_renderDecisionSettingsLock)
            {
                if (_renderDecisionSettingsInitialized
                    && _renderDecisionTargetLang == targetLang
                    && _renderDecisionErrorLogInterception == errorLogInterception
                    && _renderDecisionNewTranslation == newTranslation)
                {
                    return;
                }

                _renderDecisionTargetLang = targetLang;
                _renderDecisionErrorLogInterception = errorLogInterception;
                _renderDecisionNewTranslation = newTranslation;
                _renderDecisionSettingsInitialized = true;
                ClearRenderDecisionCache();
            }
        }

        private static void ClearRenderDecisionCache()
        {
            RenderDecisionCache.Clear();
            System.Threading.Interlocked.Increment(ref _renderDecisionVersion);
        }

        // 這個方法負責嘗試執行 NormalizeCached翻譯 並回報是否成功。
        // EN: This method tries to normalize cached translation and reports whether it succeeded.
        private static bool TryNormalizeCachedTranslation(string cacheKey, string original, string translated, out string normalized)
        {
            normalized = SanitizeUITranslationResult(original, translated);
            if (string.IsNullOrWhiteSpace(normalized) || string.Equals(original, normalized, StringComparison.Ordinal))
            {
                Cache.TryRemove(cacheKey, out _);
                RememberIgnored(original);
                MarkCacheDirty();
                ClearRenderDecisionCache();
                return false;
            }

            normalized = LanguageDetector.NormalizeChineseVariant(normalized, AutoTranslatorMod.Settings.TargetLang);
            if (ShouldSkipUITranslationText(normalized))
            {
                Cache.TryRemove(cacheKey, out _);
                RememberIgnored(original);
                MarkCacheDirty();
                ClearRenderDecisionCache();
                return false;
            }

            if (!string.Equals(normalized, translated, StringComparison.Ordinal))
            {
                Cache[cacheKey] = normalized;
                MarkCacheDirty();
                ClearRenderDecisionCache();
            }
            return true;
        }

        // 這個方法負責判斷 IsCached翻譯CompatibleWithCurrent語言 條件是否成立。
        // EN: This method checks is cached translation compatible with current language.
        private static bool IsCachedTranslationCompatibleWithCurrentLanguage(string translated)
        {
            if (string.IsNullOrWhiteSpace(translated)) return false;

            if (AutoTranslatorMod.Settings.TargetLang == TargetLanguage.Traditional)
            {
                return !LanguageDetector.LooksLikeSimplified(translated);
            }

            if (AutoTranslatorMod.Settings.TargetLang == TargetLanguage.Simplified)
            {
                return !LanguageDetector.LooksLikeTraditional(translated);
            }

            return true;
        }


        // 這個方法負責處理 RememberIgnored 相關流程。
        // EN: This method handles remember ignored.
        private static void RememberIgnored(string text)
        {
            if (IgnoredCache.Count < MaxIgnoredCacheSize)
            {
                string cacheKey = BuildCacheKey(GetTranslationLookupText(text));
                if (IgnoredCache.TryAdd(cacheKey, true))
                {
                    MarkIgnoredCacheDirty();
                }
            }
        }

        private static void MarkCacheDirty()
        {
            System.Threading.Interlocked.Increment(ref _cacheChangeVersion);
            _cacheDirty = true;
        }

        private static void MarkIgnoredCacheDirty()
        {
            System.Threading.Interlocked.Increment(ref _ignoredCacheChangeVersion);
            _ignoredCacheDirty = true;
        }


        // 這個方法負責保存 快取IfDue 資料。
        // EN: This method saves cache if due.
        private static void SaveCacheIfDue(bool force = false)
        {
            lock (_cachePersistenceLock)
            {
                if (!_cacheDirty && !_ignoredCacheDirty && !_sourceOwnersDirty) return;

                long last = System.Threading.Interlocked.Read(ref _lastCacheSaveTicks);
                if (!force && DateTime.UtcNow.Ticks - last < TimeSpan.FromSeconds(10).Ticks) return;

                if (_cacheDirty) SaveCache();
                if (_ignoredCacheDirty) SaveIgnoredCache();
                if (_sourceOwnersDirty) SaveSourceOwners();
            }
        }


        // 這個方法負責清除 UICache 資料。
        // EN: This method clears UI cache.
        public static void ClearUICache()
        {
            lock (_cachePersistenceLock)
            {
                System.Threading.Interlocked.Increment(ref _uiLanguageGeneration);
                Cache.Clear();
                IgnoredCache.Clear();
                SourceOwners.Clear();
                FastBypassDecisionCache.Clear();
                TextDecisionCache.Clear();
                ClearRenderDecisionCache();
                Patch_GUI_Label_GUIContent.ClearCache();
                DiscardQueuedClassifications();
                PendingClassifications.Clear();
                while (TranslationQueue.TryDequeue(out _)) { }
                PendingTranslations.Clear();
                System.Threading.Interlocked.Exchange(ref _queuedApproxCount, 0);
                _cacheDirty = false;
                _ignoredCacheDirty = false;
                _sourceOwnersDirty = false;

                try
                {
                    if (File.Exists(CacheFilePath))
                    {
                        File.Delete(CacheFilePath);
                    }

                    if (File.Exists(IgnoredCacheFilePath))
                    {
                        File.Delete(IgnoredCacheFilePath);
                    }

                    if (File.Exists(SourceOwnersFilePath))
                    {
                        File.Delete(SourceOwnersFilePath);
                    }
                }
                catch (Exception ex)
                {
                    Verse.Log.Warning($"[AutoTranslationCore] " + "ATC_LogError_DeleteUICacheFailed".Translate(ex.Message));
                }
            }

            Verse.Log.Message("[AutoTranslationCore] 🔄 " + "ATC_Log_UICacheClearedFull".Translate());
        }


        // 這個方法負責處理 Refresh執行期UICache 相關流程。
        // EN: This method handles refresh runtime UI cache.
        public static void RefreshRuntimeUICache()
        {
            ClearRenderDecisionCache();
            Patch_GUI_Label_GUIContent.ClearCache();
            DiscardQueuedClassifications();
            DiscardQueuedTranslations();
            SaveCacheIfDue(true);

            Verse.Log.Message("[AutoTranslationCore] UI render cache refreshed without deleting saved translations.");
        }

        // 這個方法負責送出 HotReload 請求。
        // EN: This method requests hot reload.
        public static void RequestHotReload()
        {
            try
            {

                RefreshRuntimeUICache();


                AutoTranslatorScanner.RequestMemoryDrop();


                Messages.Message("ATC_Message_HotReloadSuccess".CanTranslate()
                    ? "ATC_Message_HotReloadSuccess".Translate().ToString()
                    : "🪂 [記憶體空投] 翻譯已即時注入，UI 視窗快取已完全刷新！",
                    MessageTypeDefOf.PositiveEvent, false);
            }
            catch (Exception ex)
            {
                Log.Error($"[AutoTranslationCore] Hot reload failed: {ex.Message}");
            }
        }

    }
}
