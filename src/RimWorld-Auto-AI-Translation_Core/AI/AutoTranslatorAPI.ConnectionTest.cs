using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Verse;
// 這個檔案負責翻譯 API 連線測試。
// EN: This file tests translation API connectivity.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器API 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorAPI.
    public static partial class AutoTranslatorAPI
    {
        // 這個方法負責處理 測試連線Async 相關流程。
        // EN: This method handles test connection async.
        public static async Task<bool> TestConnectionAsync()
        {
            Task<List<string>> testTask = TranslateBatchAsync(new List<string> { "Connection Test" });
            int timeoutSeconds = Math.Max(15, Math.Min(45, AutoTranslatorMod.Settings.TimeoutSeconds + 5));
            Task completedTask = await Task.WhenAny(testTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (completedTask != testTask)
            {
                AbortActiveTranslationRequests("Connection preflight timed out");
                AutoTranslatorSettings.AddErrorLog(TranslateText("ATC_Error_TestConnectionTimeout", "API"));
                return false;
            }

            var res = await testTask;
            return res != null && res.Count > 0;
        }

        // 這個方法負責處理 Run連線測試 相關流程。
        // EN: This method handles run connection test.
        public static void RunConnectionTest(ApiKeyConfig config)
        {
            if (config == null || config.IsTesting) return;
            if (!config.Enabled) return;
            if (!ValidateApiEndpointForRequest(config)) return;
            if (!IsConfigReady(config)) return;
            ATC_Dispatcher.EnsureAlive();

            // Freeze the request settings so editing a field cannot mix credentials, URLs, or results.
            var snapshot = new ApiKeyConfig
            {
                Provider = config.Provider, Key = config.Key, CustomBaseUrl = config.CustomBaseUrl,
                SelectedModel = config.SelectedModel, UsePlayer2Protocol = config.UsePlayer2Protocol,
                Player2GameClientId = config.Player2GameClientId, ResolvedBaseUrl = config.ResolvedBaseUrl,
                ResolvedBaseUrlFingerprint = config.ResolvedBaseUrlFingerprint
            };
            string endpoint = GetBaseUrl(snapshot);
            if (snapshot.Provider == TranslatorProvider.Google)
                endpoint += "/models/" + CleanInput(snapshot.SelectedModel) + ":generateContent";
            else endpoint += snapshot.Provider == TranslatorProvider.DeepL ? "/translate" : "/chat/completions";
            string fingerprint = GetConnectionTestFingerprint(config);
            string requestModel = snapshot.Provider == TranslatorProvider.Custom_OpenAI && snapshot.UsePlayer2Protocol
                ? string.Empty : CleanInput(snapshot.SelectedModel);
            int timeoutSeconds = GetTranslationTimeoutSeconds(snapshot.Provider, requestModel) + 15;
            config.LastConnectionTest = CreateConnectionDiagnostic(snapshot, endpoint, null);
            config.ConnectionTestFingerprint = fingerprint;
            config.TestTimeoutSeconds = timeoutSeconds;
            config.IsTesting = true;
            config.TestStartedUtcTicks = DateTime.UtcNow.Ticks;
            int testGeneration = ++config.TestGeneration;
            AutoTranslatorSettings.ResetPipelineCancellation();

            Task.Run(async () =>
            {
                ApiConnectionDiagnostic diagnostic = CreateConnectionDiagnostic(snapshot, endpoint, null);
                try
                {
                    var testTask = TranslateBatchCoreAsync(new List<string> { "Connection Test" }, snapshot, false,
                        (url, response) => diagnostic = CreateConnectionDiagnostic(snapshot,
                            string.IsNullOrEmpty(url) ? endpoint : url, response),
                        () => config.TestGeneration != testGeneration || !config.IsTesting);
                    var completedTask = await Task.WhenAny(testTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));

                    if (completedTask != testTask)
                    {
                        ATC_Dispatcher.RunOnMainThread(() =>
                        {
                            if (config.TestGeneration != testGeneration) return;
                            CompleteConnectionTest(config, testGeneration, fingerprint,
                                CreateConnectionDiagnostic(snapshot, endpoint, null).TimedOut());
                        });
                        return;
                    }

                    var result = await testTask;
                    if ((result == null || result.Count == 0) && diagnostic.Succeeded)
                        diagnostic = CreateConnectionDiagnostic(snapshot, endpoint,
                            new ATC_WebResponse { ErrorText = "Request cancelled" });
                    ApiConnectionDiagnostic completedDiagnostic = diagnostic;
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        CompleteConnectionTest(config, testGeneration, fingerprint, completedDiagnostic);
                    });
                }
                catch (Exception ex)
                {
                    ApiConnectionDiagnostic failedDiagnostic = CreateConnectionDiagnostic(snapshot, endpoint,
                        new ATC_WebResponse { ErrorText = ex.Message });
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        CompleteConnectionTest(config, testGeneration, fingerprint, failedDiagnostic);
                    });
                }
            });
        }

        private static ApiConnectionDiagnostic CreateConnectionDiagnostic(ApiKeyConfig config, string endpoint, ATC_WebResponse response)
        {
            return ApiConnectionDiagnostic.Create(config.Provider.ToString(), config.SelectedModel, endpoint,
                response != null && response.IsSuccess, response != null ? response.HttpCode : 0L,
                response != null ? response.ErrorText : string.Empty,
                response != null ? response.ResponseBody : string.Empty,
                config.Key, CleanInput(config.Key), config.Player2GameClientId);
        }

        internal static string GetConnectionTestFingerprint(ApiKeyConfig config)
        {
            return GetModelFetchFingerprint(config) + "|" + CleanInput(config.SelectedModel);
        }

        internal static void CheckConnectionTestTimeout(ApiKeyConfig config)
        {
            if (!config.IsTesting || config.TestStartedUtcTicks <= 0 || config.LastConnectionTest == null) return;
            double elapsed = (DateTime.UtcNow.Ticks - config.TestStartedUtcTicks) / (double)TimeSpan.TicksPerSecond;
            if (elapsed <= config.TestTimeoutSeconds + 2) return;
            CompleteConnectionTest(config, config.TestGeneration, config.ConnectionTestFingerprint, config.LastConnectionTest.TimedOut());
            config.TestGeneration++; // Discard a late completion after the watchdog released the UI.
        }

        private static void CompleteConnectionTest(ApiKeyConfig config, int generation, string fingerprint, ApiConnectionDiagnostic diagnostic)
        {
            if (config.TestGeneration != generation) return;
            config.IsTesting = false;
            config.TestStartedUtcTicks = 0L;
            config.LastConnectionTest = diagnostic;
            config.ConnectionTestFingerprint = fingerprint;
            if (GetConnectionTestFingerprint(config) != fingerprint) return;
            string summary = GetConnectionTestSummary(diagnostic);
            if (diagnostic.Succeeded)
            {
                AutoTranslatorSettings.AddLog(summary);
                Verse.Messages.Message(summary, RimWorld.MessageTypeDefOf.PositiveEvent, false);
            }
            else
            {
                AutoTranslatorSettings.AddErrorLog(GetConnectionTestReport(diagnostic), false);
                string detail = diagnostic.GetNotificationDetail();
                string notification = string.IsNullOrEmpty(detail) ? summary : summary + " " + detail;
                Verse.Messages.Message(notification, RimWorld.MessageTypeDefOf.RejectInput, false);
            }
        }

        internal static string GetConnectionTestSummary(ApiConnectionDiagnostic diagnostic)
        {
            string summary = TranslateText(diagnostic.HintKey);
            return diagnostic.HttpCode > 0 ? "HTTP " + diagnostic.HttpCode + ": " + summary : summary;
        }

        internal static string GetConnectionTestReport(ApiConnectionDiagnostic diagnostic)
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("Auto Translation Core — " + TranslateText("ATC_ConnectionTest_Title"));
            report.AppendLine(GetConnectionTestSummary(diagnostic));
            report.AppendLine();
            report.AppendLine(TranslateText("ATC_ConnectionTest_Provider", diagnostic.Provider));
            report.AppendLine(TranslateText("ATC_ConnectionTest_Model", diagnostic.Model));
            report.AppendLine(TranslateText("ATC_ConnectionTest_Endpoint", diagnostic.Endpoint));
            if (!string.IsNullOrEmpty(diagnostic.ServiceMessage))
                report.AppendLine(TranslateText("ATC_ConnectionTest_ServiceMessage", diagnostic.ServiceMessage));
            if (!string.IsNullOrEmpty(diagnostic.TransportError))
                report.AppendLine(TranslateText("ATC_ConnectionTest_TransportError", diagnostic.TransportError));
            return report.ToString().TrimEnd();
        }

        // 這個方法負責處理 AnalyzeAndLogNetworkError 相關流程。
        // EN: This method handles analyze and log network error.
        private static void AnalyzeAndLogNetworkError(TranslatorProvider provider, Exception ex)
        {
            string msg = ex.Message.ToLower();
            string friendlyError = "ATC_Error_Unknown".Translate();

            if (ex is TaskCanceledException || msg.Contains("timeout") || msg.Contains("timed out"))
            {
                friendlyError = "ATC_Error_Timeout".Translate();
            }
            else if (msg.Contains("cannot connect") || msg.Contains("connection refused") || msg.Contains("name resolution"))
            {
                friendlyError = "ATC_Error_Connection".Translate(provider.ToString());
            }
            else if (msg.Contains("401") || msg.Contains("403") || msg.Contains("unauthorized"))
            {
                friendlyError = "ATC_Error_Unauthorized".Translate();
            }
            else
            {
                friendlyError = ex.Message;
            }

            AutoTranslatorSettings.AddErrorLog($"[{provider}] {"ATC_Error_NetworkAbnormal".Translate()}: {friendlyError}");
            Log.Error($"[AutoTranslationCore] Detailed Exception [{provider}]: {ex}");
        }
    }
}
