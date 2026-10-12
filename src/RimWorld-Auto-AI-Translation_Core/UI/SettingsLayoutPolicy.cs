namespace AutoTranslator_Core
{
    internal static class SettingsLayoutPolicy
    {
        internal const float FixedLayoutMinimumWidth = 760f;
        internal const float FixedLayoutMinimumHeight = 520f;

        internal static bool UseFixedPrimaryLayout(int activeTab, float width, float height)
        {
            return (activeTab == AutoTranslatorSettings.WorkbenchTabIndex ||
                    activeTab == AutoTranslatorSettings.EditorTabIndex) &&
                   width >= FixedLayoutMinimumWidth &&
                   height >= FixedLayoutMinimumHeight;
        }
    }
}
