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

            config.IsTesting = true;
            config.TestStartedUtcTicks = DateTime.UtcNow.Ticks;
            int testGeneration = ++config.TestGeneration;
            AutoTranslatorSettings.ResetPipelineCancellation();

            Task.Run(async () =>
            {
                try
                {
                    int timeoutSeconds = Math.Max(30, AutoTranslatorMod.Settings.TimeoutSeconds + 15);
                    var testTask = TranslateBatchAsync(new List<string> { "Connection Test" }, config);
                    var completedTask = await Task.WhenAny(testTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));

                    if (completedTask != testTask)
                    {
                        AbortActiveTranslationRequests("Connection test timed out");
                        ATC_Dispatcher.RunOnMainThread(() =>
                        {
                            if (config.TestGeneration != testGeneration) return;
                            AutoTranslatorSettings.AddErrorLog(TranslateText("ATC_Error_TestConnectionTimeout", config.Provider.ToString()));
                            Verse.Messages.Message("ATC_Msg_TestFailed".Translate(config.Provider.ToString()), RimWorld.MessageTypeDefOf.RejectInput, false);
                            config.IsTesting = false;
                            config.TestStartedUtcTicks = 0L;
                        });
                        return;
                    }

                    var result = await testTask;
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        if (config.TestGeneration != testGeneration) return;
                        try
                        {
                            if (result != null && result.Count > 0)
                            {
                                AutoTranslatorSettings.AddLog($"[{config.Provider}] " + "ATC_Log_TestSuccess".Translate());
                                Verse.Messages.Message("ATC_Msg_TestSuccess".Translate(config.Provider.ToString()), RimWorld.MessageTypeDefOf.PositiveEvent, false);
                            }
                            else
                            {
                                AutoTranslatorSettings.AddErrorLog(TranslateText("ATC_Log_TestFailed_Detail", config.Provider.ToString()));
                                Verse.Messages.Message("ATC_Msg_TestFailed".Translate(config.Provider.ToString()), RimWorld.MessageTypeDefOf.RejectInput, false);
                            }
                        }
                        finally
                        {
                            config.IsTesting = false;
                            config.TestStartedUtcTicks = 0L;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning($"[AutoTranslationCore] Test Thread Aborted: {ex.Message}");
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        if (config.TestGeneration != testGeneration) return;
                        AutoTranslatorSettings.AddErrorLog(TranslateText("ATC_Log_TestException", config.Provider.ToString(), ex.Message));
                        Verse.Messages.Message("ATC_Msg_TestFailed".Translate(config.Provider.ToString()), RimWorld.MessageTypeDefOf.RejectInput, false);
                        config.IsTesting = false;
                        config.TestStartedUtcTicks = 0L;
                    });
                }
            });
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
