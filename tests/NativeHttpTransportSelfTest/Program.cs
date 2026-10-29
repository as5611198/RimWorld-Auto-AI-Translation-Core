using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

internal static partial class Program
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type api, provider, transport, settings;
    private static int passed;
    private const string Payload = "{\"text\":\"繁體中文、简体中文 😀 {0}\\n下一行\"}";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("Usage: NativeHttpTransportSelfTest <mod.dll> <game directory> <harmony.dll> [legacy DLLs...]"); return 2; }
        string modPath = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (_, request) =>
        {
            string file = new AssemblyName(request.Name).Name + ".dll";
            foreach (string dir in new[] { Path.GetDirectoryName(modPath), args[1], Path.GetDirectoryName(args[2]) })
            {
                string path = Path.Combine(dir, file);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try
        {
            Console.WriteLine("Runtime=" + Environment.Version + "; CoreLib=" + typeof(object).Assembly.Location);
            using (var pe = new PEReader(File.OpenRead(modPath)))
            {
                MetadataReader metadata = pe.GetMetadataReader();
                Check(!metadata.TypeReferences.Select(h => metadata.GetTypeReference(h))
                    .Any(t => metadata.GetString(t.Namespace) == "UnityEngine.Networking"),
                    "1.7 product DLL has no UnityEngine.Networking references (including cloud routes)");
            }
            foreach (string legacyPath in args.Skip(3))
            {
                using var pe = new PEReader(File.OpenRead(legacyPath));
                MetadataReader metadata = pe.GetMetadataReader();
                string[] names = metadata.TypeDefinitions.Select(h => metadata.GetString(metadata.GetTypeDefinition(h).Name)).ToArray();
                Check(names.Contains("ATC_WebRequestEngine") && !names.Contains("NativeHttpTransport"),
                    "legacy DLL retains only its original web request engine: " + legacyPath);
                Check(names.Contains("ApiThinkingPolicy") && names.Contains("Window_TranslationPreferences"),
                    "legacy DLL includes the shared thinking controls and prompt editor: " + legacyPath);
                Check(metadata.TypeReferences.Select(h => metadata.GetTypeReference(h)).Any(t =>
                    metadata.GetString(t.Namespace) == "UnityEngine.Networking" && metadata.GetString(t.Name) == "UnityWebRequest"),
                    "legacy DLL continues to reference UnityWebRequest: " + legacyPath);
            }
            Assembly mod = Assembly.LoadFrom(modPath);
            api = mod.GetType("AutoTranslator_Core.AutoTranslatorAPI", true);
            provider = mod.GetType("AutoTranslator_Core.TranslatorProvider", true);
            transport = mod.GetType("AutoTranslator_Core.NativeHttpTransport", true);
            settings = mod.GetType("AutoTranslator_Core.AutoTranslatorSettings", true);
            settings.GetMethod("ResetPipelineCancellation", Static).Invoke(null, null);
            Check(mod.GetType("AutoTranslator_Core.AutoTranslatorAPI+ATC_WebRequestEngine") == null,
                "1.7 excludes the Coroutine web request engine");
            using var server = new FixtureServer();

            object response = await Post(server.Url + "/utf8", "DeepSeek", "synthetic-deepseek");
            Check(Success(response) && Text(response) == Payload, "POST response preserves Chinese, emoji, escapes and format arguments");
            var request = server.Requests.Single(r => r.Path == "/utf8");
            Check(request.Method == "POST" && request.Body.SequenceEqual(Encoding.UTF8.GetBytes(Payload)), "POST sends exact UTF-8 bytes without BOM");
            Check(request.Headers["Authorization"] == "Bearer synthetic-deepseek" &&
                request.Headers["Content-Type"].Contains("charset=utf-8"), "OpenAI-compatible request has per-request Bearer and UTF-8 content type");
            response = await Post(server.Url + "/charset", "DeepSeek", "synthetic-deepseek");
            Check(Text(response) == Payload, "incorrect response charset cannot turn UTF-8 Chinese into mojibake");
            response = await Post(server.Url + "/gzip", "DeepSeek", "synthetic-deepseek");
            Check(Success(response) && Text(response) == Payload, "gzip response is decompressed before UTF-8 decoding");

            await Post(server.Url + "/deepl", "DeepL", "synthetic-deepl");
            Check(server.Requests.Single(r => r.Path == "/deepl").Headers["Authorization"] == "DeepL-Auth-Key synthetic-deepl", "DeepL authentication is preserved");
            await Post(server.Url + "/mimo", "MiMo", "synthetic-mimo");
            request = server.Requests.Single(r => r.Path == "/mimo");
            Check(request.Headers["api-key"] == "synthetic-mimo" && !request.Headers.ContainsKey("Authorization"), "MiMo uses api-key without Bearer leakage");
            const string gameId = "403ae417-9698-42d6-a7bb-629c2ed35700";
            await Post(server.Url + "/player2", "Custom_OpenAI", "must-not-send", null, 5, true, gameId);
            request = server.Requests.Single(r => r.Path == "/player2");
            Check(request.Headers["player2-game-key"] == gameId && !request.Headers.ContainsKey("Authorization") &&
                !request.Headers.ContainsKey("api-key"), "Player2 keeps its registered Game Client ID and excludes other credentials");
            await Post(server.Url + "/google?key=synthetic-google", "Google", "synthetic-google");
            request = server.Requests.Single(r => r.Path.StartsWith("/google?"));
            Check(!request.Headers.ContainsKey("Authorization") && !request.Headers.ContainsKey("api-key"), "Google keeps query authentication without adding Bearer");
            await Post(server.Url + "/keyless", "Custom_OpenAI", "");
            request = server.Requests.Single(r => r.Path == "/keyless");
            Check(!request.Headers.ContainsKey("Authorization") && !request.Headers.ContainsKey("api-key") &&
                !request.Headers.ContainsKey("player2-game-key"), "keyless requests inherit no credentials from the shared pool");
            await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Post(server.Url + "/parallel/" + i, "DeepSeek", "synthetic-" + i)));
            Check(server.Requests.Where(r => r.Path.StartsWith("/parallel/")).All(r =>
                r.Headers["Authorization"] == "Bearer synthetic-" + r.Path.Split('/').Last()), "concurrent requests cannot exchange API keys");

            response = await Models(server.Url + "/models", "DeepSeek", "synthetic-models");
            request = server.Requests.Single(r => r.Path == "/models");
            Check(Success(response) && request.Method == "GET" && request.Headers["Accept"] == "application/json" &&
                request.Headers["Authorization"] == "Bearer synthetic-models", "model-list GET uses native transport and the correct headers");
            await Models(server.Url + "/mimo-models", "MiMo", "synthetic-mimo-models");
            Check(server.Requests.Single(r => r.Path == "/mimo-models").Headers["api-key"] == "synthetic-mimo-models", "MiMo model-list GET uses api-key");
            await Models(server.Url + "/google-models?key=synthetic-google", "Google", "synthetic-google");
            Check(!server.Requests.Single(r => r.Path.StartsWith("/google-models?")).Headers.ContainsKey("Authorization"), "Google model-list GET adds no Bearer");
            response = await Models(server.Url + "/html", "DeepSeek", "synthetic-models");
            Check(!Success(response) && Code(response) == 200 && Error(response) == "Non-JSON Response", "model-list HTML response is rejected");

            foreach (int code in new[] { 401, 403, 404, 429, 500 })
            {
                response = await Post(server.Url + "/status/" + code, "DeepSeek", "synthetic-test");
                Check(!Success(response) && Code(response) == code && Text(response).Contains("fixture error"), "HTTP " + code + " retains response body for existing diagnostics");
            }
            Check(server.Requests.Count(r => r.Path == "/status/500") == 1, "transport does not duplicate POSTs with its own retries");
            response = await Post(server.Url + "/redirect", "MiMo", "synthetic-mimo");
            Check(!Success(response) && Code(response) == 302 && !server.Requests.Any(r => r.Path == "/redirect-target"), "redirect does not forward custom credentials to another endpoint");

            foreach (string path in new[] { "/slow-headers", "/slow-body" })
            {
                var timer = Stopwatch.StartNew();
                response = await Post(server.Url + path, "DeepSeek", "synthetic-test", null, 1);
                Check(!Success(response) && Code(response) == 0 && Error(response).Contains("timed out") && timer.ElapsedMilliseconds < 2500,
                    "timeout cancels socket I/O while waiting for " + path);
            }
            int before = server.Requests.Count;
            response = await Post(server.Url + "/never-send", "DeepSeek", "synthetic-test", () => true);
            Check(Error(response).Contains("cancelled") && server.Requests.Count == before, "pre-cancelled request is never sent");
            int cancelFlag = 0;
            Task<object> pending = Post(server.Url + "/cancel", "DeepSeek", "synthetic-test", () => Volatile.Read(ref cancelFlag) != 0);
            await server.WaitFor("/cancel");
            Interlocked.Exchange(ref cancelFlag, 1);
            response = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!Success(response) && Error(response).Contains("cancelled"), "per-request cancellation interrupts an active socket");

            int cloudCancel = 0;
            Task<object> cloud = Raw(server.Url + "/cloud-wait", "GET", null, null, 10, () => Volatile.Read(ref cloudCancel) != 0);
            Task<object> first = Post(server.Url + "/abort-one", "DeepSeek", "synthetic-one");
            Task<object> second = Post(server.Url + "/abort-two", "MiMo", "synthetic-two");
            await server.WaitFor("/cloud-wait"); await server.WaitFor("/abort-one"); await server.WaitFor("/abort-two");
            settings.GetMethod("RequestPipelineCancellation", Static).Invoke(null, null);
            settings.GetMethod("ResetPipelineCancellation", Static).Invoke(null, null);
            object[] aborted = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
            Check(aborted.All(r => !Success(r) && Error(r).Contains("cancelled")), "global cancellation aborts concurrent translations even after cancellation flag reset");
            Check(!cloud.IsCompleted, "translation cancellation does not abort an unrelated cloud request");
            Interlocked.Exchange(ref cloudCancel, 1); await cloud;
            response = await Post(server.Url + "/after-abort", "DeepSeek", "synthetic-new");
            Check(Success(response), "new requests work after aborting previous requests");

            Type configType = mod.GetType("AutoTranslator_Core.ApiKeyConfig", true);
            object custom = Activator.CreateInstance(configType);
            configType.GetField("Provider").SetValue(custom, Enum.Parse(provider, "Custom_OpenAI"));
            configType.GetField("CustomBaseUrl").SetValue(custom, server.Url);
            configType.GetField("Key").SetValue(custom, "");
            configType.GetField("Enabled").SetValue(custom, true);
            var refresh = api.GetMethod("AutoFetchForConfig", Static, null, new[] { configType, typeof(bool) }, null);
            before = server.Requests.Count;
            refresh.Invoke(null, new[] { custom, (object)false });
            api.GetMethod("MaintainModelFetchState", Static, null, new[] { configType }, null).Invoke(null, new[] { custom });
            Check(server.Requests.Count == before && !(bool)configType.GetField("IsFetching").GetValue(custom),
                "typing a custom URL without a key triggers no request or fetching state");
            configType.GetField("Key").SetValue(custom, "partially-entered-key");
            refresh.Invoke(null, new[] { custom, (object)false });
            Check(server.Requests.Count == before && !(bool)configType.GetField("IsFetching").GetValue(custom),
                "partially entered custom credentials do not trigger background fetching");
            configType.GetField("Key").SetValue(custom, "");
            refresh.Invoke(null, new[] { custom, (object)true });
            using (var refreshTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
                while ((bool)configType.GetField("IsFetching").GetValue(custom)) await Task.Delay(10, refreshTimeout.Token);
            var fetched = (System.Collections.IEnumerable)configType.GetField("FetchedModels").GetValue(custom);
            Check(fetched.Cast<string>().Contains("fixture-model") && server.Requests.Any(r => r.Path == "/v1/models") &&
                server.Requests.Count(r => r.Path == "/models") == 2,
                "explicit keyless refresh still fetches models and retries the original URL after a v1 404");
            configType.GetField("FetchRetryCount").SetValue(custom, 2);
            configType.GetField("NextModelFetchRetryUtcTicks").SetValue(custom, DateTime.UtcNow.AddSeconds(-1).Ticks);
            api.GetMethod("ScheduleModelFetchRetry", Static).Invoke(null,
                new[] { custom, configType.GetField("FetchGeneration").GetValue(custom) });
            Check((int)configType.GetField("FetchRetryCount").GetValue(custom) == 0 &&
                (long)configType.GetField("NextModelFetchRetryUtcTicks").GetValue(custom) == 0L &&
                !(bool)api.GetMethod("CanAutoRetryModelFetch", Static).Invoke(null, new[] { custom, (object)"fixture-fingerprint" }),
                "custom model refresh does not queue background retries after the explicit operation ends");

            response = await Post(server.Url + "/close?key=synthetic-query", "DeepSeek", "synthetic-transport");
            Check(!Success(response) && Code(response) == 0 && !Error(response).Contains("synthetic-query") &&
                !Error(response).Contains("synthetic-transport"), "transport failures return HTTP 0 without exposing credentials");
            response = await Raw(server.Url + "/cloud-delete", "DELETE", null,
                new Dictionary<string, string> { { "X-Admin-Token", "synthetic-admin" } }, 5);
            request = server.Requests.Single(r => r.Path == "/cloud-delete");
            Check(Success(response) && request.Method == "DELETE" && request.Headers["X-Admin-Token"] == "synthetic-admin",
                "cloud DELETE retains its method and admin header");
            response = await Raw(server.Url + "/cloud-upload", "POST", Encoding.UTF8.GetBytes(Payload), null, 5);
            Check(Success(response) && server.Requests.Single(r => r.Path == "/cloud-upload").Body.SequenceEqual(Encoding.UTF8.GetBytes(Payload)), "cloud POST preserves serialized payload bytes");
            await TestThinkingPolicies(mod, server);
            await TestPlayerPreferences(mod, server);
            Type cloudClient = mod.GetType("AutoTranslator_Core.AutoTranslatorCloudClient", true);
            FieldInfo cloudUrl = cloudClient.GetField("CloudApiBaseUrl", Static);
            object originalCloudUrl = cloudUrl.GetValue(null);
            try
            {
                // Override only this isolated test process, never player settings or production URLs on disk.
                cloudUrl.SetValue(null, server.Url);
                object deleted = await Invoke(cloudClient, "DeleteCloudRecordAsync", "test.package", "ChineseTraditional", "fixture-record", "synthetic-admin-route");
                Check((bool)deleted && server.Requests.Any(r => r.Method == "DELETE" &&
                    r.Path == "/delete/test.package/ChineseTraditional?recordId=fixture-record" && r.Headers["X-Admin-Token"] == "synthetic-admin-route"),
                    "actual cloud delete route completes without a dispatcher");
                object corrections = await Invoke(cloudClient, "FetchAppliedCorrectionsAsync", "test.package", "ChineseTraditional");
                Check(corrections != null && (int)corrections.GetType().GetProperty("Count").GetValue(corrections) == 0,
                    "actual applied-corrections GET route parses its JSON response");
                object submission = Activator.CreateInstance(mod.GetType("AutoTranslator_Core.TranslationCorrectionSubmission", true));
                object submitted = await Invoke(cloudClient, "SubmitCorrectionAsync", submission, "synthetic-admin-payload");
                Check((bool)submitted && server.Requests.Any(r => r.Path == "/corrections/submit" &&
                    r.Method == "POST" && Encoding.UTF8.GetString(r.Body).Contains("synthetic-admin-payload")),
                    "actual correction submission preserves its JSON payload and completes without a dispatcher");
            }
            finally { cloudUrl.SetValue(null, originalCloudUrl); }
            object active = transport.GetField("ActiveTranslations", Static).GetValue(null);
            Check((int)active.GetType().GetProperty("Count").GetValue(active) == 0, "completed and aborted requests release their active registrations");
            Check((int)mod.GetType("AutoTranslator_Core.AutoTranslatorPerf", true).GetField("_activeApiRequests", Static).GetValue(null) == 0,
                "request performance counters remain balanced after success, failure and cancellation");
            Check(mod.GetType("AutoTranslator_Core.ATC_Dispatcher", true).GetField("_instance", Static).GetValue(null) == null,
                "API requests finish without a game main-thread dispatcher or Coroutine pump");
            Console.WriteLine("PASS: " + passed + " native transport checks; loopback only; no real credentials or cloud writes.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task<object> Invoke(Type type, string method, params object[] args)
    {
        Task task = (Task)type.GetMethod(method, Static).Invoke(null, args);
        await task.WaitAsync(TimeSpan.FromSeconds(12));
        return task.GetType().GetProperty("Result").GetValue(task);
    }
    private static Task<object> Post(string url, string name, string key, Func<bool> cancelled = null,
        int seconds = 5, bool player2 = false, string gameId = null) =>
        Invoke(api, "SendJsonRequestAttemptAsync", url, Payload, key, Enum.Parse(provider, name), seconds, cancelled, gameId, player2);
    private static Task<object> Models(string url, string name, string key) =>
        Invoke(api, "SendModelsRequestAsync", url, Enum.Parse(provider, name), key, null);
    private static Task<object> Raw(string url, string method, byte[] body, Dictionary<string, string> headers,
        int seconds, Func<bool> cancelled = null) => Invoke(transport, "SendAsync", url, method, body, headers, seconds, cancelled, false, false);
    private static object Field(object value, string field) => value.GetType().GetField(field).GetValue(value);
    private static bool Success(object value) => (bool)Field(value, "IsSuccess");
    private static long Code(object value) => (long)Field(value, "HttpCode");
    private static string Text(object value) => (string)Field(value, "ResponseBody");
    private static string Error(object value) => (string)Field(value, "ErrorText");
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception("FAIL: " + name);
        passed++; Console.WriteLine("PASS: " + name);
    }

    private sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);
    private sealed class FixtureServer : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly ConcurrentBag<Task> workers = new ConcurrentBag<Task>();
        private readonly Task accept;
        internal readonly ConcurrentQueue<Request> Requests = new ConcurrentQueue<Request>();
        internal string Url { get; }
        internal FixtureServer()
        {
            listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            accept = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                        workers.Add(Handle(client));
                    }
                }
                catch (OperationCanceledException) { }
                catch (SocketException) when (stop.IsCancellationRequested) { }
            });
        }
        internal async Task WaitFor(string path)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (!Requests.Any(r => r.Path == path)) await Task.Delay(10, timeout.Token);
        }
        private async Task Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    var bytes = new List<byte>(); var one = new byte[1];
                    while (bytes.Count < 32768)
                    {
                        if (await stream.ReadAsync(one, stop.Token) == 0) return;
                        bytes.Add(one[0]);
                        if (bytes.Count >= 4 && Encoding.ASCII.GetString(bytes.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                    }
                    string[] lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
                    string[] first = lines[0].Split(' ');
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string line in lines.Skip(1).Where(l => l.Contains(':')))
                    {
                        int colon = line.IndexOf(':'); headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
                    }
                    byte[] body = new byte[headers.TryGetValue("Content-Length", out string length) ? int.Parse(length) : 0];
                    await stream.ReadExactlyAsync(body, stop.Token);
                    Requests.Enqueue(new Request(first[0], first[1], headers, body));
                    string path = first[1];
                    if (path.StartsWith("/close")) return;
                    if (new[] { "/cancel", "/abort-one", "/abort-two", "/cloud-wait" }.Contains(path))
                    { await Task.Delay(Timeout.Infinite, stop.Token); return; }
                    if (path == "/slow-headers") await Task.Delay(3000, stop.Token);
                    int code = path.StartsWith("/status/") ? int.Parse(path.Split('/').Last()) : path == "/redirect" ? 302 : path == "/v1/models" ? 404 : 200;
                    byte[] data = Encoding.UTF8.GetBytes(code >= 400 ? "{\"error\":{\"message\":\"fixture error\"}}" :
                        path.StartsWith("/corrections/applied/") ? "{\"Corrections\":[]}" :
                        path == "/models" ? "{\"data\":[{\"id\":\"fixture-model\",\"reasoning\":{\"mandatory\":false,\"supported_efforts\":[\"low\",\"none\"]}}]}" : Payload);
                    if (code == 200 && (path.Contains("/chat/completions") || path.Contains(":generateContent")))
                        data = ThinkingResponse(path, body);
                    string extra = path == "/redirect" ? "Location: " + Url + "/redirect-target\r\n" : "";
                    if (path == "/gzip")
                    {
                        using var compressed = new MemoryStream();
                        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true)) gzip.Write(data);
                        data = compressed.ToArray(); extra += "Content-Encoding: gzip\r\n";
                    }
                    string type = path == "/html" ? "text/html" : "application/json";
                    string response = "HTTP/1.1 " + code + " Fixture\r\nContent-Type: " + type + "; charset=gbk\r\n" +
                        "Content-Length: " + data.Length + "\r\nConnection: close\r\n" + extra + "\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
                    if (path == "/slow-body") await Task.Delay(3000, stop.Token);
                    await stream.WriteAsync(data, stop.Token);
                }
                catch (IOException) { } // Expected when cancellation closes a client socket.
                catch (OperationCanceledException) { }
            }
        }
        public void Dispose()
        {
            stop.Cancel(); listener.Stop();
            accept.GetAwaiter().GetResult(); Task.WhenAll(workers.ToArray()).GetAwaiter().GetResult();
            stop.Dispose();
        }
    }
}
