using HarmonyLib;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責 UI 文字攔截與快取控制。
// EN: This file coordinates UI text interception and cache state.

namespace AutoTranslator_Core
{
    [StaticConstructorOnStartup]
    // 這個類別負責 UIInterceptor 的主要流程與狀態。
    // EN: This class manages the main workflow and state for UIInterceptor.
    public static partial class UIInterceptor
    {
        public static ConcurrentDictionary<string, string> Cache = new ConcurrentDictionary<string, string>();
        public static ConcurrentDictionary<string, bool> IgnoredCache = new ConcurrentDictionary<string, bool>();
        private static ConcurrentQueue<string> ClassificationQueue = new ConcurrentQueue<string>();
        private static ConcurrentDictionary<string, bool> PendingClassifications = new ConcurrentDictionary<string, bool>();
        private static ConcurrentQueue<string> TranslationQueue = new ConcurrentQueue<string>();
        private static ConcurrentDictionary<string, bool> PendingTranslations = new ConcurrentDictionary<string, bool>();
        // 這個欄位保存 快取File路徑 的執行狀態或快取資料。
        // EN: This field stores cache file path runtime state or cached data.
        private static readonly string CacheFilePath;
        // 這個欄位保存 Ignored快取File路徑 的執行狀態或快取資料。
        // EN: This field stores ignored cache file path runtime state or cached data.
        private static readonly string IgnoredCacheFilePath;
        // 這個常數定義 MaxQueuedTranslations 的固定值。
        // EN: This constant defines the fixed value for max queued translations.
        private const int MaxQueuedTranslations = 500;
        private const int MaxQueuedClassifications = 2000;
        // 這個常數定義 MaxNew佇列ItemsPerFrame 的固定值。
        // EN: This constant defines the fixed value for max new queue items per frame.
        private const int MaxNewQueueItemsPerFrame = 6;
        private const int MaxNewClassificationItemsPerFrame = 24;
        private const int MaxNewQueueItemsPerScanWindow = 24;
        private const int MaxNewClassificationItemsPerScanWindow = 120;
        private static readonly TimeSpan NewTranslationScanInterval = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan NewClassificationScanInterval = TimeSpan.FromMilliseconds(500);
        // 這個常數定義 MaxIgnored快取Size 的固定值。
        // EN: This constant defines the fixed value for max ignored cache size.
        private const int MaxIgnoredCacheSize = 20000;
        private const int MaxTextDecisionCacheSize = 20000;
        private const int MaxRenderDecisionCacheSize = 40000;
        private static readonly TimeSpan RenderPendingRetryInterval = TimeSpan.FromSeconds(1);
        // 這個欄位保存 queuedApproxCount 的執行狀態或快取資料。
        // EN: This field stores queued approx count runtime state or cached data.
        private static int _queuedApproxCount = 0;
        private static int _classificationApproxCount = 0;
        // 這個欄位保存 last佇列Frame 的執行狀態或快取資料。
        // EN: This field stores last queue frame runtime state or cached data.
        private static int _lastQueueFrame = -1;
        private static int _lastClassificationFrame = -1;
        // 這個欄位保存 queuedThisFrame 的執行狀態或快取資料。
        // EN: This field stores queued this frame runtime state or cached data.
        private static int _queuedThisFrame = 0;
        private static int _classifiedThisFrame = 0;
        private static readonly object _frameBudgetLock = new object();
        private static readonly object _classificationFrameBudgetLock = new object();
        private static long _nextNewTranslationScanTicks = 0L;
        private static int _queuedThisScanWindow = 0;
        private static readonly object _newTranslationScanLock = new object();
        private static long _nextNewClassificationScanTicks = 0L;
        private static int _classifiedThisScanWindow = 0;
        private static readonly object _newClassificationScanLock = new object();
        // 這個欄位保存 cacheDirty 的執行狀態或快取資料。
        // EN: This field stores cache dirty runtime state or cached data.
        private static volatile bool _cacheDirty = false;
        private static int _cacheChangeVersion = 0;
        // 這個欄位保存 ignored快取Dirty 的執行狀態或快取資料。
        // EN: This field stores ignored cache dirty runtime state or cached data.
        private static volatile bool _ignoredCacheDirty = false;
        private static int _ignoredCacheChangeVersion = 0;
        private static readonly object _cachePersistenceLock = new object();
        private static int _uiLanguageGeneration = 0;
        // 這個欄位保存 last快取SaveTicks 的執行狀態或快取資料。
        // EN: This field stores last cache save ticks runtime state or cached data.
        private static long _lastCacheSaveTicks = 0L;

