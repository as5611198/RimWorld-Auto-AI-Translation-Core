using System.Reflection;
using AutoTranslator_Core;

internal static class RepairRunChecks
{
    public static void Run()
    {
        var type = Assembly.GetExecutingAssembly().GetType("AutoTranslator_Core.TranslationRepairRun");
        if (type == null) throw new Exception("FAIL: current-run repair verification model is missing");
        dynamic run = Activator.CreateInstance(type);
        var record = new TranslationRepairRecord { SourceFile = "pack/DefInjected/Fixture/paid.xml", DefType = "Fixture",
            OriginalKey = "Def.oldLabel", NewKey = "Def.label", Translation = "付費譯文", Status = "已沿用修復" };
        run.Observe(record);
        run.Observe(record);
        if (run.Total != 1 || run.Verified != 0 || run.Waiting != 1)
            throw new Exception("FAIL: a file repair is not yet a verified runtime repair, and repeated observations count once");
        run.Verify(record, true, false);
        if (run.Verified != 1 || run.Waiting != 0) throw new Exception("FAIL: successful reinjection must update the existing issue immediately");
        run.Verify(record, false, false);
        if (run.Verified != 0 || run.Unresolved != 1) throw new Exception("FAIL: later failed validation must revoke success");
        run.Verify(record, false, true);
        if (run.Inapplicable != 1 || run.Unresolved != 0) throw new Exception("FAIL: missing current target is retained separately");
        record = new TranslationRepairRecord { SourceFile = "pack/DefInjected/Fixture/paid.xml", DefType = "Namespace.Fixture",
            OriginalKey = "Def.label", Translation = "付費譯文", Status = "衝突" };
        run.Observe(record);
        run.Verify(record, true, false);
        if (run.Total != 1 || run.Unresolved != 1) throw new Exception("FAIL: canonical spelling and namespace must not duplicate an issue or hide a conflict");
        dynamic next = Activator.CreateInstance(type);
        if (next.Total != 0) throw new Exception("FAIL: previous run history leaked into a fresh run");
        Console.WriteLine("PASS: repair run deduplication, live verification, failed injection, inapplicability, conflict and historical isolation");
    }
}
