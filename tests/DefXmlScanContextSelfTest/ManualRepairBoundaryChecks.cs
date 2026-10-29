using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;

internal static partial class Suite
{
    static int implicitBulkRepairCalls;
    static bool CaptureBulkRepairStart() { implicitBulkRepairCalls++; return false; }

    static void TestManualRepairBoundary(string languageRoot)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var scanner = typeof(AutoTranslatorScanner);
        var failures = new List<Exception>();
        void Case(Action check) { try { check(); } catch (Exception ex) { failures.Add(ex); Console.WriteLine(ex.GetBaseException().Message); } }

        Case(() => {
            var hooks = new Harmony("atc.manual-repair-boundary.initialization");
            var capture = new HarmonyMethod(typeof(Suite).GetMethod(nameof(CaptureBulkRepairStart), flags));
            // Observe both execution and the former deferred entry point, so the
            // test detects scheduling without allowing a delayed task to escape.
            hooks.Patch(scanner.GetMethod("RunPackMaintenance", flags), prefix: capture);
            var queue = scanner.GetMethod("QueueBackgroundPackMaintenanceOnce", flags);
            if (queue != null) hooks.Patch(queue, prefix: capture);
            hooks.Patch(typeof(AutoTranslatorLegacyRepairer).GetMethod("QueueBackgroundRepairOnce"), prefix: capture);
            try {
                implicitBulkRepairCalls = 0;
                AutoTranslatorScanner.EnsurePackInitialized();
                AutoTranslatorScanner.EnsurePackInitialized(runFullMaintenance: false);
                Check(implicitBulkRepairCalls == 0,
                    "normal pack initialization neither runs nor schedules bulk or legacy repair before player consent");
                AutoTranslatorScanner.EnsurePackInitialized(runFullMaintenance: true);
                Check(implicitBulkRepairCalls == 1, "explicit full maintenance still reaches its requested repair entry point");
            } finally { hooks.UnpatchAll(hooks.Id); }
        });
        Case(() => {
            string file = Path.Combine(languageRoot, "DefInjected", nameof(RepairFixtureDef), "manual_boundary_AutoTranslated.xml");
            string original = "<LanguageData><RepairFixture.oldMenuText>保留到玩家確認的付費譯文</RepairFixture.oldMenuText>" +
                "<RepairFixture.category>保留到玩家確認的錯誤欄位</RepairFixture.category></LanguageData>";
            File.WriteAllText(file, original);
            byte[] bytes = File.ReadAllBytes(file);
            DateTime written = File.GetLastWriteTimeUtc(file);
            try {
                scanner.GetMethod("ResetMemoryDropStamps", flags).Invoke(null, null);
                var build = scanner.GetMethods(flags).Single(m => m.Name == "BuildMemoryDropPayload" && m.GetParameters().Length == 1);
                object payload = build.Invoke(null, new object[] { false });
                Check(payload != null && bytes.SequenceEqual(File.ReadAllBytes(file)) && written == File.GetLastWriteTimeUtc(file),
                    "ordinary full memory preparation leaves existing aliases and invalid source XML byte-for-byte until manual repair");
            } finally { File.Delete(file); AutoTranslatorScanner.NotifyTranslationFileChanged(file); }
        });
        if (failures.Count > 0) throw new AggregateException("Manual repair boundary regressions", failures);
    }
}
