using HarmonyLib;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案保存模組設定、執行狀態與日誌資料。
// EN: This file stores mod settings, runtime state, and log data.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器設定 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorSettings.
    public class AutoTranslatorSettings : ModSettings
    {
        // 這個欄位保存 目標語言 的執行狀態或快取資料。
        // EN: This field stores target language runtime state or cached data.
        public TargetLanguage TargetLang = TargetLanguage.Traditional;
        // 這個欄位保存 HasManual目標語言 的執行狀態或快取資料。
        // EN: This field stores has manual target language runtime state or cached data.
        public bool HasManualTargetLanguage = false;
        // 這個欄位保存 Only掃描Active模組 的執行狀態或快取資料。
        // EN: This field stores only scan active mods runtime state or cached data.
        public bool OnlyScanActiveMods = true;
        // 這個欄位保存 MaxThreads 的執行狀態或快取資料。
        // EN: This field stores max threads runtime state or cached data.
        public int MaxThreads = 3;
        public bool EnableTranslationPolicyAgent = false;
        public int PolicyAgentMaxCallsPerRun = 20;
        public long PolicyAgentMaxEstimatedTokensPerRun = 200000L;
        public int PolicyAgentMaxCallsPerMod = 20;
        public int PolicyAgentMaxRetriesPerRequest = 1;
        // This configuration is intentionally separate from the translation key pool so
        // policy classification can use a cheaper or otherwise independent model.
        public ApiKeyConfig PolicyAgentApiConfig = new ApiKeyConfig
        {
            Label = "Policy Agent"
        };
        public List<ApiKeyConfig> ApiConfigs = new List<ApiKeyConfig>();

        // 這個欄位保存 CurrentProgress 的執行狀態或快取資料。
        // EN: This field stores current progress runtime state or cached data.
        public float CurrentProgress = 0f;
        // 這個欄位保存 CurrentTask名稱 的執行狀態或快取資料。
        // EN: This field stores current task name runtime state or cached data.
        public string CurrentTaskName = "";
        // 這個欄位保存 SubProgress 的執行狀態或快取資料。
        // EN: This field stores sub progress runtime state or cached data.
        public float SubProgress = 0f;
        // 這個欄位保存 SubTask名稱 的執行狀態或快取資料。
        // EN: This field stores sub task name runtime state or cached data.
        public string SubTaskName = "";
        // 這個欄位保存 Should自動Scroll 的執行狀態或快取資料。
        // EN: This field stores should auto scroll runtime state or cached data.
        public static bool ShouldAutoScroll = true;
        // 這個欄位保存 IsSkipCurrentRequested 的執行狀態或快取資料。
        // EN: This field stores is skip current requested runtime state or cached data.
        public static bool IsSkipCurrentRequested = false;

        // 這個欄位保存 last設定ViewHeight 的執行狀態或快取資料。
        // EN: This field stores last settings view height runtime state or cached data.
        public static float lastSettingsViewHeight = 1000f;
        // 這個欄位保存 ShowFinishPopup 的執行狀態或快取資料。
        // EN: This field stores show finish popup runtime state or cached data.
        public static bool ShowFinishPopup = false;
        // 這個欄位保存 mainScrollPos 的執行狀態或快取資料。
        // EN: This field stores main scroll pos runtime state or cached data.
        public static Vector2 mainScrollPos = Vector2.zero;

        // 這個欄位保存 logScrollPos 的執行狀態或快取資料。
        // EN: This field stores log scroll pos runtime state or cached data.
        public static Vector2 logScrollPos = Vector2.zero;
        public static List<string> RuntimeLogs = new List<string>();

        // 這個欄位保存 errorScrollPos 的執行狀態或快取資料。
        // EN: This field stores error scroll pos runtime state or cached data.
        public static Vector2 errorScrollPos = Vector2.zero;
        public static List<string> ErrorLogs = new List<string>();

        public static readonly object logLock = new object();

        // 這個欄位保存 IsCancellationRequested 的執行狀態或快取資料。
        // EN: This field stores is cancellation requested runtime state or cached data.
        public static bool IsCancellationRequested = false;
        // 這個欄位保存 IsRunning 的執行狀態或快取資料。
        // EN: This field stores is running runtime state or cached data.
        public static bool IsRunning = false;

        // 這個方法負責送出 PipelineCancellation 請求。
        // EN: This method requests pipeline cancellation.
        public static void RequestPipelineCancellation()
        {
            IsCancellationRequested = true;
            IsSkipCurrentRequested = false;
            AutoTranslatorAPI.AbortActiveTranslationRequests("Pipeline cancellation requested");
        }

        // 這個方法負責重置 PipelineCancellation 狀態。
        // EN: This method resets pipeline cancellation.
        public static void ResetPipelineCancellation()
        {
            IsCancellationRequested = false;
            IsSkipCurrentRequested = false;
        }

        // 這個欄位保存 EnableUIInterceptor 的執行狀態或快取資料。
        // EN: This field stores enable UI interceptor runtime state or cached data.
        public bool EnableUIInterceptor = false;
        // 這個欄位保存 EnableUINew翻譯 的執行狀態或快取資料。
        // EN: This field stores enable UI new translation runtime state or cached data.
        public bool EnableUINewTranslation = true;
        // This opt-in prototype only applies explicitly approved targeted patches.
        public bool EnableHardcodedUiPrototype = false;
        // 這個欄位保存 EnableUIErrorLogInterception 的執行狀態或快取資料。
        // EN: This field stores enable UI error log interception runtime state or cached data.
        public bool EnableUIErrorLogInterception = false;
        // 這個欄位保存 ShowOriginalUI 的執行狀態或快取資料。
        // EN: This field stores show original UI runtime state or cached data.
        public bool ShowOriginalUI = false;
        // 這個欄位保存 翻譯工作台模組Names 的執行狀態或快取資料。
        // EN: This field stores translate workbench mod names runtime state or cached data.
        public bool TranslateWorkbenchModNames = false;
        public bool ShowWorldMainButton = true;

        // 這個欄位保存 TotalCharCount 的執行狀態或快取資料。
        // EN: This field stores total char count runtime state or cached data.
        public long TotalCharCount = 0;
        // 這個欄位保存 SessionCharCount 的執行狀態或快取資料。
        // EN: This field stores cloud scroll pos runtime state or cached data.
        [NonSerialized] public long SessionCharCount = 0;


        // 這個欄位保存 Active分頁 的執行狀態或快取資料。
        // EN: This field stores cloud scroll pos runtime state or cached data.
        [NonSerialized] public static int ActiveTab = 0;

        [NonSerialized] public static List<CloudModRecord> CloudRegistry = new List<CloudModRecord>();
        // 這個欄位保存 IsFetching雲端 的執行狀態或快取資料。
        // EN: This field stores cloud scroll pos runtime state or cached data.
        [NonSerialized] public static bool IsFetchingCloud = false;
        // 這個欄位保存 HasFetched雲端ThisSession 的執行狀態或快取資料。
        // EN: This field stores cloud scroll pos runtime state or cached data.
        [NonSerialized] public static bool HasFetchedCloudThisSession = false;
        // 這個欄位保存 雲端上傳目標 的執行狀態或快取資料。
        // EN: This field stores cloud scroll pos runtime state or cached data.
        [NonSerialized] public static string CloudUploadTarget = "";
        // 這個欄位保存 雲端搜尋Text 的執行狀態或快取資料。
        // EN: This field stores cloud scroll pos runtime state or cached data.
        [NonSerialized] public static string CloudSearchText = "";
        [NonSerialized] public static bool CloudShowMineOnly = false;

        // 這個欄位保存 雲端連線Failed 的執行狀態或快取資料。
        // EN: This field stores cloud scroll pos runtime state or cached data.
        [NonSerialized] public static bool CloudConnectionFailed = false;
        // 這個欄位保存 雲端取得Generation 的執行狀態或快取資料。
        // EN: This field stores cloud scroll pos runtime state or cached data.
        [NonSerialized] public static int CloudFetchGeneration = 0;
        // 這個欄位保存 雲端取得StartedUtcTicks 的執行狀態或快取資料。
        // EN: This field stores cloud scroll pos runtime state or cached data.
        [NonSerialized] public static long CloudFetchStartedUtcTicks = 0;
        // 這個欄位保存 cloudScrollPos 的執行狀態或快取資料。
        // EN: This field stores cloud scroll pos runtime state or cached data.
        public static Vector2 cloudScrollPos = Vector2.zero;

        // 這個欄位保存 雲端Nickname 的執行狀態或快取資料。
        // EN: This field stores cloud nickname runtime state or cached data.
        public string CloudNickname = "野生大佬";
        // 這個欄位保存 雲端AdminToken 的執行狀態或快取資料。
        // EN: This field stores cloud admin token runtime state or cached data.
        public string CloudAdminToken = "";
        // 這個欄位保存 雲端上傳Type 的執行狀態或快取資料。
        // EN: This field stores cloud upload type runtime state or cached data.
        public string CloudUploadType = "AI_Auto";
        // 這個欄位保存 雲端Batch上傳Log 的執行狀態或快取資料。
        // EN: This field stores cloud batch upload log runtime state or cached data.
        public string CloudBatchUploadLog = "";
        // 這個欄位保存 雲端目標語言 的執行狀態或快取資料。
        // EN: This field stores cloud target language runtime state or cached data.
        public TargetLanguage CloudTargetLang = TargetLanguage.Traditional;

        [NonSerialized] public static Dictionary<string, CloudModRecord> SelectedCloudVersion = new Dictionary<string, CloudModRecord>(StringComparer.OrdinalIgnoreCase);

        // 這個欄位保存 自動翻譯OnUpdate 的執行狀態或快取資料。
        // EN: This field stores auto translate on update runtime state or cached data.
        public bool AutoTranslateOnUpdate = false;
        // 這個欄位保存 TimeoutSeconds 的執行狀態或快取資料。
        // EN: This field stores timeout seconds runtime state or cached data.
        public int TimeoutSeconds = 60;

        // 這個欄位保存 自動ClearOldOnUpdate 的執行狀態或快取資料。
        // EN: This field stores auto clear old on update runtime state or cached data.
        public bool AutoClearOldOnUpdate = false;
        public Dictionary<string, long> ModLastVerifiedTimes = new Dictionary<string, long>();
        public Dictionary<string, string> ModLastVerifiedFingerprints = new Dictionary<string, string>();
        public List<string> TranslationBlacklist = new List<string>();
        public List<string> CloudDownloadBlacklist = new List<string>();
        public List<string> ForceTranslationPackages = new List<string>();
        public List<string> CloudTranslationPriority = new List<string>();
        public List<string> UITranslationModBlacklist = new List<string>();
        public int CloudBatchDownloadConcurrency = 3;
        private static readonly object PackageBlacklistLock = new object();

        // 這個欄位保存 Filtered模組Count 的執行狀態或快取資料。
        // EN: This field stores filtered mods count runtime state or cached data.
        public static int FilteredModsCount = 0;

        // 這個欄位保存 HasAccepted導出Eula 的執行狀態或快取資料。
        // EN: This field stores has accepted export eula runtime state or cached data.
        public bool HasAcceptedExportEula = false;
        // 這個欄位保存 EulaAcceptedTimestamp 的執行狀態或快取資料。
        // EN: This field stores eula accepted timestamp runtime state or cached data.
        public string EulaAcceptedTimestamp = "";
        // 這個欄位保存 EulaAccepted版本 的執行狀態或快取資料。
        // EN: This field stores eula accepted version runtime state or cached data.
        public string EulaAcceptedVersion = "";
        // 這個欄位保存 EulaAcceptCount 的執行狀態或快取資料。
        // EN: This field stores eula accept count runtime state or cached data.
        public int EulaAcceptCount = 0;


        public List<string> ExportHistory = new List<string>();
        // 這個欄位保存 Today導出Date 的執行狀態或快取資料。
        // EN: This field stores today export date runtime state or cached data.
        public string TodayExportDate = "";
        // 這個欄位保存 Today導出Count 的執行狀態或快取資料。
        // EN: This field stores today export count runtime state or cached data.
        public int TodayExportCount = 0;


        // 這個方法負責判斷 IsEulaStillValid 條件是否成立。
        // EN: This method checks is eula still valid.
        public bool IsEulaStillValid()
        {
            if (!HasAcceptedExportEula) return false;
            if (string.IsNullOrEmpty(EulaAcceptedTimestamp)) return false;
            if (EulaAcceptedVersion != ExportEulaVersion.CurrentVersion) return false;

            if (DateTime.TryParse(EulaAcceptedTimestamp, out DateTime accepted))
            {
                return (DateTime.Now - accepted).TotalDays < 30;
            }
            return false;
        }


        // 這個方法負責取得 EulaRemainingDays 資料。
        // EN: This method gets eula remaining days.
        public int GetEulaRemainingDays()
        {
            if (!DateTime.TryParse(EulaAcceptedTimestamp, out DateTime accepted)) return 0;
            return 30 - (int)(DateTime.Now - accepted).TotalDays;
        }

        public bool IsTranslationBlacklisted(string packageId)
        {
            return ContainsPackageId(TranslationBlacklist, packageId);
        }

        public bool IsCloudDownloadBlacklisted(string packageId)
        {
            return ContainsPackageId(CloudDownloadBlacklist, packageId);
        }

        public void SetTranslationBlacklisted(string packageId, bool blocked)
        {
            SetPackageIdBlocked(TranslationBlacklist, packageId, blocked);
            AutoTranslatorMod.InvalidateValidModsCache();
        }

        public void SetCloudDownloadBlacklisted(string packageId, bool blocked)
        {
            SetPackageIdBlocked(CloudDownloadBlacklist, packageId, blocked);
        }

        public void ClearPackageBlacklists()
        {
            lock (PackageBlacklistLock)
            {
                TranslationBlacklist.Clear();
                CloudDownloadBlacklist.Clear();
            }
            AutoTranslatorMod.InvalidateValidModsCache();
        }

        public bool IsForceTranslationEnabled(string packageId)
        {
            return ContainsPackageId(ForceTranslationPackages, packageId);
        }

        public void SetForceTranslationEnabled(string packageId, bool enabled)
        {
            SetPackageIdBlocked(ForceTranslationPackages, packageId, enabled);
            AutoTranslatorMod.InvalidateValidModsCache();
            ModUpdateDetector.ClearStatusCache();
        }

        public bool IsUiTranslationModBlacklisted(string packageId)
        {
            return ContainsPackageId(UITranslationModBlacklist, packageId);
        }

        public void SetUiTranslationModBlacklisted(string packageId, bool blocked)
        {
            SetPackageIdBlocked(UITranslationModBlacklist, packageId, blocked);
            UIInterceptor.RefreshRuntimeUICache();
        }

        private static bool ContainsPackageId(List<string> packageIds, string packageId)
        {
            if (packageIds == null || string.IsNullOrWhiteSpace(packageId)) return false;
            lock (PackageBlacklistLock)
            {
                return packageIds.Any(id => string.Equals(id, packageId, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static void SetPackageIdBlocked(List<string> packageIds, string packageId, bool blocked)
        {
            if (packageIds == null || string.IsNullOrWhiteSpace(packageId)) return;
            string normalized = packageId.Trim().ToLowerInvariant();
            lock (PackageBlacklistLock)
            {
                packageIds.RemoveAll(id => string.Equals(id, normalized, StringComparison.OrdinalIgnoreCase));
                if (blocked) packageIds.Add(normalized);
            }
        }

        private static void NormalizePackageIdList(ref List<string> packageIds)
        {
            lock (PackageBlacklistLock)
            {
                packageIds = (packageIds ?? new List<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        private static void NormalizeOrderedPackageIdList(ref List<string> packageIds)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            packageIds = (packageIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim().ToLowerInvariant())
                .Where(id => seen.Add(id))
                .ToList();
        }

        // 這個方法負責處理 AddLog 相關流程。
        // EN: This method handles add log.
        public static void AddLog(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            lock (logLock)
            {
                RuntimeLogs.Add(line);
                if (RuntimeLogs.Count > 500) RuntimeLogs.RemoveAt(0);
                WriteLogToFile(line);
            }
        }

        // 這個方法負責處理 AddErrorLog 相關流程。
        // EN: This method handles add error log.
        public static void AddErrorLog(string msg)
        {
            lock (logLock)
            {
                if (ErrorLogs.Any(x => x.Contains(msg))) return;
                string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
                ErrorLogs.Add(line);
                if (ErrorLogs.Count > 100) ErrorLogs.RemoveAt(0);
                errorScrollPos.y = 99999f;
                WriteLogToFile("[ERROR] " + line);
            }


            ATC_Dispatcher.RunOnMainThread(() =>
            {
                TryShowRejectMessage(msg);
            });
        }

        // 這個方法負責嘗試執行 ShowRejectMessage 並回報是否成功。
        // EN: This method tries to show reject message and reports whether it succeeded.
        private static void TryShowRejectMessage(string msg)
        {
            try
            {
                if (string.IsNullOrEmpty(msg)) return;
                if (Current.ProgramState == ProgramState.MapInitializing) return;

                Verse.Messages.Message(msg, RimWorld.MessageTypeDefOf.RejectInput, false);
            }
            catch
            {

            }
        }
        // 這個方法負責保存 LogToFile 資料。
        // EN: This method saves log to file.
        private static void WriteLogToFile(string line)
        {
            try
            {
                string path = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "AutoTranslation_Log.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, line + "\n");
            }
            catch { }
        }

        // 這個方法負責清除 Log 資料。
        // EN: This method clears log.
        public static void ClearLog()
        {
            lock (logLock)
            {
                RuntimeLogs.Clear();
                ErrorLogs.Clear();
                logScrollPos = Vector2.zero;
                errorScrollPos = Vector2.zero;
            }
            try
            {
                string path = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "AutoTranslation_Log.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, $"=== Auto Translation Core V4.9 Ultimate [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ===\n\n");
            }
            catch { }
        }

        // 這個方法負責處理 Expose資料 相關流程。
        // EN: This method handles expose data.
        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref TargetLang, "TargetLang", TargetLanguage.Traditional);
            Scribe_Values.Look(ref HasManualTargetLanguage, "HasManualTargetLanguage", false);
            Scribe_Values.Look(ref OnlyScanActiveMods, "OnlyScanActiveMods", true);
            Scribe_Values.Look(ref EnableUIInterceptor, "EnableUIInterceptor", false);
            Scribe_Values.Look(ref EnableUINewTranslation, "EnableUINewTranslation", true);
            Scribe_Values.Look(ref EnableHardcodedUiPrototype, "EnableHardcodedUiPrototype", false);
            Scribe_Values.Look(ref EnableUIErrorLogInterception, "EnableUIErrorLogInterception", false);
            Scribe_Values.Look(ref TranslateWorkbenchModNames, "TranslateWorkbenchModNames", false);
            Scribe_Values.Look(ref ShowWorldMainButton, "ShowWorldMainButton", true);
            Scribe_Values.Look(ref MaxThreads, "MaxThreads", 3);
            Scribe_Values.Look(ref EnableTranslationPolicyAgent, "EnableTranslationPolicyAgent", false);
            Scribe_Values.Look(ref PolicyAgentMaxCallsPerRun, "PolicyAgentMaxCallsPerRun", 20);
            Scribe_Values.Look(ref PolicyAgentMaxEstimatedTokensPerRun, "PolicyAgentMaxEstimatedTokensPerRun", 200000L);
            Scribe_Values.Look(ref PolicyAgentMaxCallsPerMod, "PolicyAgentMaxCallsPerMod", 20);
            Scribe_Values.Look(ref PolicyAgentMaxRetriesPerRequest, "PolicyAgentMaxRetriesPerRequest", 1);
            PolicyAgentMaxCallsPerRun = Math.Min(20, Math.Max(0, PolicyAgentMaxCallsPerRun));
            PolicyAgentMaxEstimatedTokensPerRun = Math.Min(200000L, Math.Max(0L, PolicyAgentMaxEstimatedTokensPerRun));
            PolicyAgentMaxCallsPerMod = Math.Min(20, Math.Max(0, PolicyAgentMaxCallsPerMod));
            PolicyAgentMaxRetriesPerRequest = Math.Min(1, Math.Max(0, PolicyAgentMaxRetriesPerRequest));
            Scribe_Values.Look(ref ShowOriginalUI, "ShowOriginalUI", false);
            Scribe_Deep.Look(ref PolicyAgentApiConfig, "PolicyAgentApiConfig");
            Scribe_Collections.Look(ref ApiConfigs, "ApiConfigs", LookMode.Deep);
            Scribe_Values.Look(ref TotalCharCount, "TotalCharCount", 0L);

            Scribe_Values.Look(ref AutoClearOldOnUpdate, "AutoClearOldOnUpdate", false);
            Scribe_Collections.Look(ref ModLastVerifiedTimes, "ModLastVerifiedTimes", LookMode.Value, LookMode.Value);
            if (ModLastVerifiedTimes == null) ModLastVerifiedTimes = new Dictionary<string, long>();
            Scribe_Collections.Look(ref ModLastVerifiedFingerprints, "ModLastVerifiedFingerprints", LookMode.Value, LookMode.Value);
            if (ModLastVerifiedFingerprints == null) ModLastVerifiedFingerprints = new Dictionary<string, string>();
            Scribe_Collections.Look(ref TranslationBlacklist, "TranslationBlacklist", LookMode.Value);
            Scribe_Collections.Look(ref CloudDownloadBlacklist, "CloudDownloadBlacklist", LookMode.Value);
            Scribe_Collections.Look(ref ForceTranslationPackages, "ForceTranslationPackages", LookMode.Value);
            Scribe_Collections.Look(ref CloudTranslationPriority, "CloudTranslationPriority", LookMode.Value);
            Scribe_Collections.Look(ref UITranslationModBlacklist, "UITranslationModBlacklist", LookMode.Value);
            NormalizePackageIdList(ref TranslationBlacklist);
            NormalizePackageIdList(ref CloudDownloadBlacklist);
            NormalizePackageIdList(ref ForceTranslationPackages);
            NormalizeOrderedPackageIdList(ref CloudTranslationPriority);
            NormalizePackageIdList(ref UITranslationModBlacklist);
            Scribe_Values.Look(ref CloudBatchDownloadConcurrency, "CloudBatchDownloadConcurrency", 3);
            CloudBatchDownloadConcurrency = Math.Max(1, Math.Min(4, CloudBatchDownloadConcurrency));

            Scribe_Values.Look(ref TimeoutSeconds, "TimeoutSeconds", 60);

            Scribe_Values.Look(ref CloudNickname, "CloudNickname", "野生大佬");
            Scribe_Values.Look(ref CloudAdminToken, "CloudAdminToken", "");
            Scribe_Values.Look(ref CloudUploadType, "CloudUploadType", "AI_Auto");
            Scribe_Values.Look(ref CloudBatchUploadLog, "CloudBatchUploadLog", "");
            Scribe_Values.Look(ref CloudTargetLang, "CloudTargetLang", TargetLanguage.Traditional);

            Scribe_Values.Look(ref HasAcceptedExportEula, "HasAcceptedExportEula", false);
            Scribe_Values.Look(ref EulaAcceptedTimestamp, "EulaAcceptedTimestamp", "");
            Scribe_Values.Look(ref EulaAcceptedVersion, "EulaAcceptedVersion", "");
            Scribe_Values.Look(ref EulaAcceptCount, "EulaAcceptCount", 0);
            Scribe_Values.Look(ref AutoTranslateOnUpdate, "AutoTranslateOnUpdate", false);


            Scribe_Collections.Look(ref ExportHistory, "ExportHistory", LookMode.Value);
            Scribe_Values.Look(ref TodayExportDate, "TodayExportDate", "");
            Scribe_Values.Look(ref TodayExportCount, "TodayExportCount", 0);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (ApiConfigs == null || ApiConfigs.Count == 0)
                {
                    ApiConfigs = new List<ApiKeyConfig> { new ApiKeyConfig() };
                }

                if (PolicyAgentApiConfig == null)
                {
                    PolicyAgentApiConfig = new ApiKeyConfig
                    {
                        Label = "Policy Agent"
                    };
                }

                if (ExportHistory == null) ExportHistory = new List<string>();
            }
        }
    }
}
