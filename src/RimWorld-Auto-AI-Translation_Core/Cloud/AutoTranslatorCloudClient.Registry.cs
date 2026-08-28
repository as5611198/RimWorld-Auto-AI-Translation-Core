using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責雲端登錄清單同步。
// EN: This file fetches and refreshes the cloud registry list.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器雲端Client 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorCloudClient.
    public static partial class AutoTranslatorCloudClient
    {

        // 這個方法負責向外部服務取得 登錄Async。
        // EN: This method fetches registry async.
        public static async Task<List<CloudModRecord>> FetchRegistryAsync()
        {
            int maxRetries = 4;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                string baseUrl = attempt >= 3 ? BackupApiBaseUrl : PrimaryApiBaseUrl;
                if (attempt == 3)
                {
                    LogCloudTranslatedWarning("ATC_Cloud_MainRouteCrashed");
                }

                try
                {
                    string url = $"{baseUrl}/registry?fresh=1&t={DateTime.UtcNow.Ticks}";


                    var tcs = new TaskCompletionSource<string>();
                    int timeoutSeconds = 15 + attempt * 15;


                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        try
                        {
                            var request = UnityEngine.Networking.UnityWebRequest.Get(url);
                            request.timeout = timeoutSeconds;

                            var operation = request.SendWebRequest();


                            operation.completed += (op) =>
                            {
                                try
                                {
                                    if (UnityWebRequestCompat.IsSuccess(request))
                                    {

                                        byte[] rawData = request.downloadHandler.data;
                                        if (rawData != null && rawData.Length > 0)
                                        {
                                            System.Text.Encoding tolerantUtf8 = new System.Text.UTF8Encoding(false, false);
                                            string json = tolerantUtf8.GetString(rawData);
                                            tcs.TrySetResult(json);
                                        }
                                        else
                                        {
                                            tcs.TrySetResult(null);
                                        }
                                    }
                                    else
                                    {

                                        tcs.TrySetException(new Exception(request.error));
                                    }
                                }
                                catch (Exception innerEx)
                                {
                                    tcs.TrySetException(innerEx);
                                }
                                finally
                                {
                                    request.Dispose();
                                }
                            };
                        }
                        catch (Exception dispatchEx)
                        {
                            tcs.TrySetException(dispatchEx);
                        }
                    });


                    string jsonResponse = await WaitForCloudTask(tcs.Task, timeoutSeconds + 10, "registry fetch");

                    if (!string.IsNullOrEmpty(jsonResponse))
                    {
                        var records = JsonConvert.DeserializeObject<List<CloudModRecord>>(jsonResponse);
                        return records ?? new List<CloudModRecord>();
                    }
                }
                catch (Exception ex)
                {

                    if (attempt == maxRetries)
                    {
                        LogCloudTranslatedWarning("ATC_Cloud_ConnectionFailed", ex.Message);
                        return null;
                    }
                }


                int delayMs = (int)Math.Pow(2, attempt + 1) * 1000 + new System.Random().Next(100, 500);
                LogCloudTranslatedMessage("ATC_Cloud_RetryAttemptLog", attempt + 1);
                await Task.Delay(delayMs);
            }
            return null;
        }
    }
}
