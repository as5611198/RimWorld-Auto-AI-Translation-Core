using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class TranslationWorkbenchTab
    {
        public static bool OpenAndFocus(ModMetaData targetMod, string category, string key, string searchText = "")
        {
            if (targetMod == null || string.IsNullOrWhiteSpace(key)) return false;

            string effectiveCategory = string.IsNullOrWhiteSpace(category) ? "Keyed" : category.Trim();
            string effectiveSearch = string.IsNullOrWhiteSpace(searchText) ? key : searchText;
            AutoTranslatorSettings.ActiveTab = 1;
            AutoTranslatorSettings.mainScrollPos = Vector2.zero;
            StartLoadingModForEditing(targetMod, new WorkbenchFocusRequest
            {
                Category = effectiveCategory,
                Key = key,
                SearchText = effectiveSearch,
                MatchedText = searchText ?? string.Empty,
                FromGlobalSearch = false
            });
            return true;
        }
    }
}
