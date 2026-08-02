using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class TranslationWorkbenchTab
    {
        internal static int CountWorkbenchBatchReplaceMatches(
            WorkbenchBatchReplaceScope scope,
            string findText,
            string replacementText,
            bool caseSensitive)
        {
            if (string.IsNullOrEmpty(findText)) return 0;

            int count = 0;
            foreach (WorkbenchItem item in GetWorkbenchBatchReplaceTargets(scope))
            {
                string current = item != null ? item.TranslatedText ?? "" : "";
                string replaced = ReplaceWorkbenchText(current, findText, replacementText ?? "", caseSensitive);
                if (!string.Equals(current, replaced, StringComparison.Ordinal)) count++;
            }

            return count;
        }

        internal static int ApplyWorkbenchBatchReplace(
            WorkbenchBatchReplaceScope scope,
            string findText,
            string replacementText,
            bool caseSensitive)
        {
            if (_editingMod == null || _isLoading || _isSavingModifications || string.IsNullOrEmpty(findText)) return 0;

            int changed = 0;
            string lastCategory = "";
            string lastKey = "";
            foreach (WorkbenchItem item in GetWorkbenchBatchReplaceTargets(scope))
            {
                if (item == null) continue;
                string current = item.TranslatedText ?? "";
                string replaced = ReplaceWorkbenchText(current, findText, replacementText ?? "", caseSensitive);
                if (string.Equals(current, replaced, StringComparison.Ordinal)) continue;

                item.TranslatedText = replaced;
                RefreshWorkbenchItemModifiedState(item);
                lastCategory = GetWorkbenchItemCategory(item);
                lastKey = item.Key ?? "";
                changed++;
            }

            if (changed > 0)
            {
                _retainedEditedCategory = lastCategory;
                _retainedEditedKey = lastKey;
                _categorizedDataVersion++;
                InvalidateVisibleItemCache();
                SetWorkbenchStatus("ATC_Workbench_BatchReplaceSuccess".Translate(changed).ToString());
            }

            return changed;
        }

        private static List<WorkbenchItem> GetWorkbenchBatchReplaceTargets(WorkbenchBatchReplaceScope scope)
        {
            IEnumerable<WorkbenchItem> targets;
            switch (scope)
            {
                case WorkbenchBatchReplaceScope.AllCategories:
                    targets = _categorizedData.SelectMany(pair => pair.Value ?? new List<WorkbenchItem>());
                    break;
                case WorkbenchBatchReplaceScope.CurrentCategory:
                    targets = IsAllWorkbenchCategoriesSelected()
                        ? _categorizedData.SelectMany(pair => pair.Value ?? new List<WorkbenchItem>())
                        : (_categorizedData.TryGetValue(_selectedCategory ?? "", out List<WorkbenchItem> categoryItems)
                            ? categoryItems ?? new List<WorkbenchItem>()
                            : new List<WorkbenchItem>());
                    break;
                default:
                    targets = GetVisibleItemsForCurrentCategory(GetCurrentWorkbenchSourceItems())
                        .Where(item => DoesWorkbenchItemMatchSearch(item, _itemSearchText));
                    break;
            }

            return targets.Where(item => item != null).Distinct().ToList();
        }

        private static string ReplaceWorkbenchText(string input, string findText, string replacementText, bool caseSensitive)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(findText)) return input ?? "";

            StringComparison comparison = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            int firstIndex = input.IndexOf(findText, comparison);
            if (firstIndex < 0) return input;

            StringBuilder builder = new StringBuilder(input.Length);
            int sourceIndex = 0;
            int matchIndex = firstIndex;
            while (matchIndex >= 0)
            {
                builder.Append(input, sourceIndex, matchIndex - sourceIndex);
                builder.Append(replacementText ?? "");
                sourceIndex = matchIndex + findText.Length;
                matchIndex = input.IndexOf(findText, sourceIndex, comparison);
            }

            builder.Append(input, sourceIndex, input.Length - sourceIndex);
            return builder.ToString();
        }
    }
}
