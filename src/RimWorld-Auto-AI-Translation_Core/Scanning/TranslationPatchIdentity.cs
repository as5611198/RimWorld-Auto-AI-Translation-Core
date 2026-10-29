using System;
using System.Text.RegularExpressions;

namespace AutoTranslator_Core
{
    // Shared by target selection and persisted unresolved reports. No filesystem or
    // ModLister access: report queries may run before mod metadata is initialized.
    internal static class TranslationPatchIdentity
    {
        private static readonly Regex LocaleLabel = new Regex(
            @"(?:^|[\s_\-\[(])(?:zh|chs|cht|zhcn|zhtw|zh-hans|zh-hant)(?:$|[\s_\-\])])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool IsMatch(string packageId, string modName)
        {
            if (string.IsNullOrEmpty(packageId)) return false;
            string pid = packageId.ToLowerInvariant();
            string name = (modName ?? string.Empty).ToLowerInvariant();
            if (pid.StartsWith("zh.", StringComparison.Ordinal) ||
                pid.Contains("chinesepack") || pid.Contains("chinese-pack") ||
                pid.StartsWith("rwzh.", StringComparison.Ordinal) ||
                name.Contains("zh-pack") || name.Contains("chinese pack") || name.Contains("chinesepack") ||
                LocaleLabel.IsMatch(name)) return true;

            string[] keywords = { "漢化", "汉化", "翻譯", "翻译", "translation", "language", "l10n", "中文", "zh-tw", "zh-cn", "簡繁", "简繁", "繁簡", "繁简" };
            foreach (string keyword in keywords)
                if (name.Contains(keyword)) return true;

            string[] suffixes = { ".zh", "_zh", "-zh", "zh-pack", ".zhtc", "_zhtc", "-zhtc", ".zhcn", "_zhcn", "-zhcn", ".cn", "_cn", "-cn", ".tw", "_tw", "-tw", "l10n" };
            foreach (string suffix in suffixes)
                if (pid.EndsWith(suffix, StringComparison.Ordinal) || pid.Contains(suffix + ".") || pid.Contains(suffix + "_")) return true;
            return pid.EndsWith("zh", StringComparison.Ordinal);
        }
    }
}
