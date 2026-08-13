using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorCloudClient
    {
        private const int PolicyAnalysisTimeoutSeconds = 8;
        // Keep DLL contributions disabled until the public service deploys the
        // schema-v2 domain-aware contract. Reads fail closed when unavailable.
        private const bool PolicyAnalysisDllContributionsEnabled = false;
        private static readonly PolicyAnalysisCloudHealthTracker PolicyAnalysisHealth =
            new PolicyAnalysisCloudHealthTracker();

        private sealed class PolicyAnalysisHttpResult
        {
            internal bool Success;
            internal long HttpCode;
            internal string Body;
            internal string Error;
            internal PolicyAnalysisCloudFailureKind FailureKind;
        }

        public static async Task<PolicyAnalysisCloudRecord> FetchPolicyAnalysisAsync(
            string packageId,
            string gameVersion,
            string sourceFingerprint,
            string policyVersion,
            string promptVersion)
        {
            return await FetchPolicyAnalysisAsync(
                PolicyAnalysisCandidateDomain.Xml,
                packageId,
                gameVersion,
                sourceFingerprint,
                policyVersion,
                promptVersion);
        }

        public static async Task<PolicyAnalysisCloudRecord> FetchPolicyAnalysisAsync(
            string candidateDomain,
            string packageId,
            string gameVersion,
            string sourceFingerprint,
            string policyVersion,
            string promptVersion)
        {
            if (AutoTranslatorSettings.IsCancellationRequested) return null;
            string domain = PolicyAnalysisCandidateDomain.Normalize(candidateDomain);
            if (domain.Length == 0 || string.IsNullOrWhiteSpace(packageId) ||
                string.IsNullOrWhiteSpace(sourceFingerprint))
                return null;

            string url = CloudApiBaseUrl.TrimEnd('/') + "/policy-analysis/" +
                Uri.EscapeDataString(domain) + "/" +
                Uri.EscapeDataString(packageId.Trim().ToLowerInvariant()) + "/" +
                Uri.EscapeDataString(gameVersion ?? string.Empty) + "/" +
                Uri.EscapeDataString(sourceFingerprint) +
                "?policyVersion=" + Uri.EscapeDataString(policyVersion ?? string.Empty) +
                "&promptVersion=" + Uri.EscapeDataString(promptVersion ?? string.Empty);
            if (!PolicyAnalysisHealth.CanAttempt(DateTime.UtcNow)) return null;
            PolicyAnalysisHttpResult http = await SendPolicyAnalysisRequestAsync(url, null);
            if (AutoTranslatorSettings.IsCancellationRequested && (http == null || !http.Success))
                return null;
            if (http == null || !http.Success)
            {
                ReportPolicyAnalysisFailure(http);
                return null;
            }

            try
            {
                PolicyAnalysisCloudRecord record = JsonConvert.DeserializeObject<PolicyAnalysisCloudRecord>(http.Body);
                if (!PolicyAnalysisRecordValidator.IsUsable(
                        record,
                        domain,
                        packageId,
                        gameVersion,
                        sourceFingerprint,
                        policyVersion,
                        promptVersion))
                {
                    ReportPolicyAnalysisFailure(new PolicyAnalysisHttpResult
                    {
                        HttpCode = http.HttpCode,
                        FailureKind = PolicyAnalysisCloudFailureKind.InvalidSchema,
                        Error = "record identity, schema, version, or candidate domain did not match"
                    });
                    return null;
                }
                record.AllowedCandidateIds = record.AllowedCandidateIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                ReportPolicyAnalysisSuccess();
                return record;
            }
            catch (Exception ex)
            {
                ReportPolicyAnalysisFailure(new PolicyAnalysisHttpResult
                {
                    HttpCode = http.HttpCode,
                    FailureKind = PolicyAnalysisCloudFailureKind.InvalidJson,
                    Error = ex.Message
                });
                return null;
            }
        }

        public static async Task<bool> AppendPolicyAnalysisAsync(PolicyAnalysisContribution contribution)
        {
            string domain = PolicyAnalysisCandidateDomain.Normalize(contribution?.CandidateDomain);
            if (contribution == null || contribution.CandidateCount < 0 ||
                contribution.SchemaVersion != 2 || domain.Length == 0 ||
                (domain == PolicyAnalysisCandidateDomain.Dll &&
                 !PolicyAnalysisDllContributionsEnabled) ||
                string.IsNullOrWhiteSpace(contribution.PackageId) ||
                string.IsNullOrWhiteSpace(contribution.SourceFingerprint) ||
                string.IsNullOrWhiteSpace(contribution.ContributorId) ||
                string.IsNullOrWhiteSpace(contribution.ContributionId))
            {
                return false;
            }

            // Ordinary clients can only contribute an append-only set of stable IDs.
            // The endpoint deliberately has no delete or replacement field.
            contribution.SchemaVersion = 2;
            contribution.CandidateDomain = domain;
            contribution.AddAllowedCandidateIds = (contribution.AddAllowedCandidateIds ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            if (contribution.AddAllowedCandidateIds.Count > contribution.CandidateCount ||
                !PolicyAnalysisCandidateDomain.AreCandidateIdsValid(
                    domain,
                    contribution.AddAllowedCandidateIds))
            {
                return false;
            }
            string url = CloudApiBaseUrl.TrimEnd('/') + "/policy-analysis/contributions";
            PolicyAnalysisHttpResult response = await SendPolicyAnalysisRequestAsync(
                url,
                JsonConvert.SerializeObject(contribution, Formatting.None));
            return response != null && response.Success && !string.IsNullOrWhiteSpace(response.Body);
        }

        private static async Task<PolicyAnalysisHttpResult> SendPolicyAnalysisRequestAsync(string url, string postJson)
        {
            TaskCompletionSource<PolicyAnalysisHttpResult> completion =
                new TaskCompletionSource<PolicyAnalysisHttpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> dispatchStarted =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            UnityWebRequest activeRequest = null;
            ATC_Dispatcher.RunOnMainThread(() =>
            {
                if (AutoTranslatorSettings.IsCancellationRequested)
                {
                    dispatchStarted.TrySetResult(false);
                    return;
                }

                try
                {
                    UnityWebRequest request;
                    if (postJson == null)
                    {
                        request = UnityWebRequest.Get(url);
                    }
                    else
                    {
                        request = new UnityWebRequest(url, "POST")
                        {
                            uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(postJson)),
                            downloadHandler = new DownloadHandlerBuffer()
                        };
                        request.SetRequestHeader("Content-Type", "application/json");
                    }
                    activeRequest = request;
                    request.timeout = PolicyAnalysisTimeoutSeconds;
                    UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                    dispatchStarted.TrySetResult(true);
                    operation.completed += _ =>
                    {
                        try
                        {
                            if (!UnityWebRequestCompat.IsSuccess(request))
                            {
                                long code = request.responseCode;
                                string error = request.error ?? string.Empty;
                                completion.TrySetResult(new PolicyAnalysisHttpResult
                                {
                                    Success = false,
                                    HttpCode = code,
                                    Body = request.downloadHandler?.text ?? string.Empty,
                                    Error = error,
                                    FailureKind = code == 404
                                        ? PolicyAnalysisCloudFailureKind.NotFound
                                        : IsTimeoutError(error)
                                            ? PolicyAnalysisCloudFailureKind.Timeout
                                            : code >= 400
                                                ? PolicyAnalysisCloudFailureKind.HttpError
                                                : PolicyAnalysisCloudFailureKind.Transport
                                });
                            }
                            else
                            {
                                string responseText = request.downloadHandler?.text;
                                completion.TrySetResult(new PolicyAnalysisHttpResult
                                {
                                    Success = true,
                                    HttpCode = request.responseCode,
                                    Body = string.IsNullOrWhiteSpace(responseText) ? "{}" : responseText,
                                    FailureKind = PolicyAnalysisCloudFailureKind.None
                                });
                            }
                        }
                        catch
                        {
                            completion.TrySetResult(new PolicyAnalysisHttpResult
                            {
                                FailureKind = PolicyAnalysisCloudFailureKind.Transport,
                                Error = "request completion callback failed"
                            });
                        }
                        finally
                        {
                            request.Dispose();
                        }
                    };
                }
                catch (Exception ex)
                {
                    dispatchStarted.TrySetResult(false);
                    completion.TrySetResult(new PolicyAnalysisHttpResult
                    {
                        FailureKind = PolicyAnalysisCloudFailureKind.Transport,
                        Error = "request dispatch failed: " + ex.Message
                    });
                }
            });

            try
            {
                if (!await TranslationRequestSignalWaiter.WaitAsync(
                        dispatchStarted.Task,
                        () => AutoTranslatorSettings.IsCancellationRequested))
                {
                    return null;
                }

                if (!await dispatchStarted.Task)
                    return completion.Task.IsCompleted ? await completion.Task : null;

                while (!completion.Task.IsCompleted)
                {
                    if (AutoTranslatorSettings.IsCancellationRequested)
                    {
                        ATC_Dispatcher.RunOnMainThread(() =>
                        {
                            try { activeRequest?.Abort(); }
                            catch { }
                        });
                        return null;
                    }
                    await Task.Delay(100);
                }

                return await completion.Task;
            }
            catch (Exception ex)
            {
                return new PolicyAnalysisHttpResult
                {
                    FailureKind = PolicyAnalysisCloudFailureKind.Transport,
                    Error = ex.Message
                };
            }
        }

        private static bool IsTimeoutError(string error)
        {
            return (error ?? string.Empty).IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (error ?? string.Empty).IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ReportPolicyAnalysisFailure(PolicyAnalysisHttpResult result)
        {
            PolicyAnalysisCloudFailureKind kind = result?.FailureKind ?? PolicyAnalysisCloudFailureKind.Transport;
            PolicyAnalysisCloudHealthTransition transition = PolicyAnalysisHealth.RecordFailure(kind, DateTime.UtcNow);
            string detail = "kind=" + kind + "; HTTP=" + (result?.HttpCode ?? 0L) +
                "; failures=" + transition.ConsecutiveFailures + "; error=" + (result?.Error ?? string.Empty);
            if (kind == PolicyAnalysisCloudFailureKind.NotFound)
            {
                Verse.Log.Message("[AutoTranslationCore] Policy cloud cache route unavailable; continuing with local rules/Agent. " + detail);
                return;
            }
            Verse.Log.Warning("[AutoTranslationCore] Policy cloud cache degraded; continuing without it. " + detail);
            if (transition.ShouldWarn)
                AutoTranslatorSettings.AddLog(AutoTranslatorAPI.TranslateText("ATC_PolicyCloud_RuntimeUnavailable"));
        }

        private static void ReportPolicyAnalysisSuccess()
        {
            PolicyAnalysisCloudHealthTransition transition = PolicyAnalysisHealth.RecordSuccess();
            if (!transition.ShouldReportRecovery) return;
            Verse.Log.Message("[AutoTranslationCore] Policy cloud cache recovered.");
            AutoTranslatorSettings.AddLog(AutoTranslatorAPI.TranslateText("ATC_PolicyCloud_RuntimeRecovered"));
        }
    }
}
