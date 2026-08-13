namespace AutoTranslator_Core
{
    internal static class SettingsLayoutPolicy
    {
        internal const float FixedLayoutMinimumWidth = 760f;
        internal const float FixedLayoutMinimumHeight = 520f;

        internal static bool UseFixedPrimaryLayout(int activeTab, float width, float height)
        {
            return (activeTab == 0 || activeTab == 1) &&
                   width >= FixedLayoutMinimumWidth &&
                   height >= FixedLayoutMinimumHeight;
        }
    }
}
