#if RIMWORLD_1_7
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorCloudClient
    {
        // Waylith does not implement DownloadHandlerFile. Stream archives to disk
        // instead of buffering the entire ZIP in the Unity compatibility layer.
        private static class WaylithArchiveDownloader
        {
            private static readonly HttpClient Client = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };

            internal static async Task<CloudDownloadAttemptResult> DownloadAsync(
                string url, string path, int timeoutSeconds)
            {
                TryDeleteCloudTempFile(path);
                using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    try
                    {
                        using (var response = await Client.GetAsync(
                            url, HttpCompletionOption.ResponseHeadersRead, cancellation.Token).ConfigureAwait(false))
                        {
                            var result = new CloudDownloadAttemptResult
                            {
                                Success = response.IsSuccessStatusCode,
                                StatusCode = (long)response.StatusCode,
                                Error = response.IsSuccessStatusCode ? null : response.ReasonPhrase,
                                FilePath = path,
                                ServedRecordId = response.Headers.TryGetValues("X-ATC-Record-Id", out var ids) ? ids.FirstOrDefault() : null,
                                FallbackReason = response.Headers.TryGetValues("X-ATC-Record-Fallback", out var reasons) ? reasons.FirstOrDefault() : null
                            };
                            if (!result.Success) return result;

                            using (var input = await response.Content.ReadAsStreamAsync(cancellation.Token).ConfigureAwait(false))
                            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                            {
                                await input.CopyToAsync(output, cancellation.Token).ConfigureAwait(false);
                            }
                            return result;
                        }
                    }
                    catch (Exception error)
                    {
                        TryDeleteCloudTempFile(path);
                        return new CloudDownloadAttemptResult { Success = false, Error = error.Message, FilePath = path };
                    }
                }
            }
        }
    }
}
#endif
