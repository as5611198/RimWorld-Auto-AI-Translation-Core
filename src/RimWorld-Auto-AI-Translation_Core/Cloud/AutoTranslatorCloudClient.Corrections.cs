using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorCloudClient
    {
        public static async Task<List<AppliedTranslationCorrection>> FetchAppliedCorrectionsAsync(string packageId, string targetLangFolder)
        {
            if (string.IsNullOrWhiteSpace(packageId) || string.IsNullOrWhiteSpace(targetLangFolder))
                return new List<AppliedTranslationCorrection>();

            int maxRetries = 3;
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt >= 2 && CloudApiBaseUrl == PrimaryApiBaseUrl)
                {
                    CloudApiBaseUrl = BackupApiBaseUrl;
                    LogCloudTranslatedWarning("ATC_Cloud_CorrectionSubmitFallback");
                }

                try
                {
                    string url = $"{CloudApiBaseUrl}/corrections/applied/{Uri.EscapeDataString(packageId)}/{Uri.EscapeDataString(targetLangFolder)}";
                    var tcs = new TaskCompletionSource<string>();

                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        try
                        {
                            var request = UnityEngine.Networking.UnityWebRequest.Get(url);
                            request.timeout = 30;
                            var operation = request.SendWebRequest();
                            operation.completed += (op) =>
                            {
                                try
                                {
                                    if (UnityWebRequestCompat.IsSuccess(request))
                                    {
                                        byte[] rawData = request.downloadHandler != null ? request.downloadHandler.data : null;
                                        if (rawData != null && rawData.Length > 0)
                                        {
                                            System.Text.Encoding tolerantUtf8 = new System.Text.UTF8Encoding(false, false);
                                            tcs.TrySetResult(tolerantUtf8.GetString(rawData));
                                        }
                                        else
                                        {
                                            tcs.TrySetResult("");
                                        }
                                    }
                                    else
                                    {
                                        tcs.TrySetException(new Exception(request.error));
                                    }
                                }
                                catch (Exception innerEx) { tcs.TrySetException(innerEx); }
                                finally { request.Dispose(); }
                            };
                        }
                        catch (Exception dispatchEx) { tcs.TrySetException(dispatchEx); }
                    });

                    string jsonResponse = await WaitForCloudTask(tcs.Task, 40, "applied corrections fetch");
                    if (string.IsNullOrWhiteSpace(jsonResponse))
                        return null;

                    AppliedTranslationCorrectionsResponse response =
                        JsonConvert.DeserializeObject<AppliedTranslationCorrectionsResponse>(jsonResponse);
                    if (response == null || response.Corrections == null)
                        return null;
                    return response.Corrections;
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries)
                    {
                        Verse.Log.Warning($"[ATC Cloud] Applied correction fetch failed for {packageId}/{targetLangFolder}: {ex.Message}");
                        return null;
                    }
                }

                int delay = (int)Math.Pow(2, attempt + 1) * 1000 + new Random().Next(100, 500);
                await Task.Delay(delay);
            }

            return null;
        }

        public static async Task<bool> SubmitCorrectionAsync(TranslationCorrectionSubmission submission, string adminToken)
        {
            if (submission == null) return false;

            if (string.IsNullOrWhiteSpace(submission.ClientSubmissionId))
                submission.ClientSubmissionId = Guid.NewGuid().ToString("N");
            if (submission.CreatedAt == default(DateTime))
                submission.CreatedAt = DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(submission.QualityTier))
                submission.QualityTier = "InGameCorrection";
            if (string.IsNullOrWhiteSpace(submission.StatusHint))
                submission.StatusHint = "pending";

            int maxRetries = 4;

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt >= 3 && CloudApiBaseUrl == PrimaryApiBaseUrl)
                {
                    CloudApiBaseUrl = BackupApiBaseUrl;
                    LogCloudTranslatedWarning("ATC_Cloud_CorrectionSubmitFallback");
                }

                try
                {
                    var payload = new Dictionary<string, object>
                    {
                        { "ClientSubmissionId", submission.ClientSubmissionId },
                        { "PackageId", submission.PackageId ?? "" },
                        { "Language", submission.Language ?? "" },
                        { "ModName", submission.ModName ?? "" },
                        { "GameVersion", submission.GameVersion ?? "" },
                        { "ModLastUpdated", submission.ModLastUpdated ?? "" },
                        { "ScopeType", submission.ScopeType ?? "" },
                        { "EntryType", submission.EntryType ?? "" },
                        { "EntryKey", submission.EntryKey ?? "" },
                        { "SourceText", submission.SourceText ?? "" },
                        { "CurrentTranslation", submission.CurrentTranslation ?? "" },
                        { "ProposedTranslation", submission.ProposedTranslation ?? "" },
                        { "Reason", submission.Reason ?? "" },
                        { "ContributorId", submission.ContributorId ?? "" },
                        { "ContributorName", submission.ContributorName ?? "" },
                        { "QualityTier", submission.QualityTier ?? "InGameCorrection" },
                        { "StatusHint", submission.StatusHint ?? "pending" },
                        { "IsOfficialGameText", submission.IsOfficialGameText },
                        { "CreatedAt", submission.CreatedAt.ToString("O") },
                        { "AdminToken", adminToken ?? "" }
                    };

                    string jsonPayload = JsonConvert.SerializeObject(payload);
                    System.Text.Encoding tolerantUtf8 = new System.Text.UTF8Encoding(false, false);
                    byte[] payloadBytes = tolerantUtf8.GetBytes(jsonPayload);

                    var tcs = new TaskCompletionSource<bool>();
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        try
                        {
                            var request = new UnityEngine.Networking.UnityWebRequest($"{CloudApiBaseUrl}/corrections/submit", "POST");
                            request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(payloadBytes);
                            request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                            request.SetRequestHeader("Content-Type", "application/json");
                            request.timeout = 45;

                            var operation = request.SendWebRequest();
                            operation.completed += (op) =>
                            {
                                try
                                {
                                    if (UnityWebRequestCompat.IsSuccess(request))
                                    {
                                        tcs.TrySetResult(true);
                                    }
                                    else
                                    {
                                        string detail = request.downloadHandler != null ? request.downloadHandler.text : request.error;
                                        if (string.IsNullOrEmpty(detail)) detail = request.error;
                                        tcs.TrySetException(new Exception($"HTTP {request.responseCode}: {detail}"));
                                    }
                                }
                                catch (Exception innerEx) { tcs.TrySetException(innerEx); }
                                finally { request.Dispose(); }
                            };
                        }
                        catch (Exception dispatchEx) { tcs.TrySetException(dispatchEx); }
                    });

                    bool success = await WaitForCloudTask(tcs.Task, 55, "correction submit");
                    if (success) return true;
                }
                catch (Exception ex)
                {
                    if (attempt == maxRetries)
                    {
                        SaveCorrectionOutbox(submission);
                        LogCloudTranslatedWarning("ATC_Cloud_CorrectionSubmitSavedOutbox", ex.Message);
                        return false;
                    }
                }

                int delay = (int)Math.Pow(2, attempt + 1) * 1000 + new Random().Next(100, 500);
                await Task.Delay(delay);
            }

            SaveCorrectionOutbox(submission);
            return false;
        }

        private static void SaveCorrectionOutbox(TranslationCorrectionSubmission submission)
        {
            try
            {
                string root = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Correction_Outbox");
                Directory.CreateDirectory(root);
                string packageId = MakeSafeFileName(submission.PackageId ?? "unknown");
                string entryKey = MakeSafeFileName(submission.EntryKey ?? "entry");
                string fileName = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{packageId}_{entryKey}.json";
                string path = Path.Combine(root, fileName);
                File.WriteAllText(path, JsonConvert.SerializeObject(submission, Formatting.Indented), new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Verse.Log.Warning($"[ATC Cloud] Failed to save correction outbox: {ex.Message}");
            }
        }

        private static string MakeSafeFileName(string value)
        {
            if (string.IsNullOrEmpty(value)) return "empty";
            foreach (char c in Path.GetInvalidFileNameChars())
                value = value.Replace(c, '_');
            if (value.Length > 80) value = value.Substring(0, 80);
            return value;
        }
    }
}
