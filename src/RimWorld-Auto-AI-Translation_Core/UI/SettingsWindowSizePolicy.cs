using System;

namespace AutoTranslator_Core
{
    internal struct SettingsWindowSize
    {
        internal float Width;
        internal float Height;
    }

    internal static class SettingsWindowSizePolicy
    {
        internal const float BaseWidth = 900f;
        internal const float BaseHeight = 700f;
        internal const float MaximumWidth = 1180f;
        internal const float MaximumHeight = 900f;
        internal const float ScreenMargin = 40f;

        internal static SettingsWindowSize Resolve(float screenWidth, float screenHeight)
        {
            if (screenWidth <= 0f || screenHeight <= 0f)
            {
                return new SettingsWindowSize { Width = BaseWidth, Height = BaseHeight };
            }

            // The maximum must never be larger than the logical screen. At high
            // UI scale a 1366x768 display can expose fewer than 540 logical
            // vertical pixels; forcing the old floor would place controls outside
            // the screen before the content-level scroll fallback could help.
            float safeMaximumWidth = Math.Max(1f, screenWidth - ScreenMargin);
            float safeMaximumHeight = Math.Max(1f, screenHeight - ScreenMargin);
            float desiredWidth = Clamp(screenWidth * 0.78f, BaseWidth, MaximumWidth);
            float desiredHeight = Clamp(screenHeight * 0.84f, BaseHeight, MaximumHeight);

            return new SettingsWindowSize
            {
                Width = Math.Min(desiredWidth, safeMaximumWidth),
                Height = Math.Min(desiredHeight, safeMaximumHeight)
            };
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
