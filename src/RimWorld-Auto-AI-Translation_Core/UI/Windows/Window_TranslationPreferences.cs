using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public sealed class Window_TranslationPreferences : Window
    {
        private string _draft;
        private bool _preview;
        private Vector2 _scroll;
        public override Vector2 InitialSize => new Vector2(900f, 740f);

        public Window_TranslationPreferences()
        {
            _draft = AutoTranslatorAPI.NormalizeTranslationInstructions(AutoTranslatorMod.Settings.ExtraTranslationInstructions);
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            bool previousBypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
            bool previousEnabled = GUI.enabled;
            GameFont previousFont = Text.Font;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, 0f, inRect.width - 24f, 34f), "ATC_Advanced_PromptTitle".Translate());
                Text.Font = GameFont.Small;
                string help = "ATC_Advanced_PromptHelp".Translate();
                float helpHeight = Text.CalcHeight(help, inRect.width);
                Widgets.Label(new Rect(0f, 42f, inRect.width, helpHeight), help);
                float top = 50f + helpHeight;
                Widgets.Label(new Rect(0f, top, inRect.width, 26f), _preview
                    ? "ATC_Advanced_PromptPreviewLabel".Translate(("ATC_Lang_" + AutoTranslatorMod.Settings.TargetLang).Translate())
                    : "ATC_Advanced_PromptCount".Translate(_draft.Length, AutoTranslatorAPI.MaximumTranslationInstructionsLength));

                string content = _preview ? AutoTranslatorAPI.GetSystemPrompt(AutoTranslatorMod.Settings.TargetLang, _draft) : _draft;
                Rect outRect = new Rect(0f, top + 30f, inRect.width, inRect.height - top - 130f);
                Rect viewRect = new Rect(0f, 0f, outRect.width - 22f,
                    Mathf.Max(outRect.height - 1f, Text.CalcHeight(content + "\n", outRect.width - 42f) + 36f));
                Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
                if (_preview) Widgets.TextArea(viewRect, content, true);
                else
                {
                    GUI.enabled = previousEnabled && !AutoTranslatorSettings.IsRunning;
                    _draft = AutoTranslatorAPI.NormalizeTranslationInstructions(Widgets.TextArea(viewRect, _draft));
                    GUI.enabled = previousEnabled;
                }
                Widgets.EndScrollView();

                float half = (inRect.width - 12f) / 2f;
                if (Widgets.ButtonText(new Rect(0f, inRect.height - 86f, half, 34f),
                    (_preview ? "ATC_Advanced_PromptEdit" : "ATC_Advanced_PromptPreview").Translate()))
                {
                    _preview = !_preview;
                    _scroll = Vector2.zero;
                    GUI.FocusControl(null);
                }
                GUI.enabled = previousEnabled && !AutoTranslatorSettings.IsRunning;
                if (Widgets.ButtonText(new Rect(half + 12f, inRect.height - 86f, half, 34f), "ATC_Advanced_PromptReset".Translate()))
                {
                    _draft = "";
                    _scroll = Vector2.zero;
                    GUI.FocusControl(null);
                }
                if (Widgets.ButtonText(new Rect(0f, inRect.height - 42f, half, 34f), "ATC_Advanced_PromptSave".Translate()))
                {
                    AutoTranslatorMod.Settings.ExtraTranslationInstructions = AutoTranslatorAPI.NormalizeTranslationInstructions(_draft);
                    LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
                    Close();
                }
                GUI.enabled = previousEnabled;
                if (Widgets.ButtonText(new Rect(half + 12f, inRect.height - 42f, half, 34f), "ATC_Advanced_PromptCancel".Translate())) Close();
            }
            finally
            {
                GUI.enabled = previousEnabled;
                Text.Font = previousFont;
                Patch_GUI_Label_GUIContent.BypassInterceptor = previousBypass;
            }
        }
    }
}
