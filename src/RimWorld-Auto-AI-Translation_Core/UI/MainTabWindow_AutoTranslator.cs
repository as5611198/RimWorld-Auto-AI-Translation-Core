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

        public override Vector2 RequestedTabSize => GetTabSize(UI.screenWidth, UI.screenHeight);

        internal static Vector2 GetTabSize(float screenWidth, float screenHeight)
        {
            // UI.screen* already accounts for the player's UI scale. Grow the
            // editor on large logical screens; MainTabWindow still anchors it.
            return new Vector2(
                Mathf.Min(screenWidth, Mathf.Max(1010f, screenWidth * 0.85f)),
                Mathf.Min(screenHeight - 35f, Mathf.Max(684f, (screenHeight - 35f) * 0.9f)));
        }

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
