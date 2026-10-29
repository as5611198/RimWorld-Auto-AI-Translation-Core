using System.Reflection;
using AutoTranslator_Core;
using Verse;

internal static partial class Suite
{
    private sealed class NativeCorpusRow { public string PackageId, Key, Source; }
    static void TestNativeUiCorpus(string path)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var type = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.NativeUiTranslationLookup");
        var reset = type.GetMethod("Invalidate", flags); var pump = type.GetMethod("Pump", flags); var resolve = type.GetMethod("TryResolve", flags);
        var preparing = type.GetProperty("Preparing", flags);
        var numeric = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), typeof(AutoTranslatorScanner).Assembly
            .GetType("AutoTranslator_Core.UIDynamicNumberTemplate").GetMethod("IsNumericDisplayText", flags));
        var previousDefault = LanguageDatabase.defaultLanguage; var previousActive = LanguageDatabase.activeLanguage;
        var previousSettings = AutoTranslatorMod.Settings;
        var rows = Newtonsoft.Json.JsonConvert.DeserializeObject<List<NativeCorpusRow>>(File.ReadAllText(path));
        var source = rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Last().Source);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            AutoTranslatorMod.Settings = new AutoTranslatorSettings { EnableUIInterceptor = true };
            var language = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
            var fallback = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
            fallback.keyedReplacements = source.ToDictionary(p => p.Key, p => new LoadedLanguage.KeyedReplacement { key = p.Key, value = p.Value });
            language.keyedReplacements = source.ToDictionary(p => p.Key, p => new LoadedLanguage.KeyedReplacement { key = p.Key, value = "測試譯文：" + p.Value });
            LanguageDatabase.defaultLanguage = fallback; LanguageDatabase.activeLanguage = language; reset.Invoke(null, null);
            using (var budget = (IDisposable)typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.TranslationFrameBudget")
                .GetMethod("UnlimitedForSynchronousFixture", flags).Invoke(null, null))
                while ((bool)preparing.GetValue(null))
                {
                    pump.Invoke(null, null);
                    if (watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("Real source corpus index did not settle");
                    Thread.Sleep(1);
                }
            foreach (var group in rows.GroupBy(r => r.PackageId))
            {
                int checkedRows = 0;
                foreach (var row in group)
                {
                    if (source[row.Key] != row.Source) continue; // Native global key override.
                    object[] args = { row.Source, null };
                    if (numeric(row.Source))
                    {
                        if ((bool)resolve.Invoke(null, args)) throw new Exception("Numeric corpus value was translated: " + group.Key + "/" + row.Key);
                        continue;
                    }
                    if (!(bool)resolve.Invoke(null, args) || (string)args[1] != "測試譯文：" + row.Source)
                        throw new Exception("Real-source lookup failed: " + group.Key + "/" + row.Key);
                    checkedRows++;
                }
                Check(checkedRows > 0, "real English Keyed captions covered by common lookup: " + group.Key + " (" + checkedRows + " rows; synthetic target)");
            }
            Console.WriteLine("Corpus: " + rows.Count + " source rows / " + source.Count + " global keys; " + watch.ElapsedMilliseconds + " ms including paced snapshot, build and checks; no UI/API/game execution.");
        }
        finally
        {
            LanguageDatabase.defaultLanguage = previousDefault; LanguageDatabase.activeLanguage = previousActive;
            AutoTranslatorMod.Settings = previousSettings; reset.Invoke(null, null);
        }
    }
}
