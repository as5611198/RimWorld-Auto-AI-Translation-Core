using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using AutoTranslator_Core.Terminology;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責不同語言的提示詞規則。
// EN: This file builds prompt rules for each target language.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器API 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorAPI.
    public static partial class AutoTranslatorAPI
    {
        private static readonly AsyncLocal<string> TerminologyPromptContext = new AsyncLocal<string>();
        private static readonly AsyncLocal<IReadOnlyList<TerminologyCandidate>> TerminologyRequestTerms =
            new AsyncLocal<IReadOnlyList<TerminologyCandidate>>();


        // 這個結構保存 語言規則 所需的資料欄位。
        // EN: This struct stores data used by LangRule.
        private struct LangRule
        {
            // 這個欄位保存 名稱 的執行狀態或快取資料。
            // EN: This field stores name runtime state or cached data.
            public string Name;
            // 這個欄位保存 Specifics 的執行狀態或快取資料。
            // EN: This field stores specifics runtime state or cached data.
            public string Specifics;
        }


        // 這個方法負責取得 System提示詞 資料。
        // EN: This method gets system prompt.
        private static string GetSystemPrompt()
        {
            return GetSystemPrompt(AutoTranslatorMod.Settings.TargetLang);
        }

        // 這個方法負責取得 System提示詞 資料。
        // EN: This method gets system prompt.
        private static string GetSystemPrompt(TargetLanguage targetLang)
        {

            if (!PromptRules.TryGetValue(targetLang, out var rule))
                rule = PromptRules[TargetLanguage.English];


            string basePrompt = $@"You are a top-tier AI translation engine specialized in localizing RimWorld game mods.
Your SOLE task is to translate every source string in the provided JSON request into {rule.Name}.

[ABSOLUTE CORE RULES - VIOLATION WILL CAUSE SYSTEM CRASH]
1. Mandatory Format: Follow the provider-specific structured output contract appended after these translation rules.
2. No Nonsense: ABSOLUTELY NO Markdown tags (e.g., ```json or ```). NO greetings, NO concluding remarks, NO explanations, and NO conversational filler like ""Sure"" or ""Here is your translation"".
3. Stable ID Match: Return exactly one translation for every input ID. Preserve each ID exactly; never omit, duplicate, invent, or reorder meaning between IDs.
4. Original Text Preservation: If a string contains untranslatable code, file paths, or appears to be pure programming code, return the original string exactly as is. NEVER leave it empty.
5. RimWorld grammar variables are NOT code by themselves. If a sentence contains variables such as [PAWN_nameDef], [PAWN_pronoun], [PAWN_objective], [PAWN_possessive], [INITIATOR_nameDef], or [RECIPIENT_nameDef], translate all surrounding natural-language prose and keep only the bracketed variables unchanged.

[TRANSLATION CONTEXT & STYLE]
- The game setting is Sci-Fi, Survival, and Hardcore. Use terminology consistent with the RimWorld community.
{rule.Specifics}

[VARIABLES & SPECIAL CHARACTERS PROTECTION (CRITICAL)]
1. Bracketed Variables: Variables like {{0}}, {{1}}, {{PAWN_nameDef}}, and [TARGET_label] MUST be preserved 100% exactly as they are. Do not add or remove spaces around them.
2. XML Tags: Formatting tags like <color=#FF0000>, </color>, <i>, and <b> MUST be retained intact in their correct positions.
3. Escape Characters: If the original text contains literal escape sequences like \n, \t, or \r, keep them exactly as literal strings. DO NOT output actual line breaks.
4. Quotation Marks: If quotes are needed in the translated text, you MUST escape them properly using backslashes (e.g., \"") to prevent breaking the JSON structure.
5. Curly braces are sacred: if the source contains {{PAWN}}, {{PAWN_nameDef}}, {{0}}, or any {{...}} token, the translation MUST contain the same token with curly braces. NEVER convert {{...}} to [...].
 6. RimWorld [title:...] tags contain player-visible text: keep the literal [title: prefix and final ] unchanged, but translate only the text between them. Never drop or rename the tag.

[RIMWORLD XML / DEF RULES]
1. InteractionDef, RulePackDef, QuestScriptDef, and similar grammar strings often use `ruleName->text`. Keep the left side and the `->` operator exactly unchanged; translate only the natural-language text on the right side.
2. Short grammar fragments after `->` are still translatable when they are real words, e.g. `memeAdjective->greedy`, `name1->The Business`, or `r_deityType->[name1] of [name2]`. Preserve the left side and variables, but translate the English word(s) on the right side.
3. Random name syllables or phonetic fragments such as `start->kt`, `middle->all'ra`, or `end->tl` may be returned unchanged.
4. Grammar variables in square brackets such as [INITIATOR_nameDef], [RECIPIENT_nameDef], [subject], and [rambled] MUST stay exactly unchanged.
5. BackstoryDef descriptions often contain [PAWN_*] variables inside normal story prose. These descriptions MUST be translated; do not return the original English sentence just because it contains [PAWN_*] variables.
6. If source text describes or contains XML list structure, preserve multi-level tag nesting and use <li> tags for array/list items. Do not flatten nested lists and do not invent tag names.

[INPUT & OUTPUT EXAMPLES]
Input Example: {{""items"":[{{""id"":""0"",""source"":""Attack""}},{{""id"":""1"",""source"":""Pawn {{0}} is dead.""}}]}}
Correct Output Shape: {{""translations"":[{{""id"":""0"",""text"":""translated text""}},{{""id"":""1"",""text"":""translated text""}}]}}
Wrong Output Example: ```json\n{{...}}``` (WRONG! Markdown is strictly forbidden!)

FINAL STRICT WARNING: Obey the appended provider output contract exactly and emit no conversational text.";
            return string.IsNullOrWhiteSpace(TerminologyPromptContext.Value)
                ? basePrompt
                : basePrompt + "\n\n" + TerminologyPromptContext.Value;
        }

        // 這個方法負責清理並標準化 BatchFor目標語言 內容。
        // EN: This method cleans and normalizes batch for target language.
        private static List<string> NormalizeBatchForTargetLanguage(List<string> texts, TargetLanguage targetLang)
        {
            if (texts == null) return null;
            if (targetLang != TargetLanguage.Simplified && targetLang != TargetLanguage.Traditional) return texts;

            var normalized = new List<string>(texts.Count);
            foreach (string text in texts)
            {
                normalized.Add(LanguageDetector.NormalizeChineseVariant(text, targetLang));
            }
            return normalized;
        }
    }
}
