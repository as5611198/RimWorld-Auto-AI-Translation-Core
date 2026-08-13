using AutoTranslator_Core;
using System;

internal static class Program
{
    private static int Main()
    {
        try
        {
            SettingsWindowSize laptop = SettingsWindowSizePolicy.Resolve(1366f, 768f);
            AssertClose(laptop.Width, 1065.48f, "1366 window width");
            AssertClose(laptop.Height, 700f, "1366 window height");
            Assert(SettingsLayoutPolicy.UseFixedPrimaryLayout(0, laptop.Width - 36f, laptop.Height - 176f),
                "1366x768-class main console should not use an outer scroll view");

            SettingsWindowSize compact = SettingsWindowSizePolicy.Resolve(1280f, 720f);
            AssertClose(compact.Width, 998.4f, "1280 window width");
            AssertClose(compact.Height, 680f, "720p safe-height clamp");
            Assert(!SettingsLayoutPolicy.UseFixedPrimaryLayout(1, compact.Width - 36f, compact.Height - 176f),
                "720p workbench should use the compatibility outer scroll fallback");

            SettingsWindowSize large = SettingsWindowSizePolicy.Resolve(2560f, 1440f);
            AssertClose(large.Width, 1180f, "large-screen maximum width");
            AssertClose(large.Height, 900f, "large-screen maximum height");
            Assert(SettingsLayoutPolicy.UseFixedPrimaryLayout(0, 1600f, 900f),
                "1080p/2K/4K effective layouts should stay fixed");
            Assert(SettingsLayoutPolicy.UseFixedPrimaryLayout(0, 800f, 520f),
                "scaled layout at the documented minimum should stay fixed");
            Assert(!SettingsLayoutPolicy.UseFixedPrimaryLayout(0, 759f, 520f),
                "narrow layout must fall back to outer scrolling");
            Assert(!SettingsLayoutPolicy.UseFixedPrimaryLayout(1, 800f, 519f),
                "short layout must fall back to outer scrolling");
            Assert(!SettingsLayoutPolicy.UseFixedPrimaryLayout(2, 1600f, 900f),
                "cloud tab retains its content scrolling behavior");
            SettingsWindowSize tiny = SettingsWindowSizePolicy.Resolve(800f, 600f);
            AssertClose(tiny.Width, 760f, "tiny-screen safe width");
            AssertClose(tiny.Height, 560f, "tiny-screen safe height");
            Console.WriteLine("PASS: responsive main/workbench fixed-layout thresholds");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertClose(float actual, float expected, string message)
    {
        if (Math.Abs(actual - expected) > 0.02f)
            throw new InvalidOperationException(message + ": expected " + expected + ", got " + actual);
    }
}
