using System;
using System.Reflection;
using AutoTranslator_Core;

internal static partial class Suite
{
    static void TestStartupRepairPolicy()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var assembly = typeof(AutoTranslatorScanner).Assembly;
        var policy = assembly.GetType("AutoTranslator_Core.TranslationStartupRepairPolicy");
        Check(policy != null, "startup repair policy exists");
        if (policy == null) return;

        var startup = policy.GetMethod("DecideStartup", flags);
        var pipeline = policy.GetMethod("DecidePipeline", flags);
        var observation = policy.GetMethod("ShouldAutoRepairObservation", flags);
        Check(startup != null && pipeline != null && observation != null, "startup repair policy exposes startup, pipeline, and observation decisions");
        if (startup == null || pipeline == null || observation == null) return;

        string Startup(bool completed, bool ownedPackChanged)
            => (string)startup.Invoke(null, new object[] { completed, ownedPackChanged });
        string Pipeline(bool producedOutput)
            => (string)pipeline.Invoke(null, new object[] { producedOutput });

        Check(Startup(false, false) == "repair-owned-pack",
            "first ATC activation repairs the owned translation pack before injection");
        Check(Startup(true, false) == "fast-inject",
            "unchanged ATC-owned pack skips startup repair");
        Check(Startup(true, true) == "repair-delta",
            "an existing baseline with changed files can only request incremental repair");
        Check(Pipeline(false) == "no-op",
            "translation completion without new output does not start repair");
        Check(Pipeline(true) == "repair-delta",
            "translation completion with new ATC output requests delta repair");
        bool Auto(bool automatic, bool owned, string sourceKind)
            => (bool)observation.Invoke(null, new object[] { automatic, owned, sourceKind });
        Check(Auto(true, true, "generated-xml"),
            "automatic repair accepts ATC-owned generated observations");
        Check(!Auto(true, false, "third-party-read-overlay"),
            "automatic repair excludes third-party observations");
        Check(!Auto(true, false, "native-duplicate-list"),
            "automatic repair excludes native repair observations");
        Check(Auto(false, false, "native-duplicate-list"),
            "manual repair keeps native diagnostics available");
    }
}
