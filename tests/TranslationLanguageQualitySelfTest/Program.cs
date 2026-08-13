using AutoTranslator_Core;
using AutoTranslator_Core.TargetedHardcodedUi;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TranslationLanguageQualitySelfTest
{
    internal static class Program
    {
        private static int _passed;

        private static int Main()
        {
            try
            {
                Run("unchanged and reformatted English are rejected", TestUnchangedEnglish);
                Run("English paraphrases are rejected", TestEnglishParaphrase);
                Run("English output from a Chinese source is rejected", TestEnglishFromChineseSource);
                Run("short English phrases are rejected", TestShortEnglishPhrase);
                Run("ordinary single-word English is rejected", TestSingleWordEnglishResidual);
                Run("Traditional Chinese with model names is accepted", TestTraditionalWithModelNames);
                Run("intentional model tokens are accepted", TestIntentionalModelTokens);
                Run("known UI tokens are rejected when unchanged", TestKnownUiTokens);
                Run("Chinese variant normalization", TestChineseVariantNormalization);
                Run("wrong Chinese variants are rejected", TestWrongChineseVariant);
                Run("unexpected writing systems are rejected", TestUnexpectedWritingSystems);
                Run("ambiguous same-form Han characters are not rejected", TestAmbiguousHanCharacters);
                Run("grammar and protected tokens remain acceptable", TestGrammarAndProtectedTokens);
                Run("persisted generated Chinese values are filtered per entry", TestPersistedGeneratedValues);
                Run("stale flattened aggregate keys are removed", TestStaleAggregateCleanup);
                Run("Keyed output ownership is exact", TestKeyedOutputOwnership);
                Run("Keyed migration discovers only exact legacy files", TestKeyedMigrationFileDiscovery);
                Run("English target accepts English", TestEnglishTarget);
                Run("hardcoded UI dictionary is role-aware and validated", TestHardcodedUiDictionary);

                Console.WriteLine("PASS: " + _passed + " translation language quality self-tests");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL: " + ex);
                return 1;
            }
        }

        private static void TestUnchangedEnglish()
        {
            const string source =
                "Solar crystals are a special type of crystal that can store energy for later use.";
            const string reformatted =
                "Solar crystals are a special type of crystal\\nthat can store energy for later use.";

            AssertFalse(
                TranslationResultLanguagePolicy.ShouldAccept(source, source, TargetLanguage.Traditional),
                "Unchanged English sentence");
            AssertFalse(
                TranslationResultLanguagePolicy.ShouldAccept(reformatted, source, TargetLanguage.Traditional),
                "Whitespace/newline-only English change");
        }

        private static void TestHardcodedUiDictionary()
        {
            AssertTrue(
                HardcodedUiBuiltInDictionary.TryTranslate(
                    "Close", "button", TargetLanguage.Simplified, out string translated) &&
                translated == "关闭",
                "button dictionary hit");
            AssertFalse(
                HardcodedUiBuiltInDictionary.TryTranslate(
                    "Close", "label", TargetLanguage.Simplified, out _),
                "same source with wrong semantic role");
            AssertFalse(
                HardcodedUiBuiltInDictionary.IsValidTranslation(
                    "Save {0}", "保存", TargetLanguage.Simplified),
                "placeholder loss");
            AssertFalse(
                HardcodedUiBuiltInDictionary.IsValidTranslation(
                    "Save", "Save", TargetLanguage.Simplified),
                "unchanged source");
        }

        private static void TestEnglishParaphrase()
        {
            const string source =
                "The Bishop unit can protect nearby allies and restore their shields.";
            const string paraphrase =
                "Nearby allies receive shield restoration and protection from this Bishop unit.";
            AssertFalse(
                TranslationResultLanguagePolicy.ShouldAccept(paraphrase, source, TargetLanguage.Traditional),
                "Complete English paraphrase");
        }

        private static void TestEnglishFromChineseSource()
        {
            AssertFalse(
                TranslationResultLanguagePolicy.ShouldAccept(
                    "This unit stores energy for nearby allies.",
                    "这个单位为附近的盟友储存能量。",
                    TargetLanguage.Traditional),
                "English sentence returned from a Simplified Chinese input");
        }

        private static void TestShortEnglishPhrase()
        {
            foreach (string phrase in new[] { "Energy core", "Blue color" })
            {
                AssertFalse(
                    TranslationResultLanguagePolicy.ShouldAccept(
                        phrase,
                        phrase,
                        TargetLanguage.Traditional),
                    "Short English phrase " + phrase);
            }

            AssertTrue(
                TranslationResultLanguagePolicy.ShouldAccept(
                    "Rook III",
                    "Rook III",
                    TargetLanguage.Traditional),
                "Model name with Roman-numeral designator");
        }

        private static void TestTraditionalWithModelNames()
        {
            AssertTrue(
                TranslationResultLanguagePolicy.ShouldAccept(
                    "Bishop I 與 Rook III 都會保護附近友軍並恢復護盾。",
                    "The Bishop I and Rook III protect nearby allies.",
                    TargetLanguage.Traditional),
                "Traditional sentence containing model names");
        }

        private static void TestSingleWordEnglishResidual()
        {
            foreach (string word in new[] { "Column", "strogestation" })
            {
                AssertFalse(
                    TranslationResultLanguagePolicy.ShouldAccept(
                        word,
                        word,
                        TargetLanguage.Traditional),
                    "Single-word English residual " + word);
            }
        }

        private static void TestIntentionalModelTokens()
        {
            foreach (string token in new[] { "CPU", "IED", "GAU4", "Kord", "THICC", "NPC" })
            {
                AssertTrue(
                    TranslationResultLanguagePolicy.ShouldAccept(token, token, TargetLanguage.Traditional),
                    "Intentional token " + token);
            }
        }

        private static void TestKnownUiTokens()
        {
            AssertFalse(
                TranslationResultLanguagePolicy.ShouldAccept("None", "None", TargetLanguage.Traditional),
                "Known UI token None");
        }

        private static void TestChineseVariantNormalization()
        {
            AssertEqual(
                "騎士衝鋒",
                LanguageDetector.NormalizeChineseVariant("骑士冲锋", TargetLanguage.Traditional),
                "Milira charge text");
            AssertEqual(
                "涅瓦蓮母艦著陸儲存",
                LanguageDetector.NormalizeChineseVariant("涅瓦莲母舰着陆储存", TargetLanguage.Traditional),
                "Nivarian tutorial text");
            AssertEqual(
                "骑士冲锋",
                LanguageDetector.NormalizeChineseVariant("騎士衝鋒", TargetLanguage.Simplified),
                "Reverse Simplified conversion");
            AssertEqual(
                "記錄了關於米莉拉訊息的卷宗。",
                LanguageDetector.NormalizeChineseVariant("记录了关于米莉拉信息的卷宗。", TargetLanguage.Traditional),
                "Taiwan information terminology");
            AssertEqual(
                "防禦砲手吃洋芋片。",
                LanguageDetector.NormalizeChineseVariant("防御炮手吃薯片。", TargetLanguage.Traditional),
                "Taiwan gameplay terminology");
            AssertEqual(
                "防御炮手吃薯片。",
                LanguageDetector.NormalizeChineseVariant("防禦砲手吃洋芋片。", TargetLanguage.Simplified),
                "Reverse Mainland terminology");
        }

        private static void TestWrongChineseVariant()
        {
            AssertTrue(
                LanguageDetector.HasWrongChineseVariant("骑士冲锋", TargetLanguage.Traditional),
                "Simplified markers under Traditional target");
            AssertFalse(
                TranslationResultLanguagePolicy.ShouldAccept(
                    "骑士冲锋",
                    "Knight charge",
                    TargetLanguage.Traditional),
                "Wrong Simplified output");
            AssertFalse(
                TranslationResultLanguagePolicy.ShouldAccept(
                    "騎士冲鋒",
                    "Knight charge",
                    TargetLanguage.Traditional),
                "Mixed Traditional/Simplified output");
            AssertTrue(
                LanguageDetector.HasWrongChineseVariant("騎士衝鋒", TargetLanguage.Simplified),
                "Traditional markers under Simplified target");
            AssertFalse(
                LanguageDetector.HasWrongChineseVariant("騎士衝鋒", TargetLanguage.Traditional),
                "Correct Traditional output");
        }

        private static void TestAmbiguousHanCharacters()
        {
            AssertFalse(
                LanguageDetector.HasWrongChineseVariant("重干里游群余云征采", TargetLanguage.Traditional),
                "Ambiguous or same-form characters");
        }

        private static void TestUnexpectedWritingSystems()
        {
            foreach (string wrongLanguage in new[]
            {
                "ランダムクエストアイテム",
                "무작위 퀘스트 아이템",
                "Случайный предмет задания"
            })
            {
                AssertFalse(
                    TranslationResultLanguagePolicy.ShouldAccept(
                        wrongLanguage,
                        "Random quest item",
                        TargetLanguage.Traditional),
                    "Wrong writing system " + wrongLanguage);
            }

            AssertFalse(
                TranslationResultLanguagePolicy.ShouldAccept(
                    "奇妙の音效",
                    "Strange sound",
                    TargetLanguage.Traditional),
                "Unexpected mixed Japanese");
            AssertTrue(
                TranslationResultLanguagePolicy.ShouldAccept(
                    "角色名稱是初音ミク。",
                    "The character name is 初音ミク.",
                    TargetLanguage.Traditional),
                "Source-preserved Japanese proper name");
            AssertTrue(
                TranslationResultLanguagePolicy.ShouldAccept(
                    "初音ミク",
                    "初音ミク",
                    TargetLanguage.Traditional),
                "Short unchanged foreign proper name");
            AssertTrue(
                TranslationResultLanguagePolicy.TryNormalizePersistedGeneratedValue(
                    "一段中文（俄語：Автомат Фёдорова）",
                    TargetLanguage.Traditional,
                    out _),
                "Persisted Chinese with a preserved foreign name");
            AssertTrue(
                TranslationResultLanguagePolicy.TryNormalizePersistedGeneratedValue(
                    "中文表情 (๑•̀ㅂ́)و✧",
                    TargetLanguage.Traditional,
                    out _),
                "Persisted Chinese emoticon");
        }

        private static void TestGrammarAndProtectedTokens()
        {
            AssertTrue(
                TranslationResultLanguagePolicy.ShouldAccept(
                    "memeAdjective->思想開放的",
                    "memeAdjective->open-minded",
                    TargetLanguage.Traditional),
                "Translatable grammar rule");
            AssertTrue(
                TranslationResultLanguagePolicy.ShouldAccept(
                    "[PAWN_nameDef] 已獲得 {0} 點能量。",
                    "[PAWN_nameDef] gained {0} energy.",
                    TargetLanguage.Traditional),
                "Protected tokens with target-language text");
        }

        private static void TestPersistedGeneratedValues()
        {
            foreach (string residual in new[] { "Column", "strogestation", "This unit stores energy." })
            {
                AssertFalse(
                    TranslationResultLanguagePolicy.TryNormalizePersistedGeneratedValue(
                        residual,
                        TargetLanguage.Traditional,
                        out _),
                    "Persisted English residual " + residual);
            }

            AssertTrue(
                TranslationResultLanguagePolicy.TryNormalizePersistedGeneratedValue(
                    "防御炮塔",
                    TargetLanguage.Traditional,
                    out string normalizedTraditional),
                "Persisted Simplified Chinese can be normalized safely");
            AssertEqual("防禦砲塔", normalizedTraditional, "Persisted Traditional normalization");
            AssertTrue(
                TranslationResultLanguagePolicy.TryNormalizePersistedGeneratedValue(
                    "奇妙の音效",
                    TargetLanguage.Traditional,
                    out _),
                "Ambiguous persisted mixed-script name waits for source-aware validation");
            AssertFalse(
                TranslationResultLanguagePolicy.TryNormalizePersistedGeneratedValue(
                    "ランダムクエストアイテム",
                    TargetLanguage.Traditional,
                    out _),
                "Persisted full Japanese output under Traditional target");
            AssertTrue(
                TranslationResultLanguagePolicy.TryNormalizePersistedGeneratedValue(
                    "防禦砲塔",
                    TargetLanguage.Traditional,
                    out _),
                "Persisted Traditional Chinese");
            AssertTrue(
                TranslationResultLanguagePolicy.TryNormalizePersistedGeneratedValue(
                    "Kord",
                    TargetLanguage.Traditional,
                    out _),
                "Persisted proper name");
            AssertTrue(
                TranslationResultLanguagePolicy.TryNormalizePersistedGeneratedValue(
                    "Rook III",
                    TargetLanguage.Traditional,
                    out _),
                "Persisted model designation");
            AssertTrue(
                TranslationResultLanguagePolicy.TryNormalizePersistedGeneratedValue(
                    "Texte français valide.",
                    TargetLanguage.French,
                    out _),
                "Latin target is not filtered without source text");
        }

        private static void TestEnglishTarget()
        {
            AssertTrue(
                TranslationResultLanguagePolicy.ShouldAccept(
                    "The unit is ready.",
                    "The unit is ready.",
                    TargetLanguage.English),
                "English target");
        }

        private static void TestKeyedOutputOwnership()
        {
            HashSet<string> owned = TranslationGeneratedOutputOwnership.BuildKeyedFileNameSet(
                "foo.bar",
                new[] { "Keys.xml", "sub\\Extra.xml" });
            AssertTrue(owned.Contains("foo_bar_Keys.xml"), "Primary keyed output");
            AssertTrue(owned.Contains("foo_bar_Extra.xml"), "Nested keyed output");
            AssertTrue(
                TranslationGeneratedOutputOwnership.IsOwnedKeyedFile(
                    "foo_bar_Keys.xml",
                    "foo.bar",
                    owned),
                "Exact package output");
            AssertFalse(
                TranslationGeneratedOutputOwnership.IsOwnedKeyedFile(
                    "foo_bar_addon_Keys.xml",
                    "foo.bar",
                    owned),
                "Addon package output must not be claimed");
        }

        private static void TestStaleAggregateCleanup()
        {
            List<string> staleKeys = TranslationGeneratedOutputCleanup.FindStaleAggregateKeys(
                new[]
                {
                    "DMS_Hypothesis.generalRules.rulesStrings",
                    "DMS_Hypothesis.generalRules.rulesStrings.0",
                    "DMS_Hypothesis.generalRules.rulesStrings.1",
                    "Legacy.IsolatedText",
                    "Current.Parent"
                },
                new[]
                {
                    "DMS_Hypothesis.generalRules.rulesStrings.0",
                    "DMS_Hypothesis.generalRules.rulesStrings.1",
                    "Current.Parent",
                    "Current.Parent.description"
                });

            AssertEqual(1, staleKeys.Count, "Stale aggregate count");
            AssertEqual(
                "DMS_Hypothesis.generalRules.rulesStrings",
                staleKeys[0],
                "Stale aggregate key");
        }

        private static void TestKeyedMigrationFileDiscovery()
        {
            string root = Path.Combine(Path.GetTempPath(), "atc-keyed-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                foreach (string fileName in new[]
                {
                    "foo_bar_A.xml",
                    "foo_bar_B.xml",
                    "foo_bar_AutoTranslated.xml",
                    "foo_bar_addon_A.xml"
                })
                {
                    File.WriteAllText(Path.Combine(root, fileName), "<LanguageData />");
                }

                HashSet<string> owned = TranslationGeneratedOutputOwnership.BuildKeyedFileNameSet(
                    "foo.bar",
                    new[] { "A.xml", "nested\\B.xml" });
                List<string> discovered = Directory.GetFiles(root, "*.xml")
                    .Where(file => TranslationGeneratedOutputOwnership.IsOwnedKeyedFile(
                        file,
                        "foo.bar",
                        owned))
                    .Select(Path.GetFileName)
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                AssertEqual(3, discovered.Count, "Exact migration candidate count");
                AssertTrue(discovered.Contains("foo_bar_A.xml"), "First legacy file");
                AssertTrue(discovered.Contains("foo_bar_B.xml"), "Nested legacy file");
                AssertTrue(discovered.Contains("foo_bar_AutoTranslated.xml"), "Canonical file");
                AssertFalse(discovered.Contains("foo_bar_addon_A.xml"), "Addon file exclusion");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void Run(string name, Action test)
        {
            test();
            _passed++;
            Console.WriteLine("PASS: " + name);
        }

        private static void AssertTrue(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message + " should be true.");
        }

        private static void AssertFalse(bool value, string message)
        {
            if (value) throw new InvalidOperationException(message + " should be false.");
        }

        private static void AssertEqual<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    message + ": expected <" + expected + ">, actual <" + actual + ">.");
            }
        }
    }
}
