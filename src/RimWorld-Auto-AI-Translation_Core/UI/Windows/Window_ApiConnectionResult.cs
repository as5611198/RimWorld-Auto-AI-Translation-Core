using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    internal sealed class Window_ApiConnectionResult : Window
    {
        private readonly string report;
        private Vector2 scroll;
        public override Vector2 InitialSize { get { return new Vector2(740f, 520f); } }

        internal Window_ApiConnectionResult(ApiConnectionDiagnostic diagnostic)
        {
            report = AutoTranslatorAPI.GetConnectionTestReport(diagnostic);
            doCloseX = true;
            absorbInputAroundWindow = true;
            draggable = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            GameFont oldFont = Text.Font;
            bool oldWrap = Text.WordWrap;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0, 0, inRect.width - 30f, 36f), "ATC_ConnectionTest_Title".Translate());
                Text.Font = GameFont.Small;
                Text.WordWrap = true;
                Rect viewport = new Rect(0, 44f, inRect.width, inRect.height - 94f);
                float width = viewport.width - 20f;
                Rect content = new Rect(0, 0, width, Mathf.Max(viewport.height, Text.CalcHeight(report, width) + 8f));
                Widgets.BeginScrollView(viewport, ref scroll, content);
                Widgets.Label(content, report);
                Widgets.EndScrollView();
                if (Widgets.ButtonText(new Rect(0, inRect.height - 38f, inRect.width * 0.55f, 34f), "ATC_ConnectionTest_Copy".Translate()))
                    GUIUtility.systemCopyBuffer = report;
                if (Widgets.ButtonText(new Rect(inRect.width * 0.6f, inRect.height - 38f, inRect.width * 0.4f, 34f), "CloseButton".Translate()))
                    Close();
            }
            finally
            {
                Text.Font = oldFont;
                Text.WordWrap = oldWrap;
            }
        }
    }
}
