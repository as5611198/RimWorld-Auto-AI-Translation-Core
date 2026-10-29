using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    // Non-modal status surface used only by automatic startup and pipeline repair.
    internal sealed class Window_TranslationRepairProgress : Window
    {
        public override Vector2 InitialSize => new Vector2(430f, 112f);

        public Window_TranslationRepairProgress()
        {
            doWindowBackground = true;
            doCloseButton = false;
            doCloseX = false;
            closeOnAccept = false;
            closeOnCancel = false;
            forcePause = false;
            absorbInputAroundWindow = false;
            draggable = true;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            float width = InitialSize.x;
            float height = InitialSize.y;
            windowRect = new Rect(
                Mathf.Max(8f, UI.screenWidth - width - 24f),
                Mathf.Max(8f, UI.screenHeight - height - 24f),
                width, height);
        }

        public override void DoWindowContents(Rect rect)
        {
            bool bypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(0f, 0f, rect.width, 24f), TranslationRepairController.ModeLabel.Translate());
                string status = TranslationRepairController.IsWaiting
                    ? TranslationRepairController.WaitingReason.Translate()
                    : TranslationRepairController.Phase.Translate();
                Widgets.Label(new Rect(0f, 26f, rect.width, 24f), status);

                if (TranslationRepairController.Applying)
                {
                    Widgets.Label(new Rect(0f, 52f, rect.width, 24f), "ATC_Repair_ApplyingHint".Translate());
                    return;
                }

                int total = TranslationRepairController.Total;
                int processed = TranslationRepairController.Processed;
                if (total > 0 && !TranslationRepairController.Finished)
                {
                    Rect bar = new Rect(0f, 54f, rect.width, 24f);
                    Widgets.FillableBar(bar, Mathf.Clamp01((float)processed / total));
                    Widgets.Label(bar, "ATC_Repair_Progress".Translate(processed, total));
                }
                else
                    Widgets.Label(new Rect(0f, 54f, rect.width, 24f), "ATC_Repair_AutomaticHint".Translate());
            }
            finally
            {
                Text.Font = GameFont.Small;
                Patch_GUI_Label_GUIContent.BypassInterceptor = bypass;
            }
        }
    }
}
