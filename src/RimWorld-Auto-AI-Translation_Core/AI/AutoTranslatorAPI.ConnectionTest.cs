using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Verse;
using AutoTranslator_Core.Workflow;
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
        // 這個方法負責處理 Run連線測試 相關流程。
        // EN: This method handles run connection test.
        public static void RunConnectionTest(ApiKeyConfig config)
        {
            if (config == null ||
                config.IsTesting ||
                AutoTranslatorSettings.IsRunning ||
                HasOutstandingTranslationWork)
                return;
            if (!config.Enabled) return;
            ATC_Dispatcher.EnsureAlive();

            config.IsTesting = true;
            config.TestStartedUtcTicks = DateTime.UtcNow.Ticks;
            int testGeneration = ++config.TestGeneration;
            AutoTranslatorSettings.ResetPipelineCancellation();

            Task.Run(async () =>
            {
                try
                {
                    // Do not wrap this in Task.WhenAny: queue/dispatch waiting is not an API
                    // response timeout, and a connection test must never abort unrelated
                    // translation requests globally.
                    bool result = await WorkflowBackendRuntime.GetOrCreate().RunApiConnectionTestAsync(config);
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        if (config.TestGeneration != testGeneration) return;
                        try
                        {
                            if (result)
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

    }
}
