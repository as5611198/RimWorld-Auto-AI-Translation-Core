using AutoTranslator_Core;
using System;
using System.Collections.Generic;

namespace TranslationTaskTierSelfTest
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                ApiKeyConfig bulk = new ApiKeyConfig { Name = "bulk", TaskTier = TranslationTaskTier.Bulk };
                ApiKeyConfig standard = new ApiKeyConfig { Name = "standard", TaskTier = TranslationTaskTier.Standard };
                ApiKeyConfig precision = new ApiKeyConfig { Name = "precision", TaskTier = TranslationTaskTier.Precision };
                Assert(TranslationTaskTierRouter.SelectEligible(new[] { bulk, standard, precision }, TranslationTaskTier.Bulk)[0] == bulk,
                    "bulk task used a higher tier");
                Assert(TranslationTaskTierRouter.SelectEligible(new[] { bulk, standard, precision }, TranslationTaskTier.Standard)[0] == standard,
                    "standard exact tier not selected");
                Assert(TranslationTaskTierRouter.SelectEligible(new[] { bulk, standard }, TranslationTaskTier.Precision)[0] == standard,
                    "precision did not fall back to standard");
                Assert(TranslationTaskTierRouter.SelectEligible(new[] { bulk }, TranslationTaskTier.Precision)[0] == bulk,
                    "precision did not fall back to bulk");
                Assert(TranslationTaskTierRouter.SelectEligible(new[] { standard, precision }, TranslationTaskTier.Bulk).Count == 0,
                    "bulk incorrectly fell upward");
                Console.WriteLine("PASS: 5 translation task tier assertions");
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
    }
}
