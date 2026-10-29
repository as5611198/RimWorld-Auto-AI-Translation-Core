using System.Reflection;
using AutoTranslator_Core;
using HarmonyLib;
using Verse;

internal static partial class Suite
{
    static bool SkipEntryEngineUpdate() => false;
    static bool AddRepairNotice(Window window)
    {
        if (!window.GetType().Name.StartsWith("Window_TranslationRepair", StringComparison.Ordinal)) return true;
        if (window is Window_TranslationRepair) repairNoticeOpens++;
        ((List<Window>)AccessTools.Field(typeof(WindowStack), "windows").GetValue(repairWindowStack)).Add(window);
        return false;
    }

    static void PollNoticeForCompletedResult(Type controller, BindingFlags flags)
    {
        controller.GetField("nextNoticeCheck", flags).SetValue(null, DateTime.MinValue);
        controller.GetField("readyAfter", flags).SetValue(null, DateTime.MinValue);
        controller.GetField("checkedNoticeRevision", flags).SetValue(null, -1);
        controller.GetField("noticeSignature", flags).SetValue(null, null);
        controller.GetMethod("PollNotice", flags).Invoke(null, null);
    }

    static void TestMainMenuNoticeLifecycle()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var controller = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.TranslationRepairController");
        var h = new Harmony("atc.main-menu-notice-native-route");
        var previousState = Current.ProgramState;
        var previousSettings = AutoTranslatorMod.Settings;
        bool previousRunning = AutoTranslatorSettings.IsRunning;
        var failures = new List<Exception>();
        void Case(Action check) { try { check(); } catch (Exception ex) { failures.Add(ex); Console.WriteLine(ex.GetBaseException().Message); } }
        void Reset()
        {
            repairWindowStack = new WindowStack();
            repairNoticeOpens = 0;
            AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Traditional };
            AutoTranslatorSettings.IsRunning = false;
            controller.GetMethod("ClearEnvironment", flags).Invoke(null, null);
            controller.GetProperty("Finished", flags).SetValue(null, false);
            controller.GetField("run", flags).SetValue(null, null);
            controller.GetField("checkedNoticeRevision", flags).SetValue(null, -1);
            controller.GetField("noticeSignature", flags).SetValue(null, null);
        }
        void Frame()
        {
            var root = (UIRoot_Entry)Uninitialized(typeof(UIRoot_Entry));
            root.windows = repairWindowStack;
            // First native update establishes this menu's window stack. The
            // second advances past the settling period without waiting in tests.
            root.UIRootUpdate();
            controller.GetField("nextNoticeCheck", flags).SetValue(null, DateTime.MinValue);
            controller.GetField("readyAfter", flags).SetValue(null, DateTime.MinValue);
            root.UIRootUpdate();
        }
        try
        {
            Current.ProgramState = ProgramState.Entry;
            h.Patch(AccessTools.PropertyGetter(typeof(Find), "WindowStack"), prefix: new HarmonyMethod(typeof(Suite), nameof(RepairWindowStack)));
            h.Patch(AccessTools.PropertyGetter(typeof(LongEventHandler), "AnyEventNowOrWaiting"), prefix: new HarmonyMethod(typeof(Suite), nameof(NoLongEvent)));
            h.Patch(AccessTools.Method(typeof(UIRoot_Entry), "UIRootUpdate"), prefix: new HarmonyMethod(typeof(Suite), nameof(SkipEntryEngineUpdate)));
            h.Patch(AccessTools.Method(typeof(WindowStack), "Add"), prefix: new HarmonyMethod(typeof(Suite), nameof(AddRepairNotice)));
            Case(() => {
                Reset(); Frame();
                Check(repairNoticeOpens == 1 && repairWindowStack.Windows.Single() is Window_TranslationRepair,
                    "native menu update reaches the installed hook and adds a real repair window without the startup MonoBehaviour");
                Check(!(bool)controller.GetProperty("Finished", flags).GetValue(null),
                    "unresolved issue notice remains a manual repair introduction before a completed run");
                Frame();
                Check(repairNoticeOpens == 1, "native menu updates retain one introduction without reopening it");
            });
            Case(() => {
                Reset();
                string file = Write("Notice/completed-result.xml", "<LanguageData />");
                var record = new TranslationRepairRecord {
                    Id = "completed-result", SourceFile = file, SourceKind = "owned-pack",
                    OriginalKey = "Fixture.label", Translation = "已完成"
                };
                controller.GetMethod("Observe", flags).Invoke(null, new object[] { record });
                var completed = new TranslationRepairRun();
                completed.Observe(record); completed.Verify(record, true, false);
                controller.GetField("run", flags).SetValue(null, completed);
                controller.GetProperty("Finished", flags).SetValue(null, true);
                PollNoticeForCompletedResult(controller, flags);
                Check(repairNoticeOpens == 1 && repairWindowStack.Windows.Single() is Window_TranslationRepair,
                    "completed automatic repair opens a result window");
                var resultWindow = repairWindowStack.Windows.Single();
                var showResult = (bool)typeof(Window_TranslationRepair)
                    .GetField("showResult", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(resultWindow);
                Check(showResult, "completed repair notice is not the manual start screen");
            });
            Case(() => {
                var progressType = typeof(AutoTranslatorScanner).Assembly.GetType("AutoTranslator_Core.Window_TranslationRepairProgress");
                Check(progressType != null, "automatic repair has a dedicated progress window");
                if (progressType == null) return;
                controller.GetField("automaticRepair", flags).SetValue(null, true);
                controller.GetMethod("EnsureAutomaticProgressWindow", flags).Invoke(null, null);
                Check(repairWindowStack.Windows.Count(w => progressType.IsInstanceOfType(w)) == 1,
                    "automatic repair opens one right-corner progress window");
                var progress = (Window)Activator.CreateInstance(progressType);
                Check(!progress.forcePause && !progress.absorbInputAroundWindow,
                    "automatic repair progress window is non-modal and does not pause the main menu");
            });
            Case(() => {
                Reset(); AutoTranslatorSettings.IsRunning = true; Frame();
                Check(repairNoticeOpens == 1, "background translation does not hide the main-menu introduction");
                Check(!(bool)controller.GetMethod("Start", flags).Invoke(null, null),
                    "visible introduction cannot start bulk repair while translation is active");
            });
            Case(() => {
                Reset();
                var scanner = typeof(AutoTranslatorScanner);
                scanner.GetField("_pendingMemoryDrop", flags).SetValue(null, true);
                try {
                    Frame();
                    Check(repairNoticeOpens == 1, "queued memory injection does not hide the main-menu introduction");
                    Check(!(bool)controller.GetMethod("Start", flags).Invoke(null, null),
                        "visible introduction cannot start bulk repair during memory injection");
                } finally { scanner.GetField("_pendingMemoryDrop", flags).SetValue(null, false); }
            });
            Case(() => {
                Reset();
                AutoTranslatorMod.Settings.LastRepairNotice = "main-menu-repair-v2:" +
                    typeof(AutoTranslatorScanner).GetProperty("SelfRepairEnvironment", flags).GetValue(null) + ":Traditional";
                Frame();
                Check(repairNoticeOpens == 1, "legacy acknowledgement cannot permanently suppress the corrected introduction");
            });
            Case(() => {
                Reset(); Frame();
                controller.GetMethod("DeferNotice", flags).Invoke(null, null);
                Check(string.IsNullOrEmpty(AutoTranslatorMod.Settings.LastRepairNotice) && AutoTranslatorMod.Settings.SeenRepairIssues.Count == 0,
                    "Later never persists a completed-repair acknowledgement or marks issues resolved");
                ((List<Window>)AccessTools.Field(typeof(WindowStack), "windows").GetValue(repairWindowStack)).Clear();
                Frame();
                Check(repairNoticeOpens == 1, "Later suppresses the introduction for this session");
                controller.GetMethod("ClearEnvironment", flags).Invoke(null, null);
                Frame();
                Check(repairNoticeOpens == 2, "a fresh environment offers repair again after Later without clearing player settings");
            });
            Case(() => {
                Reset(); Frame();
                ((List<Window>)AccessTools.Field(typeof(WindowStack), "windows").GetValue(repairWindowStack)).Clear();
                Frame();
                Check(repairNoticeOpens == 2, "a window removed before its first render does not silently consume the introduction");
                controller.GetMethod("NoticeDrawn", flags).Invoke(null, null);
                ((List<Window>)AccessTools.Field(typeof(WindowStack), "windows").GetValue(repairWindowStack)).Clear();
                Frame();
                Check(repairNoticeOpens == 2, "a rendered introduction does not automatically reopen after closure in the same session");
            });
            Case(() => {
                Reset(); Frame();
                repairWindowStack = new WindowStack();
                Frame();
                Check(repairNoticeOpens == 2, "replacing the native menu stack cannot lose the pending introduction");
            });
            Case(() => {
                Reset();
                var windows = (List<Window>)AccessTools.Field(typeof(WindowStack), "windows").GetValue(repairWindowStack);
                windows.Add((Window)Uninitialized(typeof(RepairOverlayWindow)));
                windows[0].absorbInputAroundWindow = true;
                Frame();
                Check(repairNoticeOpens == 0, "native menu route waits while a modal dialog is open");
                windows.Clear(); Frame();
                Check(repairNoticeOpens == 1, "closing the modal dialog makes the pending introduction appear");
            });
            Case(() => {
                Reset();
                AutoTranslatorMod.Settings.LastRepairNotice = (string)controller.GetMethod("NoticeSignature", flags).Invoke(null, null);
                Frame();
                Check(repairNoticeOpens == 0, "a completed run with no new observations stays acknowledged");
                string file = Write("Notice/new-overlay.xml", "<LanguageData />");
                controller.GetMethod("Observe", flags).Invoke(null, new object[] { new TranslationRepairRecord {
                    Id = "notice-new-issue", SourceFile = file, SourceKind = "third-party-read-overlay",
                    OriginalKey = "Fixture.label", Translation = "A new preserved candidate"
                } });
                Frame();
                Check(repairNoticeOpens == 1, "a new observed issue is offered after a previously completed run");
            });
            Case(() => {
                Reset();
                string file = Write("Notice/restart-overlay.xml", "<LanguageData />");
                var record = new TranslationRepairRecord { Id = "restart-notice", SourceFile = file,
                    SourceKind = "third-party-read-overlay", OriginalKey = "Fixture.label", Translation = "Previously reviewed candidate" };
                var completed = new TranslationRepairRun(); completed.Observe(record); completed.Verify(record, false, false);
                controller.GetField("run", flags).SetValue(null, completed);
                controller.GetMethod("Acknowledge", flags).Invoke(null, null);
                var persisted = AutoTranslatorMod.Settings;
                AutoTranslatorMod.Settings = new AutoTranslatorSettings { TargetLang = TargetLanguage.Traditional,
                    LastRepairNotice = persisted.LastRepairNotice, SeenRepairIssues = persisted.SeenRepairIssues.ToList() };
                controller.GetMethod("ClearEnvironment", flags).Invoke(null, null);
                AutoTranslatorScanner.NotifyRawDefSourcesChanged();
                controller.GetMethod("Observe", flags).Invoke(null, new object[] { record });
                Frame();
                Check(repairNoticeOpens == 0, "a completed run survives fresh session caches and rereading the same unresolved candidate");
                controller.GetMethod("Observe", flags).Invoke(null, new object[] { new TranslationRepairRecord {
                    Id = "restart-equivalent-merge", SourceFile = file, SourceKind = "third-party-read-overlay",
                    OriginalKey = "Fixture.otherLabel", Translation = "Equivalent candidate", Reason = "valid", Status = "相同譯文合併" } });
                Frame();
                Check(repairNoticeOpens == 0, "a safely merged equivalent candidate does not count as a newly discovered repair problem");
                controller.GetMethod("Observe", flags).Invoke(null, new object[] { new TranslationRepairRecord {
                    Id = "restart-new-value", SourceFile = file, SourceKind = "third-party-read-overlay",
                    OriginalKey = "Fixture.label", Translation = "A newly changed candidate" } });
                Frame();
                Check(repairNoticeOpens == 1, "a genuinely changed value remains eligible for a notice after restart");
            });
        }
        finally
        {
            Current.ProgramState = previousState;
            AutoTranslatorMod.Settings = previousSettings;
            AutoTranslatorSettings.IsRunning = previousRunning;
            h.UnpatchAll(h.Id);
        }
        if (failures.Count != 0) throw new AggregateException("Native main-menu notice regressions", failures);
    }
}
