using System;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    /// <summary>ATC 统一消息与确认弹窗。</summary>
    internal sealed class Window_AtcDialog : Window
    {
        private readonly string _message;
        private readonly string _buttonAText;
        private readonly Action _buttonAAction;
        private readonly string _buttonBText;
        private readonly Action _buttonBAction;
        private readonly string _title;
        private readonly bool _buttonADestructive;
        private readonly bool _buttonBDestructive;
        private readonly Vector2 _initialSize;
        private Vector2 _scrollPosition;

        public override Vector2 InitialSize => _initialSize;

        internal Window_AtcDialog(
            string text,
            string buttonAText = null,
            Action buttonAAction = null,
            string buttonBText = null,
            Action buttonBAction = null,
            string title = null,
            bool buttonADestructive = false,
            bool buttonBDestructive = false,
            Vector2? initialSize = null)
        {
            _message = text ?? string.Empty;
            _buttonAText = buttonAText;
            _buttonAAction = buttonAAction;
            _buttonBText = buttonBText;
            _buttonBAction = buttonBAction;
            _title = string.IsNullOrWhiteSpace(title)
                ? AutoTranslatorMod.WfText("提示", "Message")
                : title;
            _buttonADestructive = buttonADestructive;
            _buttonBDestructive = buttonBDestructive;
            _initialSize = initialSize ?? new Vector2(760f, 560f);

            doCloseButton = false;
            doCloseX = true;
            closeOnAccept = false;
            closeOnCancel = true;
            closeOnClickedOutside = false;
            forcePause = true;
            absorbInputAroundWindow = true;
            draggable = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect titleRect = new Rect(0f, 0f, inRect.width, 48f);
            Widgets.DrawBoxSolid(titleRect, WorkflowUiStyle.Header);
            Widgets.DrawBoxSolid(
                new Rect(titleRect.x, titleRect.y, 4f, titleRect.height),
                _buttonADestructive ? WorkflowUiStyle.ErrorText : WorkflowUiStyle.GoodText);
            WorkflowUiStyle.DrawBorder(titleRect, new Color(0.31f, 0.35f, 0.38f));

            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(18f, 0f, titleRect.width - 28f, titleRect.height), _title);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;

            Rect bodyRect = new Rect(0f, 60f, inRect.width, inRect.height - 122f);
            Widgets.DrawBoxSolid(bodyRect, WorkflowUiStyle.Panel);
            WorkflowUiStyle.DrawBorder(bodyRect, new Color(0.25f, 0.28f, 0.30f));
            Rect bodyInner = bodyRect.ContractedBy(14f);
            float textWidth = Mathf.Max(1f, bodyInner.width - 18f);
            float textHeight = Mathf.Max(bodyInner.height, Text.CalcHeight(_message, textWidth) + 8f);
            Rect viewRect = new Rect(0f, 0f, textWidth, textHeight);
            Widgets.BeginScrollView(bodyInner, ref _scrollPosition, viewRect);
            Widgets.Label(new Rect(0f, 0f, textWidth, textHeight), _message);
            Widgets.EndScrollView();

            DrawButtons(inRect);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private void DrawButtons(Rect inRect)
        {
            const float buttonWidth = 180f;
            const float buttonHeight = 38f;
            float y = inRect.height - buttonHeight;
            bool hasA = !string.IsNullOrWhiteSpace(_buttonAText);
            bool hasB = !string.IsNullOrWhiteSpace(_buttonBText);

            if (!hasA && !hasB)
            {
                Rect closeRect = new Rect(inRect.width - buttonWidth, y, buttonWidth, buttonHeight);
                if (WorkflowUiStyle.Button(closeRect,
                        AutoTranslatorMod.WfText("关闭", "Close"), WorkflowButtonStyle.Quiet))
                    Close();
                return;
            }

            if (hasB)
            {
                Rect buttonBRect = new Rect(0f, y, buttonWidth, buttonHeight);
                WorkflowButtonStyle style = _buttonBDestructive
                    ? WorkflowButtonStyle.Stop
                    : WorkflowButtonStyle.Quiet;
                if (WorkflowUiStyle.Button(buttonBRect, _buttonBText, style))
                {
                    Close();
                    _buttonBAction?.Invoke();
                }
            }

            if (hasA)
            {
                Rect buttonARect = new Rect(inRect.width - buttonWidth, y, buttonWidth, buttonHeight);
                WorkflowButtonStyle style = _buttonADestructive
                    ? WorkflowButtonStyle.Stop
                    : WorkflowButtonStyle.Primary;
                if (WorkflowUiStyle.Button(buttonARect, _buttonAText, style))
                {
                    Close();
                    _buttonAAction?.Invoke();
                }
            }
        }
    }
}
