using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    internal enum WorkflowButtonStyle
    {
        Quiet,
        Primary,
        Warning,
        Stop,
        Link,
        Dropdown,
        Tab,
        ActiveTab
    }

    internal static class WorkflowUiStyle
    {
        internal static readonly Color Panel = new Color(0.075f, 0.09f, 0.102f, 0.96f);
        internal static readonly Color RaisedPanel = new Color(0.12f, 0.145f, 0.165f, 0.96f);
        internal static readonly Color Header = new Color(0.16f, 0.18f, 0.20f, 0.98f);
        internal static readonly Color Selection = new Color(0.145f, 0.18f, 0.12f, 0.92f);
        internal static readonly Color MutedText = new Color(0.62f, 0.64f, 0.65f);
        internal static readonly Color LinkText = new Color(0.48f, 0.73f, 0.90f);
        internal static readonly Color GoodText = new Color(0.60f, 0.80f, 0.40f);
        internal static readonly Color WarningText = new Color(0.90f, 0.68f, 0.34f);
        internal static readonly Color ErrorText = new Color(0.94f, 0.43f, 0.39f);

        internal static bool Button(
            Rect rect,
            string label,
            WorkflowButtonStyle style = WorkflowButtonStyle.Quiet,
            bool enabled = true,
            GameFont font = GameFont.Small)
        {
            Color previousGuiColor = GUI.color;
            GUI.color = Color.white;
            Color background;
            Color border;
            Color text;
            switch (style)
            {
                case WorkflowButtonStyle.Primary:
                    background = new Color(0.26f, 0.36f, 0.15f, 0.98f);
                    border = new Color(0.46f, 0.60f, 0.28f);
                    text = Color.white;
                    break;
                case WorkflowButtonStyle.Warning:
                    background = new Color(0.46f, 0.35f, 0.08f, 0.98f);
                    border = new Color(0.78f, 0.61f, 0.18f);
                    text = Color.white;
                    break;
                case WorkflowButtonStyle.Stop:
                    background = new Color(0.40f, 0.14f, 0.12f, 0.98f);
                    border = new Color(0.66f, 0.29f, 0.25f);
                    text = Color.white;
                    break;
                case WorkflowButtonStyle.Link:
                    background = Color.clear;
                    border = Color.clear;
                    text = LinkText;
                    break;
                case WorkflowButtonStyle.Dropdown:
                    background = new Color(0.13f, 0.17f, 0.20f, 0.98f);
                    border = new Color(0.38f, 0.55f, 0.66f);
                    text = Color.white;
                    break;
                case WorkflowButtonStyle.ActiveTab:
                    background = new Color(0.12f, 0.145f, 0.165f, 1f);
                    border = new Color(0.38f, 0.42f, 0.45f);
                    text = Color.white;
                    break;
                case WorkflowButtonStyle.Tab:
                    background = new Color(0.085f, 0.10f, 0.115f, 1f);
                    border = new Color(0.25f, 0.28f, 0.30f);
                    text = new Color(0.78f, 0.78f, 0.76f);
                    break;
                default:
                    background = new Color(0.11f, 0.13f, 0.145f, 0.98f);
                    border = new Color(0.31f, 0.35f, 0.38f);
                    text = Color.white;
                    break;
            }

            if (style != WorkflowButtonStyle.Link)
            {
                Widgets.DrawBoxSolid(rect, background);
                DrawBorder(rect, border);
            }
            if (enabled && Mouse.IsOver(rect))
                Widgets.DrawHighlight(rect);

            GameFont previousFont = Text.Font;
            TextAnchor previousAnchor = Text.Anchor;
            Text.Font = font;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = enabled ? text : new Color(text.r, text.g, text.b, 0.38f);
            Widgets.Label(new Rect(rect.x + 4f, rect.y + 1f, rect.width - 8f, rect.height - 2f),
                label ?? string.Empty);
            GUI.color = Color.white;
            Text.Anchor = previousAnchor;
            Text.Font = previousFont;

            if (style == WorkflowButtonStyle.ActiveTab)
                Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - 3f, rect.width, 3f), GoodText);
            bool clicked = enabled && Widgets.ButtonInvisible(rect);
            GUI.color = previousGuiColor;
            return clicked;
        }

        internal static void DrawBorder(Rect rect, Color color)
        {
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, rect.width, 1f), color);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), color);
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.y, 1f, rect.height), color);
            Widgets.DrawBoxSolid(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), color);
        }
    }
}
