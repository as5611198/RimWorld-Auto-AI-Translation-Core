using System.Reflection;
using AutoTranslator_Core;
using Verse;
using HarmonyLib;

internal static partial class Suite
{
    private static readonly string retainedNativeCaption = "Minimum harvest day";
    static void TestNativeUiLookup()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var lookup = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.NativeUiTranslationLookup");
        var pump = lookup.GetMethod("Pump", flags);
        var reset = lookup.GetMethod("Invalidate", flags);
        var resolve = lookup.GetMethod("TryResolve", flags);
        var preparing = lookup.GetProperty("Preparing", flags);
        var oldLanguage = LanguageDatabase.activeLanguage; var oldDefault = LanguageDatabase.defaultLanguage;
        var oldSettings = AutoTranslatorMod.Settings;
        object Call(string method) => lookup.GetMethod(method, flags).Invoke(null, null);
        bool Hit(string text, out string target)
        {
            object[] args = { text, null }; bool found = (bool)resolve.Invoke(null, args); target = (string)args[1]; return found;
        }
        void Drain()
        {
            var until = DateTime.UtcNow.AddSeconds(15);
            while ((bool)preparing.GetValue(null))
            {
                pump.Invoke(null, null);
                if (DateTime.UtcNow > until) throw new TimeoutException("Existing UI translation index did not settle");
                Thread.Sleep(1);
            }
        }
        LoadedLanguage Language(params (string key, string value)[] entries)
        {
            var value = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
            value.keyedReplacements = entries.ToDictionary(e => e.key, e => new LoadedLanguage.KeyedReplacement { key = e.key, value = e.value });
            return value;
        }
        void Set(string key, string value) => LanguageDatabase.activeLanguage.keyedReplacements[key] = new LoadedLanguage.KeyedReplacement { key = key, value = value };
        using (var budget = (IDisposable)typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.TranslationFrameBudget")
            .GetMethod("UnlimitedForSynchronousFixture", flags).Invoke(null, null))
        try
        {
            var renderCore = typeof(UIInterceptor).GetMethod("TryResolveRenderTextCore", flags);
            var renderIl = PatchProcessor.GetOriginalInstructions(renderCore).ToList();
            int nativeCall = renderIl.FindIndex(i => i.operand is MethodInfo m && m.DeclaringType == lookup && m.Name == "TryResolve");
            int classifierCall = renderIl.FindIndex(i => i.operand is MethodInfo m && m.Name == "QueueForClassification");
            int decisionCacheRead = renderIl.FindIndex(i => i.operand is FieldInfo f && f.Name == "RenderDecisionCache");
            int numericGuard = renderIl.FindIndex(i => i.operand is MethodInfo m && m.Name == "IsNumericDisplayText");
            Check(numericGuard >= 0 && numericGuard < nativeCall && numericGuard < decisionCacheRead, "numeric render guard runs before native mappings and old render decisions");
            var cacheIl = PatchProcessor.GetOriginalInstructions(typeof(UIInterceptor).GetMethod("TryGetCachedTranslationKnownSafe", flags | BindingFlags.Public)).ToList();
            int cacheGuard = cacheIl.FindIndex(i => i.operand is MethodInfo m && m.Name == "IsNumericDisplayText");
            Check(cacheGuard >= 0 && cacheGuard < cacheIl.FindIndex(i => i.operand is FieldInfo f && f.Name == "Cache"), "numeric cache guard runs before historical cached translations");
            var safetyIl = PatchProcessor.GetOriginalInstructions(typeof(UIInterceptor).GetMethod("ShouldBypassUIPatchText", flags)).ToList();
            int safetyGuard = safetyIl.FindIndex(i => i.operand is MethodInfo m && m.Name == "IsNumericDisplayText");
            Check(safetyGuard >= 0 && safetyGuard < safetyIl.FindIndex(i => i.operand is FieldInfo f && f.Name == "EnableUIErrorLogInterception"), "numeric bypass remains active when error-log text interception is enabled");
            Check(nativeCall >= 0 && nativeCall < decisionCacheRead && nativeCall < classifierCall, "production render path consults native reuse before stale render decisions and AI classification");
            Check(renderIl.FindIndex(i => i.operand is MethodInfo m && m.DeclaringType == lookup && m.Name == "get_Preparing") < classifierCall &&
                renderIl.Any(i => i.operand is MethodInfo m && m.DeclaringType == lookup && m.Name == "get_PendingMatch"),
                "production render path defers new AI work while native indexing or a bounded template match is pending");
            AutoTranslatorMod.Settings = new AutoTranslatorSettings { EnableUIInterceptor = true };
            LanguageDatabase.defaultLanguage = Language(
                ("SmartFarming.Inspector.MinHarvestDay", retainedNativeCaption), ("LevelUp.FormattingLabel", "Formatting"),
                ("Fluffy.MusicManager.Columns.Name.Label", "Track name"), ("ASF.Empty", "Empty container"),
                ("Situation.Progress", "Situation progress"), ("One.Shared", "Shared caption"), ("Two.Shared", "Shared caption"),
                ("One.Missing", "Missing caption"), ("Two.Missing", "Missing caption"),
                ("Harvest.Count", "Harvest fully grown: {0} plants"), ("Named.Pawn", "Colonist {PAWN} reached level {0}"),
                ("Repeated", "Items {0} compare with {0}"), ("Escaped", "Value {{fixed}} equals {0}"),
                ("Formatted", "Mass in storage: {0:0.00} kg"),
                ("Conflict.One", "Quantity available: {0}"), ("Conflict.Two", "Quantity available: {0}"),
                ("Invalid.Placeholder", "Stored quantity: {0}"));
            foreach (string key in new[] { "Rich.One", "Rich.Two", "Rich.Plain" })
                LanguageDatabase.defaultLanguage.keyedReplacements[key] = new LoadedLanguage.KeyedReplacement { key = key, value = key == "Rich.Plain" ? "Wrapped shared" : "<b>Wrapped shared</b>" };
            LanguageDatabase.activeLanguage = Language(
                ("SmartFarming.Inspector.MinHarvestDay", "最早收穫日"), ("LevelUp.FormattingLabel", "格式設定"),
                ("Fluffy.MusicManager.Columns.Name.Label", "曲目名稱"), ("ASF.Empty", "空容器"), ("Situation.Progress", "狀況進度"),
                ("One.Shared", "第一個意思"), ("Two.Shared", "第二個意思"), ("One.Missing", "僅第一鍵有翻譯"),
                ("Harvest.Count", "收穫完全成熟植物：{0} 株"), ("Named.Pawn", "殖民者 {PAWN} 已達 {0} 級"),
                ("Repeated", "物品 {0} 與 {0} 比較"), ("Escaped", "值 {{fixed}} 等於 {0}"),
                ("Formatted", "倉庫重量：{0:0.00} 公斤"),
                ("Conflict.One", "可用數量：{0}"), ("Conflict.Two", "另一種數量：{0}"),
                ("Invalid.Placeholder", "庫存：{1}"));
            // Real upstream source/translation pairs from RimHonkai. These
            // belong to a dedicated localization key, not arbitrary UI values.
            foreach (var numeric in new[] { ("3", "三"), ("7", "七") })
            {
                string key = "RimHonkai.HerrscherOrdinal" + numeric.Item1;
                LanguageDatabase.defaultLanguage.keyedReplacements[key] = new LoadedLanguage.KeyedReplacement { key = key, value = numeric.Item1 };
                Set(key, numeric.Item2);
            }
            Set("Rich.One", "第一種粗體意思"); Set("Rich.Two", "第二種粗體意思"); Set("Rich.Plain", "純文字意思");
            LanguageDatabase.defaultLanguage.keyedReplacements["Placeholder.Target"] = new LoadedLanguage.KeyedReplacement { key = "Placeholder.Target", value = "Native placeholder caption" };
            LanguageDatabase.activeLanguage.keyedReplacements["Placeholder.Target"] = new LoadedLanguage.KeyedReplacement { key = "Placeholder.Target", value = "not a real translation", isPlaceholder = true };
            LanguageDatabase.defaultLanguage.keyedReplacements["Placeholder.Source"] = new LoadedLanguage.KeyedReplacement { key = "Placeholder.Source", value = "not a real English source", isPlaceholder = true };
            Set("Placeholder.Source", "有效譯文");
            reset.Invoke(null, null);
            Check(!Hit(retainedNativeCaption, out _), "native UI lookup never exposes a partial snapshot");
            Drain();
            Check(!Hit("3", out _) && !Hit("7", out _), "real RimHonkai ordinal translations cannot replace unrelated numeric UI values");
            Check(!Hit("<color=#00ff00><b>7</b></color>", out _), "rich formatting cannot bypass numeric display protection");
            var published = lookup.GetField("published", flags).GetValue(null);
            var plainLookup = (System.Collections.IDictionary)published.GetType().GetField("Lookup", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(published);
            Check(!plainLookup.Contains("3") && !plainLookup.Contains("7"), "numeric translations are excluded at common lookup construction, not merely hidden after lookup");
            Check(LanguageDatabase.activeLanguage.keyedReplacements["RimHonkai.HerrscherOrdinal7"].value == "七", "dedicated mod localization keys retain their authored ordinal wording");
            Check(Hit(retainedNativeCaption, out string translated) && translated == "最早收穫日", "readonly cached caption reuses existing Keyed translation at display time");
            Check(retainedNativeCaption == "Minimum harvest day", "readonly source field is not mutated");
            foreach (string source in new[] { "Formatting", "Track name", "Empty container", "Situation progress" })
                Check(Hit(source, out _), "common display repair covers static/instance cache shapes: " + source);
            Check(!Hit("Shared caption", out _), "conflicting translations for identical English captions are excluded");
            Check(!Hit("Missing caption", out _), "untranslated homonyms prevent borrowing another key's translation");
            Check(!Hit("Native placeholder caption", out _), "native TODO/placeholder target entries are not reused as translations");
            Check(!Hit("not a real English source", out _), "native placeholder English entries are not treated as source captions");
            Check(!Hit("Prefix Minimum harvest day suffix", out _), "ordinary substrings are never replaced");
            Check(!Hit("Unknown English caption", out _), "missing native translations do not create a translation");
            Check(Hit("<color=#aabbcc><b>Minimum harvest day</b></color>", out translated) && translated == "<color=#aabbcc><b>最早收穫日</b></color>", "native display lookup preserves nested presentation tags");
            Check(!Hit("<b>Minimum harvest day</i>", out _), "unbalanced presentation tags are not rewritten");
            Check(!Hit("<b>Wrapped shared</b>", out _), "presentation unwrapping cannot bypass a known whole-caption ambiguity");
            Check(Hit("Harvest fully grown: 12 plants", out translated) && translated == "收穫完全成熟植物：12 株", "formatted cached labels preserve displayed quantities without another API request");
            Check(Hit("Colonist Ada reached level 7", out translated) && translated == "殖民者 Ada 已達 7 級", "named and numbered native placeholders preserve pawn text");
            Check(Hit("Items 3 compare with 3", out translated) && translated == "物品 3 與 3 比較", "repeated native placeholder values agree");
            Check(!Hit("Items 3 compare with 4", out _), "inconsistent repeated placeholders cannot match a template");
            Check(Hit("Value {fixed} equals 9", out translated) && translated == "值 {fixed} 等於 9", "composite-format escaped braces retain their displayed meaning");
            Check(Hit("Mass in storage: 1,234.50 kg", out translated) && translated == "倉庫重量：1,234.50 公斤", "formatted numeric captures are retained verbatim without culture conversion");
            Check(!Hit("Quantity available: 10", out _), "conflicting formatted translations are excluded");
            Check(!Hit("Stored quantity: 10", out _), "mismatched source and target placeholder identities are excluded");
            Check(!Hit("Stored quantity: {0}", out _), "unexpanded labels cannot introduce mismatched native placeholders either");
            Check(!Hit("", out _) && !Hit(null, out _), "empty cached labels remain unchanged");

            Set("SmartFarming.Inspector.MinHarvestDay", "最早可收穫日"); Call("Invalidate");
            Check(!Hit(retainedNativeCaption, out _), "same-count translation edits invalidate the previous display snapshot immediately");
            Drain();
            Check(Hit(retainedNativeCaption, out translated) && translated == "最早可收穫日", "hot corrections update readonly caption displays");
            Check(Hit("最早收穫日", out translated) && translated == "最早可收穫日", "instances retaining the previous translated caption also update");
            Set("SmartFarming.Inspector.MinHarvestDay", "最新收穫日"); Call("Invalidate"); Drain();
            Check(Hit("最早收穫日", out translated) && translated == "最新收穫日", "previous display aliases survive consecutive hot corrections");
            LanguageDatabase.activeLanguage = LanguageDatabase.defaultLanguage; Call("Invalidate"); Drain();
            Check(Hit("最早收穫日", out translated) && translated == retainedNativeCaption, "returning to English repairs retained translated captions");
            Check(!Hit(retainedNativeCaption, out _), "English source labels are not translated again in English mode");

            var oldEntries = LanguageDatabase.defaultLanguage.keyedReplacements;
            LanguageDatabase.defaultLanguage.keyedReplacements = new Dictionary<string, LoadedLanguage.KeyedReplacement>(oldEntries);
            Check(!Hit("最新收穫日", out _), "same-count dictionary replacement revokes a previously published snapshot");
            Drain();
            LanguageDatabase.defaultLanguage = Language(("New.Same", "Minimum harvest day"));
            LanguageDatabase.activeLanguage = Language(("New.Same", "新環境譯文")); Call("Invalidate"); Drain();
            Check(Hit(retainedNativeCaption, out translated) && translated == "新環境譯文", "a fresh native language environment rebuilds its own lookup");
            Check(!Hit("最早收穫日", out _), "a different English language instance drops obsolete aliases");

            var manySource = Enumerable.Range(0, 3000).Select(i => ("Fixture." + i, "Fixture source " + i)).ToArray();
            LanguageDatabase.defaultLanguage = Language(manySource);
            LanguageDatabase.activeLanguage = Language(manySource.Select(e => (e.Item1, "測試譯文 " + e.Item1)).ToArray());
            Call("Invalidate"); pump.Invoke(null, null);
            var pending = lookup.GetField("capture", flags).GetValue(null);
            var data = pending.GetType().GetField("Data", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(pending);
            var rows = (System.Collections.IDictionary)data.GetType().GetField("Rows", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(data);
            Check(rows.Count <= 1024 && (bool)preparing.GetValue(null), "native dictionary snapshots are bounded to 1024 rows per pump");
            LanguageDatabase.activeLanguage = Language(("Fixture.0", "較新的環境")); Call("Invalidate"); Drain();
            Check(Hit("Fixture source 0", out translated) && translated == "較新的環境", "a superseding language invalidates work captured for the old environment");
            Check(!Hit("Fixture source 2999", out _), "cancelled/old snapshots cannot publish translations into the new environment");
        }
        finally
        {
            LanguageDatabase.activeLanguage = oldLanguage; LanguageDatabase.defaultLanguage = oldDefault;
            AutoTranslatorMod.Settings = oldSettings; reset.Invoke(null, null);
        }
    }
}
