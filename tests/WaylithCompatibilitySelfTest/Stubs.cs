// Only the application services are substituted. Patch implementation, Unity GUI,
// Harmony, the downloader, and persistent identity code are the production code.
namespace AutoTranslator_Core
{
    public static class DeleteTranslationWindow { }
    public static class AutoTranslatorMod
    {
        public static SettingsStub Settings = new SettingsStub();
    }
    public sealed class SettingsStub
    {
        public bool EnableUIInterceptor = true;
        public bool EnableUIErrorLogInterception;
        public bool ShowOriginalUI;
    }
    public static class UIInterceptor
    {
        public static int Calls;
        public static bool Throw;
        public static bool TryResolveRenderText(string text, out string translated)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("intentional translation failure");
            translated = text == "Source label" ? "翻譯文字" : text + "-twice";
            return true;
        }
    }
    public static partial class AutoTranslatorCloudClient
    {
        private sealed class CloudDownloadAttemptResult
        {
            public bool Success;
            public long StatusCode;
            public string Error, FilePath, ServedRecordId, FallbackReason;
        }
        private static void TryDeleteCloudTempFile(string path) { if (File.Exists(path)) File.Delete(path); }
        public static async Task<(bool success, long status, string id, string reason)> Download(string url, string path, int timeout = 3)
        {
            var result = await WaylithArchiveDownloader.DownloadAsync(url, path, timeout);
            return (result.Success, result.StatusCode, result.ServedRecordId, result.FallbackReason);
        }
    }
}
