using System;
using System.IO;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public sealed class Window_TranslationRepair : Window
    {
        private bool minimized;
        private Rect expandedRect;
        private string actionHint;
        private bool showResult;
        private bool noticeDrawn;
        public override Vector2 InitialSize => new Vector2(660f, 590f);

        public Window_TranslationRepair(bool showPreviousResult = true)
        {
            showResult = showPreviousResult;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
            closeOnCancel = false;
            doCloseX = false;
            draggable = true;
        }

        private void ToggleSize()
        {
            minimized = !minimized;
            if (minimized)
            {
                expandedRect = windowRect;
                windowRect = new Rect(Mathf.Max(0f, Verse.UI.screenWidth - 440f), Mathf.Max(0f, Verse.UI.screenHeight - 230f), 420f, 200f);
            }
            else windowRect = expandedRect;
        }

        public override void DoWindowContents(Rect rect)
        {
            if (!noticeDrawn && Event.current?.type == EventType.Repaint)
            {
                noticeDrawn = true;
                TranslationRepairController.NoticeDrawn();
            }
            bool bypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
            bool enabled = GUI.enabled;
            var font = Text.Font;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                bool running = TranslationRepairController.IsRunning;
                bool waiting = TranslationRepairController.IsWaiting;
                bool finished = showResult && TranslationRepairController.Finished;
                var results = TranslationRepairController.Results;
                if (minimized)
                {
                    Text.Font = GameFont.Small;
                    Widgets.Label(new Rect(0f, 0f, rect.width, 26f), TranslationRepairController.Phase.Translate());
                    if (finished && results != null)
                        Widgets.Label(new Rect(0f, 29f, rect.width, 28f), "ATC_Repair_ShortResult".Translate(results.Verified, results.Inapplicable, results.Unresolved));
                    else DrawProgress(new Rect(0f, 29f, rect.width, waiting ? 55f : 25f));
                    float expandWidth = waiting ? (rect.width - 10f) / 2f : rect.width;
                    if (Widgets.ButtonText(new Rect(0f, 100f, expandWidth, 30f), "ATC_Repair_Expand".Translate())) ToggleSize();
                    if (waiting && Widgets.ButtonText(new Rect(expandWidth + 10f, 100f, expandWidth, 30f), "ATC_Repair_CancelWaiting".Translate()))
                    { TranslationRepairController.CancelWaiting(); ToggleSize(); }
                    return;
                }
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, 0f, rect.width, 38f), "ATC_Repair_IntroTitle".Translate());
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(0f, 48f, rect.width, 100f), "ATC_Repair_Intro".Translate());
                if (waiting)
                {
                    Widgets.Label(new Rect(0f, 160f, rect.width, 30f), "ATC_Repair_Waiting".Translate());
                    Widgets.Label(new Rect(0f, 202f, rect.width, 65f), TranslationRepairController.WaitingReason.Translate());
                    Widgets.Label(new Rect(0f, 280f, rect.width, 110f), "ATC_Repair_WaitHint".Translate());
                }
                else if (running || finished)
                {
                    Widgets.Label(new Rect(0f, 150f, rect.width, 28f), TranslationRepairController.ModeLabel.Translate() + " — " + TranslationRepairController.Phase.Translate());
                    DrawProgress(new Rect(0f, 182f, rect.width, 28f));
                    float y = 226f;
                    if (results != null)
                    {
                        Widgets.Label(new Rect(0f, y, rect.width, 27f), "ATC_Repair_VerifiedCount".Translate(results.Verified));
                        Widgets.Label(new Rect(0f, y + 30f, rect.width, 27f), "ATC_Repair_InapplicableCount".Translate(results.Inapplicable));
                        Widgets.Label(new Rect(0f, y + 60f, rect.width, 27f), "ATC_Repair_UnresolvedCount".Translate(results.Unresolved));
                        if (running) Widgets.Label(new Rect(0f, y + 90f, rect.width, 27f), "ATC_Repair_WaitingCount".Translate(results.Waiting));
                        else if (TranslationRepairController.NativeErrorsBefore.HasValue && TranslationRepairController.NativeErrorsAfter.HasValue)
                            Widgets.Label(new Rect(0f, y + 90f, rect.width, 27f), "ATC_Repair_NativeErrorCounts".Translate(
                                TranslationRepairController.NativeErrorsBefore.Value, TranslationRepairController.NativeErrorsAfter.Value));
                    }
                    string note = !running && actionHint != null ? actionHint : TranslationRepairController.Failure != null ? "ATC_Repair_FailureHint" :
                        finished && results?.Total == 0 ? "ATC_Repair_Clean" : running ? "ATC_Repair_StayAtMenu" : "ATC_Repair_ResultHint";
                    Widgets.Label(new Rect(0f, 354f, rect.width, 140f), note.Translate());
                }
                else
                {
                    Widgets.Label(new Rect(0f, 160f, rect.width, 130f), "ATC_Repair_Explanation".Translate());
                    if (!string.IsNullOrEmpty(actionHint)) Widgets.Label(new Rect(0f, 300f, rect.width, 70f), actionHint.Translate());
                }
                float third = (rect.width - 20f) / 3f;
                float bottom = rect.height - 42f;
                if (waiting)
                {
                    float half = (rect.width - 10f) / 2f;
                    if (Widgets.ButtonText(new Rect(0f, bottom, half, 36f), "ATC_Repair_Minimize".Translate())) ToggleSize();
                    if (Widgets.ButtonText(new Rect(half + 10f, bottom, half, 36f), "ATC_Repair_CancelWaiting".Translate())) TranslationRepairController.CancelWaiting();
                }
                else if (running)
                {
                    if (Widgets.ButtonText(new Rect(0f, bottom, rect.width, 36f), "ATC_Repair_Minimize".Translate())) ToggleSize();
                }
                else
                {
                    if (Widgets.ButtonText(new Rect(0f, bottom, third, 36f), (finished ? "ATC_Repair_CheckAgain" : "ATC_Repair_Start").Translate()))
                    {
                        if (TranslationRepairController.RequestStart()) { actionHint = null; showResult = true; }
                        else actionHint = "ATC_Repair_Busy";
                    }
                    if (Widgets.ButtonText(new Rect(third + 10f, bottom, third, 36f), "ATC_Repair_OpenReport".Translate()))
                    {
                        string directory = Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "SelfRepair");
                        Directory.CreateDirectory(directory);
                        Application.OpenURL(new Uri(directory + Path.DirectorySeparatorChar).AbsoluteUri);
                    }
                    if (Widgets.ButtonText(new Rect(2f * third + 20f, bottom, third, 36f), (finished ? "ATC_Repair_Close" : "ATC_Repair_Later").Translate()))
                    {
                        if (!finished) TranslationRepairController.DeferNotice();
                        Close();
                    }
                }
            }
            finally
            {
                GUI.enabled = enabled;
                Text.Font = font;
                Patch_GUI_Label_GUIContent.BypassInterceptor = bypass;
            }
        }

        private static void DrawProgress(Rect rect)
        {
            if (TranslationRepairController.IsWaiting)
            {
                Widgets.Label(rect, TranslationRepairController.WaitingReason.Translate());
                return;
            }
            int total = TranslationRepairController.Total, processed = TranslationRepairController.Processed;
            if (TranslationRepairController.Applying)
            {
                Widgets.Label(rect, "ATC_Repair_ApplyingHint".Translate());
                return;
            }
            if (total > 0 && !TranslationRepairController.Finished)
            {
                Widgets.FillableBar(rect, Mathf.Clamp01((float)processed / total));
                Widgets.Label(rect, "ATC_Repair_Progress".Translate(processed, total));
            }
        }
    }
}
