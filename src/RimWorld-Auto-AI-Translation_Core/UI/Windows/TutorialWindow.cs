using HarmonyLib;
using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責教學視窗內容。
// EN: This file draws the tutorial window content.

namespace AutoTranslator_Core
{
    // 這個類別負責 Tutorial視窗 的主要流程與狀態。
    // EN: This class manages the main workflow and state for TutorialWindow.
    public class TutorialWindow : Window
    {
        // 這個欄位保存 scrollPos 的執行狀態或快取資料。
        // EN: This field stores scroll pos runtime state or cached data.
        private Vector2 scrollPos = Vector2.zero;
        // 這個屬性提供 InitialSize 的讀寫或計算結果。
        // EN: This method handles vector2.
        public override Vector2 InitialSize => new Vector2(750f, 700f);
        // 這個方法負責處理 Tutorial視窗 相關流程。
        // EN: This constructor initializes tutorial window.
        public TutorialWindow() { this.doCloseButton = false; this.doCloseX = true; this.forcePause = true; this.absorbInputAroundWindow = true; }
        // 這個方法負責處理 Do視窗Contents 相關流程。
        // EN: This method handles do window contents.
        public override void DoWindowContents(Rect inRect)
        {

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 40f), "📖 " + "ATC_Tutorial_Btn".Translate());
            Text.Font = GameFont.Small;
            Widgets.DrawLineHorizontal(0, 35f, inRect.width);
            Rect outRect = new Rect(0, 45f, inRect.width, inRect.height - 100f);
            string contentText = "ATC_Tutorial_FullText".Translate();
            float textHeight = Text.CalcHeight(contentText, inRect.width - 20f);
            Rect viewRect = new Rect(0, 0, inRect.width - 20f, Mathf.Max(textHeight + 50f, outRect.height));
            Widgets.BeginScrollView(outRect, ref scrollPos, viewRect);
            Widgets.Label(new Rect(0, 0, viewRect.width, textHeight), contentText);
            Widgets.EndScrollView();
            Rect closeRect = new Rect(inRect.width - 150f, inRect.height - 40f, 150f, 36f);
            if (WorkflowUiStyle.Button(closeRect, "CloseButton".Translate(), WorkflowButtonStyle.Quiet))
                Close();
        }
    }
}
