using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    internal enum TutorialSection { GettingStarted, Settings, Workbench, Cloud }

    public class TutorialWindow : Window
    {
        private Vector2 scrollPos;
        private readonly bool batchXmlOnly;
        private TutorialSection section;
        public override Vector2 InitialSize => new Vector2(900f, 740f);

        public TutorialWindow()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnAccept = false;
        }

        internal TutorialWindow(bool batchXmlOnly) : this() { this.batchXmlOnly = batchXmlOnly; }

        public override void PostOpen()
        {
            base.PostOpen();
            if (!batchXmlOnly && AutoTranslatorMod.Settings != null && !AutoTranslatorMod.Settings.HasSeenUsageGuide)
            {
                AutoTranslatorMod.Settings.HasSeenUsageGuide = true;
                LoadedModManager.GetMod<AutoTranslatorMod>()?.WriteSettings();
            }
        }

        internal static string SectionLabelKey(TutorialSection value) => "ATC_Guide_Area_" + value;
        internal static string SectionContentKey(TutorialSection value) => "ATC_Guide_Content_" + value;
        internal static int DestinationTab(TutorialSection value)
        {
            switch (value)
            {
                case TutorialSection.Workbench: return 1;
                case TutorialSection.Cloud: return 2;
                default: return 3;
            }
        }

        private void Navigate(int tab)
        {
            AutoTranslatorSettings.ActiveTab = tab;
            AutoTranslatorSettings.mainScrollPos = Vector2.zero;
            Close();
        }

        public override void DoWindowContents(Rect inRect)
        {
            bool previousBypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
            GameFont previousFont = Text.Font;
            bool previousWrap = Text.WordWrap;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, 0f, inRect.width - 26f, 36f),
                    (batchXmlOnly ? "ATC_BatchXml_Title" : "ATC_Guide_Title").Translate());
                Text.Font = GameFont.Small;
                Text.WordWrap = true;
                float top = 42f;
                if (!batchXmlOnly)
                {
                    string introduction = "ATC_Guide_Introduction".Translate();
                    float introductionHeight = Text.CalcHeight(introduction, inRect.width);
                    Widgets.Label(new Rect(0f, top, inRect.width, introductionHeight), introduction);
                    top += introductionHeight + 34f;
                    var tabs = new List<TabRecord>();
                    foreach (TutorialSection value in Enum.GetValues(typeof(TutorialSection)))
                    {
                        TutorialSection captured = value;
                        tabs.Add(new TabRecord(SectionLabelKey(value).Translate(), () =>
                        {
                            section = captured;
                            scrollPos = Vector2.zero;
                        }, section == value));
                    }
                    TabDrawer.DrawTabs(new Rect(0f, top, inRect.width, 30f), tabs);
                    top += 14f;
                }

                string content = batchXmlOnly ? "ATC_BatchXml_TutorialText".Translate().ToString()
                    : SectionContentKey(section).Translate().ToString();
                if (!batchXmlOnly && section == TutorialSection.Workbench)
                    content += "\n\n" + "ATC_BatchXml_TutorialText".Translate();
                Rect outRect = new Rect(0f, top, inRect.width, Mathf.Max(50f, inRect.height - top - 56f));
                float textHeight = Text.CalcHeight(content, outRect.width - 24f);
                Rect viewRect = new Rect(0f, 0f, outRect.width - 20f, Mathf.Max(outRect.height - 1f, textHeight + 12f));
                Widgets.BeginScrollView(outRect, ref scrollPos, viewRect);
                Widgets.Label(new Rect(0f, 0f, viewRect.width, textHeight), content);
                Widgets.EndScrollView();

                float bottom = inRect.height - 38f;
                if (!batchXmlOnly)
                {
                    float buttonWidth = (inRect.width - 24f) / 3f;
                    string destination = section == TutorialSection.Workbench ? "ATC_Guide_OpenWorkbench"
                        : section == TutorialSection.Cloud ? "ATC_Guide_OpenCloud" : "ATC_Guide_OpenSettings";
                    if (Widgets.ButtonText(new Rect(0f, bottom, buttonWidth, 34f), destination.Translate())) Navigate(DestinationTab(section));
                    if (Widgets.ButtonText(new Rect(buttonWidth + 12f, bottom, buttonWidth, 34f), "ATC_Guide_OpenTasks".Translate())) Navigate(0);
                    if (Widgets.ButtonText(new Rect((buttonWidth + 12f) * 2f, bottom, buttonWidth, 34f), "ATC_Guide_Close".Translate())) Close();
                }
                else if (Widgets.ButtonText(new Rect(inRect.width - 180f, bottom, 180f, 34f), "ATC_Guide_Close".Translate())) Close();
            }
            finally
            {
                Text.Font = previousFont;
                Text.WordWrap = previousWrap;
                Patch_GUI_Label_GUIContent.BypassInterceptor = previousBypass;
            }
        }
    }
}
