#if RIMWORLD_1_7
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static AutoTranslator_Core.AutoTranslatorAPI;

namespace AutoTranslator_Core
{
    public static partial class AutoTranslatorAPI
    {
        public static void AbortActiveTranslationRequests(string reason)
        {
            NativeHttpTransport.AbortTranslations();
        }

        private static Task<ATC_WebResponse> SendModelsRequestAsync(string url, TranslatorProvider provider,
            string apiKey, Func<bool> cancelled)
        {
            var headers = new Dictionary<string, string> { { "Accept", "application/json" } };
            if (provider != TranslatorProvider.Google && !string.IsNullOrEmpty(apiKey))
                headers.Add(provider == TranslatorProvider.MiMo ? "api-key" : "Authorization",
                    provider == TranslatorProvider.MiMo ? apiKey : "Bearer " + apiKey);
            return NativeHttpTransport.SendAsync(url, "GET", null, headers, 15, cancelled, false, true);
        }
    }

    // One connection pool per mod, with credentials confined to each request.
    internal static class NativeHttpTransport
    {
        private static readonly HttpClient Client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            AllowAutoRedirect = false
        }) { Timeout = Timeout.InfiniteTimeSpan };

        private static readonly ConcurrentDictionary<int, CancellationTokenSource> ActiveTranslations =
            new ConcurrentDictionary<int, CancellationTokenSource>();
        private static int nextId;

        internal static async Task<ATC_WebResponse> SendAsync(string url, string method, byte[] body,
            Dictionary<string, string> headers, int timeoutSeconds, Func<bool> cancelled = null,
            bool translationRequest = false, bool requireJson = false)
        {
            int id = Interlocked.Increment(ref nextId);
            int seconds = Math.Max(1, timeoutSeconds);
            using (var stopped = new CancellationTokenSource())
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token, timeout.Token))
            using (var monitorStopped = new CancellationTokenSource())
            {
                if (translationRequest) ActiveTranslations.TryAdd(id, stopped);
                Task monitor = MonitorCancellationAsync(cancelled, stopped, monitorStopped.Token);
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    using (var request = new HttpRequestMessage(new HttpMethod(method), url))
                    {
                        if (body != null)
                        {
                            request.Content = new ByteArrayContent(body);
                            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                        }
                        if (headers != null)
                        {
                            foreach (var header in headers)
                            {
                                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                                request.Headers.Add(header.Key, header.Value);
                            }
                        }
                        using (HttpResponseMessage response = await Client.SendAsync(request,
                            HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false))
                        {
                            // JSON APIs use UTF-8, even when a gateway supplies an incorrect charset.
                            byte[] data = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
                            bool success = response.IsSuccessStatusCode;
                            string error = success ? string.Empty : "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase;
                            string contentType = response.Content.Headers.ContentType?.MediaType;
                            if (success && requireJson &&
                                (contentType == null || !contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase)))
                            {
                                success = false;
                                error = "Non-JSON Response";
                            }
                            return new ATC_WebResponse
                            {
                                IsSuccess = success, HttpCode = (long)response.StatusCode,
                                ErrorText = error, ResponseBody = new UTF8Encoding(false, false).GetString(data)
                            };
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return Failed(stopped.IsCancellationRequested ? "Request cancelled" : "Request timed out after " + seconds + "s");
                }
                catch (Exception ex)
                {
                    // HttpClient exception messages can contain URLs, including query credentials.
                    string safeError = ApiConnectionDiagnostic.Redact(ex.Message, new[] { url });
                    if (headers != null)
                        foreach (var header in headers) safeError = ApiConnectionDiagnostic.Redact(safeError, new[] { header.Value });
                    return Failed(safeError);
                }
                finally
                {
                    ActiveTranslations.TryRemove(id, out _);
                    monitorStopped.Cancel();
                    await monitor.ConfigureAwait(false);
                }
            }
        }

        private static ATC_WebResponse Failed(string error)
        {
            return new ATC_WebResponse { IsSuccess = false, HttpCode = 0, ErrorText = error, ResponseBody = string.Empty };
        }

        private static async Task MonitorCancellationAsync(Func<bool> cancelled,
            CancellationTokenSource stopped, CancellationToken monitorStopped)
        {
            if (cancelled == null) return;
            try
            {
                while (!monitorStopped.IsCancellationRequested)
                {
                    bool stop;
                    try { stop = cancelled(); }
                    catch { stop = true; }
                    if (stop) { stopped.Cancel(); return; }
                    await Task.Delay(50, monitorStopped).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        internal static void AbortTranslations()
        {
            foreach (var request in ActiveTranslations.Values)
            {
                try { request.Cancel(); }
                catch (ObjectDisposedException) { } // A completing request may dispose after enumeration.
            }
        }
    }
}
#endif
