using AutoTranslator_Core;
using HarmonyLib;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using UnityEngine;

internal static class Bootstrap
{
    private static int Main()
    {
        string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        Assembly.LoadFrom(Path.Combine(directory, "0Harmony.dll"));
        Assembly.LoadFrom(Path.Combine(directory, "Newtonsoft.Json.dll"));
        Assembly.Load("Assembly-CSharp");
        return Program.Run().GetAwaiter().GetResult();
    }
}

internal static class Program
{
    private static int passed;
    private static string drawn;
    private static bool throwDraw;
    private static readonly Rect Rect = new Rect(0, 0, 100, 30);
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        passed++; Console.WriteLine("PASS " + name);
    }

    internal static async Task<int> Run()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "ATC-Waylith-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Console.WriteLine("Runtime: " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            var harmony = new Harmony("atc.waylith-selftest");
            harmony.PatchAll(typeof(Patch_GUI_Label_String).Assembly);
            var stringLabel = AccessTools.Method(typeof(GUI), nameof(GUI.Label), new[] { typeof(Rect), typeof(string), typeof(GUIStyle) });
            harmony.Patch(stringLabel, postfix: new HarmonyMethod(typeof(Program), nameof(ObserveString)));

            UIInterceptor.Calls = 0;
            GUI.Label(Rect, "Source label", null);
            Check(drawn == "翻譯文字" && UIInterceptor.Calls == 1, "direct string label is translated exactly once");

            UIInterceptor.Calls = 0;
            var content = new GUIContent("Source label");
            GUI.Label(Rect, content, null);
            Check(drawn == "翻譯文字" && UIInterceptor.Calls == 1, "GUIContent to string delegation is translated once");
            Check(content.text == "Source label", "caller-owned GUIContent is unchanged");

            UIInterceptor.Calls = 0;
            string text = "Source label";
            Patch_Widgets_Label_String.Prefix(Rect, ref text, out bool state);
            try { GUI.Label(Rect, text, null); }
            finally { Patch_Widgets_Label_String.Finalizer(null, state); }
            Check(drawn == "翻譯文字" && UIInterceptor.Calls == 1, "Widgets to GUI nesting does not translate twice");

            AutoTranslatorMod.Settings.EnableUIInterceptor = false;
            UIInterceptor.Calls = 0;
            GUI.Label(Rect, "Source label", null);
            Check(drawn == "Source label" && UIInterceptor.Calls == 0, "disabled interceptor leaves labels untouched");
            AutoTranslatorMod.Settings.EnableUIInterceptor = true;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            GUI.Label(Rect, "Source label", null);
            Check(drawn == "Source label" && UIInterceptor.Calls == 0, "log bypass leaves labels untouched");
            Patch_GUI_Label_GUIContent.BypassInterceptor = false;

            throwDraw = true;
            try { GUI.Label(Rect, new GUIContent("Source label"), null); } catch (InvalidOperationException) { }
            throwDraw = false;
            Check(!UIInterceptorPatchGuard.IsNestedLabelCall, "nested guard is restored after rendering exception");
            UIInterceptor.Throw = true;
            try { GUI.Label(Rect, "Source label", null); } catch (InvalidOperationException) { }
            UIInterceptor.Throw = false;
            Check(!UIInterceptorPatchGuard.IsNestedLabelCall, "guard is restored after translation exception");
            harmony.UnpatchAll(harmony.Id);

            string identityPath = Path.Combine(temporary, "identity.txt");
            string first = CloudClientIdentity.GetOrCreate(identityPath);
            Check(Guid.TryParseExact(first, "N", out _) && CloudClientIdentity.GetOrCreate(identityPath) == first,
                "cloud identity survives reload");
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => CloudClientIdentity.GetOrCreate(identityPath + ".race"))));
            Check(concurrent.Distinct().Count() == 1, "concurrent identity creation keeps one identity");
            Check(!Directory.GetFiles(temporary, "*.tmp").Any(), "identity creation leaves no temporary files");

            byte[] zip = Encoding.UTF8.GetBytes("PK\x03\x04test archive content");
            await WithServer(async stream => {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {zip.Length}\r\nX-ATC-Record-Id: record-17\r\nX-ATC-Record-Fallback: replacement\r\nConnection: close\r\n\r\n"));
                await stream.WriteAsync(zip);
            }, async url => {
                string path = Path.Combine(temporary, "ok.zip");
                var result = await AutoTranslatorCloudClient.Download(url, path);
                Check(result.success && File.ReadAllBytes(path).SequenceEqual(zip), "archive bytes stream to disk unchanged");
                Check(result.status == 200 && result.id == "record-17" && result.reason == "replacement", "download recovery headers survive");
            });
            await WithServer(stream => stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")).AsTask(), async url => {
                string path = Path.Combine(temporary, "missing.zip");File.WriteAllText(path, "stale");
                var result = await AutoTranslatorCloudClient.Download(url, path);
                Check(!result.success && result.status == 404 && !File.Exists(path), "HTTP failure removes stale archive");
            });
            await WithServer(async stream => {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\npartial"));
            }, async url => {
                string path = Path.Combine(temporary, "partial.zip");
                var result = await AutoTranslatorCloudClient.Download(url, path);
                Check(!result.success && !File.Exists(path), "truncated response removes partial archive");
            });
            await WithServer(async stream => {
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\npartial"));
                await Task.Delay(1600);
            }, async url => {
                string path = Path.Combine(temporary, "timeout.zip");
                var result = await AutoTranslatorCloudClient.Download(url, path, 1);
                Check(!result.success && !File.Exists(path), "timeout removes partial archive");
            });
            Console.WriteLine($"PASS: {passed} Waylith compatibility checks");return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    public static void ObserveString(string __1)
    {
        drawn = __1;
        if (throwDraw) throw new InvalidOperationException("intentional rendering failure");
    }

    private static async Task WithServer(Func<NetworkStream, Task> serve, Func<string, Task> request)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () => {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            await serve(stream);
        });
        await request("http://127.0.0.1:" + port + "/archive");await server;
    }
}
