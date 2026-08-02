using RimWorld;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public class Window_WorkbenchBatchReplace : Window
    {
        private string _findText = "";
        private string _replacementText = "";
        private bool _caseSensitive;
        private string _previewFingerprint = "";
        private int _previewMatchCount;
        private TranslationWorkbenchTab.WorkbenchBatchReplaceScope _scope =
            TranslationWorkbenchTab.WorkbenchBatchReplaceScope.VisibleResults;

        public override Vector2 InitialSize => new Vector2(680f, 430f);

        public Window_WorkbenchBatchReplace()
        {
            doCloseButton = false;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            bool previousBypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "ATC_Workbench_BatchReplaceTitle".Translate());
                Text.Font = GameFont.Small;
                Widgets.DrawLineHorizontal(0f, 38f, inRect.width);

                float y = 52f;
                Widgets.Label(new Rect(0f, y, inRect.width, 24f), "ATC_Workbench_BatchFindLabel".Translate());
                y += 26f;
                _findText = Widgets.TextField(new Rect(0f, y, inRect.width, 32f), _findText ?? "");
                y += 46f;

                Widgets.Label(new Rect(0f, y, inRect.width, 24f), "ATC_Workbench_BatchReplaceLabel".Translate());
                y += 26f;
                _replacementText = Widgets.TextField(new Rect(0f, y, inRect.width, 32f), _replacementText ?? "");
                y += 48f;

                Widgets.Label(new Rect(0f, y, inRect.width, 24f), "ATC_Workbench_BatchScopeLabel".Translate());
                y += 28f;
                DrawScopeButtons(new Rect(0f, y, inRect.width, 34f));
                y += 48f;

                Widgets.CheckboxLabeled(
                    new Rect(0f, y, 280f, 28f),
                    "ATC_Workbench_BatchCaseSensitive".Translate(),
                    ref _caseSensitive);

                int matchCount = GetPreviewMatchCount();
                GUI.color = matchCount > 0 ? new Color(0.65f, 1f, 0.65f) : Color.gray;
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(
                    new Rect(inRect.width - 300f, y, 300f, 28f),
                    "ATC_Workbench_BatchMatchCount".Translate(matchCount));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;

                float buttonY = inRect.height - 42f;
                if (Widgets.ButtonText(new Rect(0f, buttonY, 150f, 38f), "ATC_Btn_Cancel".Translate()))
                {
                    Close();
                }

                bool canApply = matchCount > 0 && !string.IsNullOrEmpty(_findText);
                GUI.color = canApply ? new Color(0.55f, 1f, 0.6f) : Color.gray;
                if (Widgets.ButtonText(new Rect(inRect.width - 180f, buttonY, 180f, 38f), "ATC_Workbench_BatchApplyBtn".Translate()))
                {
                    if (canApply) ConfirmApply(matchCount);
                }
                GUI.color = Color.white;
            }
            finally
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = true;
                GUI.color = Color.white;
                Patch_GUI_Label_GUIContent.BypassInterceptor = previousBypass;
            }
        }

        private void DrawScopeButtons(Rect rect)
        {
            float gap = 8f;
            float width = (rect.width - gap * 2f) / 3f;
            DrawScopeButton(
                new Rect(rect.x, rect.y, width, rect.height),
                TranslationWorkbenchTab.WorkbenchBatchReplaceScope.VisibleResults,
                "ATC_Workbench_BatchScopeVisible".Translate().ToString());
            DrawScopeButton(
                new Rect(rect.x + width + gap, rect.y, width, rect.height),
                TranslationWorkbenchTab.WorkbenchBatchReplaceScope.CurrentCategory,
                "ATC_Workbench_BatchScopeCategory".Translate().ToString());
            DrawScopeButton(
                new Rect(rect.x + (width + gap) * 2f, rect.y, width, rect.height),
                TranslationWorkbenchTab.WorkbenchBatchReplaceScope.AllCategories,
                "ATC_Workbench_BatchScopeAll".Translate().ToString());
        }

        private int GetPreviewMatchCount()
        {
            string findText = _findText ?? "";
            string replacementText = _replacementText ?? "";
            string fingerprint = ((int)_scope) + "|" + (_caseSensitive ? "1" : "0") + "|" +
                                 findText.Length + ":" + findText + "|" +
                                 replacementText.Length + ":" + replacementText;
            if (fingerprint == _previewFingerprint) return _previewMatchCount;

            _previewFingerprint = fingerprint;
            _previewMatchCount = TranslationWorkbenchTab.CountWorkbenchBatchReplaceMatches(
                _scope,
                _findText,
                _replacementText,
                _caseSensitive);
            return _previewMatchCount;
        }

        private void DrawScopeButton(Rect rect, TranslationWorkbenchTab.WorkbenchBatchReplaceScope scope, string label)
        {
            bool selected = _scope == scope;
            GUI.color = selected ? new Color(0.5f, 0.85f, 1f) : Color.white;
            if (Widgets.ButtonText(rect, label)) _scope = scope;
            GUI.color = Color.white;
        }

        private void ConfirmApply(int matchCount)
        {
            string findText = _findText ?? "";
            string replacementText = _replacementText ?? "";
            bool caseSensitive = _caseSensitive;
            TranslationWorkbenchTab.WorkbenchBatchReplaceScope scope = _scope;

            Find.WindowStack.Add(new Dialog_MessageBox(
                "ATC_Workbench_BatchConfirm".Translate(matchCount),
                "ATC_Btn_Confirm".Translate(),
                () =>
                {
                    int changed = TranslationWorkbenchTab.ApplyWorkbenchBatchReplace(
                        scope,
                        findText,
                        replacementText,
                        caseSensitive);
                    Messages.Message(
                        "ATC_Workbench_BatchReplaceSuccess".Translate(changed),
                        changed > 0 ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NeutralEvent,
                        false);
                    Close();
                },
                "ATC_Btn_Cancel".Translate(),
                null,
                "ATC_Workbench_BatchReplaceTitle".Translate()));
        }
    }
}
