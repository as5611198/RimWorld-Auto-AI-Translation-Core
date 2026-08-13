using System;
using AutoTranslator_Core.TranslationPolicy;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    internal sealed class Window_TranslationPolicyAgentBudget : Window
    {
        private readonly string _title;
        private readonly string _message;
        private readonly Action<TranslationPolicyAgentConsentDecision> _onDecision;
        private bool _resolved;

        public override Vector2 InitialSize => new Vector2(760f, 500f);

        public Window_TranslationPolicyAgentBudget(
            string title,
            string message,
            Action<TranslationPolicyAgentConsentDecision> onDecision)
        {
            _title = title ?? string.Empty;
            _message = message ?? string.Empty;
            _onDecision = onDecision;
            doCloseX = false;
            doCloseButton = false;
            closeOnCancel = false;
            closeOnAccept = false;
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
                Widgets.Label(new Rect(0f, 0f, inRect.width, 38f), _title);
                Text.Font = GameFont.Small;

                Rect messageRect = new Rect(0f, 48f, inRect.width, inRect.height - 132f);
                Widgets.Label(messageRect, _message);

                float buttonWidth = (inRect.width - 20f) / 3f;
                float buttonY = inRect.height - 64f;
                Rect continueRect = new Rect(0f, buttonY, buttonWidth, 40f);
                Rect localOnlyRect = new Rect(buttonWidth + 10f, buttonY, buttonWidth, 40f);
                Rect cancelRect = new Rect((buttonWidth + 10f) * 2f, buttonY, buttonWidth, 40f);

                if (Widgets.ButtonText(continueRect, "ATC_PolicyAgent_BudgetPrompt_Continue".Translate()))
                {
                    Resolve(TranslationPolicyAgentConsentDecision.ContinueWithAgent);
                }
                if (Widgets.ButtonText(localOnlyRect, "ATC_PolicyAgent_BudgetPrompt_LocalOnly".Translate()))
                {
                    Resolve(TranslationPolicyAgentConsentDecision.LocalOnly);
                }
                if (Widgets.ButtonText(cancelRect, "ATC_PolicyAgent_BudgetPrompt_Cancel".Translate()))
                {
                    Resolve(TranslationPolicyAgentConsentDecision.Cancel);
                }
            }
            finally
            {
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                Patch_GUI_Label_GUIContent.BypassInterceptor = previousBypass;
            }
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            if (!_resolved &&
                (AutoTranslatorSettings.IsCancellationRequested ||
                 AutoTranslatorSettings.IsSkipCurrentRequested))
            {
                Resolve(TranslationPolicyAgentConsentDecision.Cancel);
            }
        }

        private void Resolve(TranslationPolicyAgentConsentDecision decision)
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                if (_onDecision != null) _onDecision(decision);
            }
            finally
            {
                Close();
            }
        }

        public override void PostClose()
        {
            base.PostClose();
            if (_resolved) return;

            _resolved = true;
            if (_onDecision != null)
                _onDecision(TranslationPolicyAgentConsentDecision.LocalOnly);
        }
    }
}
