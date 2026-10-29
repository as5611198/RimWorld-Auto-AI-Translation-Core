using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static bool CaptureQueuedRepairStart() { queuedRepairStarts++; return false; }
    static int queuedRepairStarts;

    static void TestRepairWaiting(Harmony hooks)
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var scanner = typeof(AutoTranslatorScanner);
        var controller = scanner.Assembly.GetType("AutoTranslator_Core.TranslationRepairController");
        var request = controller.GetMethod("RequestStart", flags);
        Check(request != null, "repair accepts one player request and waits for active translation work");
        var countNativeErrors = controller.GetMethod("CountNativeErrors", flags);
        Check(countNativeErrors != null, "repair exposes the game's native diagnostic count separately from candidate counts");
        var diagnostics = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
        var diagnosticPackage = new DefInjectionPackage(typeof(RepairFixtureDef));
        diagnostics.loadErrors = new List<string> { "language error" };
        diagnosticPackage.loadErrors = new List<string> { "failed path one", "failed path two" };
        diagnostics.defInjections = new List<DefInjectionPackage> { diagnosticPackage };
        Check((int)countNativeErrors.Invoke(null, new object[] { diagnostics }) == 3,
            "native count includes language and injection diagnostics using the same scope as the game's warning");
        diagnosticPackage.loadErrors.RemoveAt(0);
        Check((int)countNativeErrors.Invoke(null, new object[] { diagnostics }) == 2,
            "native diagnostic count reflects the latest lists instead of accumulating past warnings");
        var previousState = Current.ProgramState;
        var previousLanguage = LanguageDatabase.activeLanguage;
        bool previousBusy = AutoTranslatorSettings.IsRunning;
        var previousWindows = repairWindowStack;
        hooks.Patch(AccessTools.PropertyGetter(typeof(Find), "WindowStack"), prefix: new HarmonyMethod(typeof(Suite), nameof(RepairWindowStack)));
        hooks.Patch(controller.GetMethod("BeginRepair", flags), prefix: new HarmonyMethod(typeof(Suite), nameof(CaptureQueuedRepairStart)));
        bool Waiting() => (bool)controller.GetProperty("IsWaiting", flags).GetValue(null);
        bool StartupRepairActive() => (bool)controller.GetProperty("StartupRepairActive", flags).GetValue(null);
        void Tick() => controller.GetMethod("Tick", flags).Invoke(null, null);
        bool Request() => (bool)request.Invoke(null, null);
        void Cancel() => controller.GetMethod("CancelWaiting", flags).Invoke(null, null);
        string Reason() => (string)controller.GetProperty("WaitingReason", flags).GetValue(null);
        try
        {
            Current.ProgramState = ProgramState.Entry;
            LanguageDatabase.activeLanguage = (LoadedLanguage)Uninitialized(typeof(LoadedLanguage));
            repairWindowStack = new WindowStack();
            var windows = (List<Window>)AccessTools.Field(typeof(WindowStack), "windows").GetValue(repairWindowStack);
            windows.Add((Window)Uninitialized(typeof(Window_TranslationRepair)));
            AutoTranslatorSettings.IsRunning = true;
            queuedRepairStarts = 0;
            Check(Request() && Waiting() && !(bool)controller.GetProperty("IsRunning", flags).GetValue(null),
                "waiting reserves player intent without blocking the existing translation pipeline");
            Check(Reason() == "ATC_Repair_WaitTranslation", "waiting screen identifies translation work as the blocker");
            Request(); Tick(); Tick();
            Check(queuedRepairStarts == 0 && Waiting(), "repeated clicks cannot start duplicate jobs while translation is busy");
            AutoTranslatorSettings.IsRunning = false;
            scanner.GetField("_pendingMemoryDrop", flags).SetValue(null, true);
            Tick();
            Check(Waiting() && queuedRepairStarts == 0 && Reason() == "ATC_Repair_WaitApplying",
                "queued repair waits for pending injection without blocking its dispatcher");
            scanner.GetField("_pendingMemoryDrop", flags).SetValue(null, false);
            Tick();
            Check(!Waiting() && queuedRepairStarts == 1, "native menu pump starts the queued repair once the existing work finishes");
            controller.GetField("startupRepair", flags).SetValue(null, true);
            controller.GetField("waiting", flags).SetValue(null, true);
            Check(StartupRepairActive(), "startup repair remains an explicit priority while its queued work is waiting");
            controller.GetField("waiting", flags).SetValue(null, false);
            controller.GetField("running", flags).SetValue(null, true);
            Check(StartupRepairActive(), "startup repair remains an explicit priority while its repair run is active");
            controller.GetField("running", flags).SetValue(null, false);
            Check(!StartupRepairActive(), "startup repair releases update work after its repair run finishes");
            controller.GetField("startupRepair", flags).SetValue(null, false);
            AutoTranslatorSettings.IsRunning = true;
            Request(); Cancel(); AutoTranslatorSettings.IsRunning = false; Tick();
            Check(!Waiting() && queuedRepairStarts == 1, "cancelled waiting cannot later begin a repair");
            AutoTranslatorSettings.IsRunning = true;
            Request(); controller.GetMethod("ClearEnvironment", flags).Invoke(null, null);
            AutoTranslatorSettings.IsRunning = false; Tick();
            Check(!Waiting() && queuedRepairStarts == 1, "environment changes cancel a queued request rather than repairing a different language");
            AutoTranslatorSettings.IsRunning = true; Request();
            Current.ProgramState = ProgramState.Playing; Tick();
            Check(!Waiting() && queuedRepairStarts == 1, "leaving the main menu cancels a queued request");
        }
        finally
        {
            Cancel(); scanner.GetField("_pendingMemoryDrop", flags).SetValue(null, false);
            Current.ProgramState = previousState; AutoTranslatorSettings.IsRunning = previousBusy;
            LanguageDatabase.activeLanguage = previousLanguage;
            repairWindowStack = previousWindows;
            hooks.Unpatch(controller.GetMethod("BeginRepair", flags), typeof(Suite).GetMethod(nameof(CaptureQueuedRepairStart), flags));
            hooks.Unpatch(AccessTools.PropertyGetter(typeof(Find), "WindowStack"), typeof(Suite).GetMethod(nameof(RepairWindowStack), flags));
        }
    }
}