        private static readonly Regex LetterRegex = new Regex(@"\p{L}", RegexOptions.Compiled);
        private static readonly Regex EnglishRegex = new Regex(@"[a-zA-Z]", RegexOptions.Compiled);
        private static readonly Regex CyrillicRegex = new Regex(@"\p{IsCyrillic}", RegexOptions.Compiled);
        private static readonly Regex KanaRegex = new Regex(@"\p{IsHiragana}|\p{IsKatakana}", RegexOptions.Compiled);
        private static readonly Regex HangulRegex = new Regex(@"\p{IsHangulSyllables}", RegexOptions.Compiled);
        private static readonly Regex CJKRegex = new Regex(@"\p{IsCJKUnifiedIdeographs}", RegexOptions.Compiled);
        private static readonly Regex DataKeyValueRegex = new Regex(@"^\s*(?:""[^""]{1,64}""|'[^']{1,64}'|text|translation|translated|result|value)\s*[:=]\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex LogTimestampRegex = new Regex(@"^(?<prefix>\s*(?:\(\d+\)\s*)?\[\d{1,2}:\d{2}:\d{2}\]\s*)(?<body>[\s\S]*)$", RegexOptions.Compiled);
        private static readonly Regex VolatileMetricRegex = new Regex(@"^\s*(?:FPS|TPS|帧率|幀率)\s*[:：]\s*\d+(?:\s*[\(（]\d+[\)）])?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TemperatureReadoutRegex = new Regex(@"^\s*(?:(?:Indoor|Indoors|Outdoor|Outdoors|Inside|Outside|Room|室內|室内|室外|戶外|户外|屋內|屋内|外面|内部|外部)\s+)?[-+]?\d+(?:\.\d+)?\s*(?:°\s*)?[CF]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex StackCountRegex = new Regex(@"^\s*[\p{L}\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}\p{IsHangulSyllables}\p{IsCyrillic}\s'\-·・]+[xX×]\s*\d{1,6}\s*$", RegexOptions.Compiled);
        private static readonly Regex InternalKeyRegex = new Regex(@"^\s*[A-Za-z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+(?:Label|Def|Path|Worker|Job|Recipe|Tool|Group|Tab|Menu|Key)?\s*$|^\s*[A-Za-z][A-Za-z0-9]+(?:Label|Def|Path|Worker|Job|Recipe|Tool|Group|Tab|Menu|Key)\s*$", RegexOptions.Compiled);
        private static readonly Regex NumericStatusRegex = new Regex(@"(?:\d+(?:\.\d+)?\s*[%/]|[=/|]\s*\d+(?:\.\d+)?|\d+(?:\.\d+)?\s*(?:h|mm|cm|kg|g|W|kW|XP)\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MusicPlaybackStatusRegex = new Regex(@"^\s*(?:Now\s+playing|Currently\s+playing|Playing)\s*[:：]\s*.+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex LanguageDetectionNumericMarkerRegex = new Regex(@"(?<=\d)\s*(?:x|×)\s*(?=\d|\p{L}|[\s\)\]\},.;:!?]|$)|(?<=\p{L})\s*(?:x|×)\s*(?=\d)|(?<=\d)\s*(?:°\s*)?(?:C|F)\b|(?<=\d)\s*(?:kW|MW|W|XP|MB|GB|kB|kg|mm|cm|ms|L|g|m|s|h|d)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> FastBypassDecisionCache = new ConcurrentDictionary<string, bool>();
        private static readonly ConcurrentDictionary<string, bool> TextDecisionCache = new ConcurrentDictionary<string, bool>();
        private static readonly ConcurrentDictionary<string, UIRenderDecision> RenderDecisionCache = new ConcurrentDictionary<string, UIRenderDecision>();
        private static readonly object _renderDecisionSettingsLock = new object();
        private static int _renderDecisionVersion = 0;
        private static bool _renderDecisionSettingsInitialized = false;
        private static TargetLanguage _renderDecisionTargetLang;
        private static bool _renderDecisionErrorLogInterception;
        private static bool _renderDecisionNewTranslation;

        internal enum UIRenderDecisionKind
        {
            PassThrough,
            Classifying,
            Pending,
            Translated
        }

        internal struct UIRenderDecision
        {
            public UIRenderDecisionKind Kind;
            public string TranslatedText;
            public int Version;
            public long RetryAfterTicks;
        }

        static UIInterceptor()
        {
            CacheFilePath = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "UI_Hardcoded_Cache.json");
            IgnoredCacheFilePath = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "UI_Hardcoded_Ignored.json");
            AutoTranslatorMod.TryAutoSyncLanguageWithGame(resetCaches: false, log: false, writeSettings: true);
            LoadCache();
            LoadIgnoredCache();

            AutoTranslatorScanner.RequestKeyedMemoryDrop();

            Task.Run(() => BackgroundClassificationWorker());
            Task.Run(() => BackgroundTranslationWorker());

            Task.Run(async () =>
            {
                await Task.Delay(3000);
                ATC_Dispatcher.RunOnMainThread(() =>
                {
                    AutoTranslatorMod.TryAutoSyncLanguageWithGame(resetCaches: true, log: false, writeSettings: true);
                });
            });

            Task.Run(async () =>
            {
                await Task.Delay(10000);

                if (AutoTranslatorMod.Settings.AutoTranslateOnUpdate)
                {
                    var updates = ModUpdateDetector.GetUpdatedOrNewModsBlocking();
                    if (updates.Count > 0)
                    {
                        AutoTranslatorSettings.AddLog("ATC_Log_AutoStartUpdateScan".Translate(updates.Count));
                        AutoTranslatorScanner.StartMultiScan(updates);
                    }
                }
            });

            var harmony = new Harmony("MingYang.AutoTranslation.UIInterceptor");
            harmony.PatchAll(typeof(UIInterceptor).Assembly);

            Log.Message("[AutoTranslationCore] " + "ATC_Log_UIInterceptorStarted".Translate());
        }

        private static readonly System.Threading.CancellationTokenSource _workerCts = new System.Threading.CancellationTokenSource();
    }
}
