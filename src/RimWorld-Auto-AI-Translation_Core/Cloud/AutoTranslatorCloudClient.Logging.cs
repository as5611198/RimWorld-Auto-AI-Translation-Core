using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責雲端請求的日誌與錯誤包裝。
// EN: This file wraps cloud request logging and error reporting.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器雲端Client 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorCloudClient.
    public static partial class AutoTranslatorCloudClient
    {

        // 這個欄位保存 WaitFor雲端Task 的執行狀態或快取資料。
        // EN: This field stores wait for cloud task runtime state or cached data.
        private static async Task<T> WaitForCloudTask<T>(Task<T> task, int timeoutSeconds, string operationName)
        {
            Task timeoutTask = Task.Delay(Math.Max(5, timeoutSeconds) * 1000);
            Task completedTask = await Task.WhenAny(task, timeoutTask);
            if (completedTask != task)
            {
                throw new TimeoutException($"{operationName} timed out after {timeoutSeconds}s");
            }

            return await task;
        }


        // 這個方法負責處理 Log雲端Message 相關流程。
        // EN: This method handles log cloud message.
        private static void LogCloudMessage(string message)
        {
            ATC_Dispatcher.RunOnMainThread(() => Verse.Log.Message("[ATC Cloud] " + message));
        }


        // 這個方法負責處理 Log雲端Warning 相關流程。
        // EN: This method handles log cloud warning.
        private static void LogCloudWarning(string message)
        {
            ATC_Dispatcher.RunOnMainThread(() => Verse.Log.Warning("[ATC Cloud] " + message));
        }


        // 這個方法負責處理 Log雲端Error 相關流程。
        // EN: This method handles log cloud error.
        private static void LogCloudError(string message)
        {
            ATC_Dispatcher.RunOnMainThread(() => Verse.Log.Error("[ATC Cloud] " + message));
        }


        // 這個方法負責處理 Log雲端TranslatedMessage 相關流程。
        // EN: This method handles log cloud translated message.
        private static void LogCloudTranslatedMessage(string key, int arg)
        {
            ATC_Dispatcher.RunOnMainThread(() => Verse.Log.Message("[ATC Cloud] " + key.Translate(arg)));
        }

        private static void LogCloudTranslatedMessage(string key, string arg)
        {
            ATC_Dispatcher.RunOnMainThread(() => Verse.Log.Message("[ATC Cloud] " + key.Translate(arg)));
        }


        // 這個方法負責處理 Log雲端TranslatedWarning 相關流程。
        // EN: This method handles log cloud translated warning.
        private static void LogCloudTranslatedWarning(string key, string arg)
        {
            ATC_Dispatcher.RunOnMainThread(() => Verse.Log.Warning("[ATC Cloud] " + key.Translate(arg)));
        }


        // 這個方法負責處理 Log雲端TranslatedError 相關流程。
        // EN: This method handles log cloud translated error.
        private static void LogCloudTranslatedError(string key, string arg)
        {
            ATC_Dispatcher.RunOnMainThread(() => Verse.Log.Error("[ATC Cloud] " + key.Translate(arg)));
        }

        private static void LogCloudTranslatedError(string key, string arg1, long arg2, string arg3)
        {
            ATC_Dispatcher.RunOnMainThread(() => Verse.Log.Error("[ATC Cloud] " + key.Translate(arg1, arg2, arg3)));
        }

    }
}
