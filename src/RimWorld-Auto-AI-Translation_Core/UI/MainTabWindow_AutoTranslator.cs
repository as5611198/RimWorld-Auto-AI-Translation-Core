using RimWorld;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public class MainButtonWorker_AutoTranslator : MainButtonWorker_ToggleTab
    {
        public override bool Visible
        {
            get
            {
                AutoTranslatorSettings settings = AutoTranslatorMod.Settings;
                return base.Visible && (settings == null || settings.ShowWorldMainButton);
            }
        }
    }

    public class MainTabWindow_AutoTranslator : MainTabWindow
    {
        private AutoTranslatorMod mod;

        public override Vector2 RequestedTabSize => new Vector2(1010f, 684f);

        public override void DoWindowContents(Rect inRect)
        {
            GetMod()?.DoSettingsWindowContents(inRect);
        }

        public override void PreClose()
        {
            GetMod()?.WriteSettings();
            base.PreClose();
        }

        private AutoTranslatorMod GetMod()
        {
            if (mod == null)
            {
                mod = LoadedModManager.GetMod<AutoTranslatorMod>();
            }

            return mod;
        }
    }
}
