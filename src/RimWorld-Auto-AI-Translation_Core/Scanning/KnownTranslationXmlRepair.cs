using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

namespace AutoTranslator_Core
{
    // Fingerprint, complete relative source path and strict reparse are all
    // required. Updating a Workshop file automatically opts it out of a rule.
    public static class KnownTranslationXmlRepair
    {
        public static bool TryRepair(string path, string input, out string repaired, out string rule)
        {
            repaired = input; rule = null;
            if (input == null || input.StartsWith("base64,", StringComparison.Ordinal)) return false;
            string p = (path ?? "").Replace('\\', '/');
            string hash = TranslationRepairJournal.Hash(input.Replace("\r\n", "\n"));
            Func<string, string, bool> matches = (suffix, fingerprint) => p.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase) && hash == fingerprint;
            if (matches("3394862242/Languages/English/DefInjected/ThingDef/Weapons_Moon.xml", "d6c22b464f08a7c523f461e26a74c2d7ea1ede5abb06d54c4a93f8d55ed2fb75"))
            {
                repaired = input.Replace("&lt;i&mt;", "&lt;i&gt;").Replace("&lt;/i&mt;", "&lt;/i&gt;"); rule = "ratkin-italic-entity";
            }
            else if (matches("3421959154/Languages/ChineseTraditional (繁體中文)/DefInjected/RecipeDef/127x55mm.xml", "5458616b8ac49feca7a53bd0f3cb3aa87731542f3ea34d3f1ff4285038c51384"))
            { repaired = "<" + input; rule = "cinders-xml-declaration"; }
            else if (matches("3421959154/Languages/ChineseTraditional (繁體中文)/DefInjected/RecipeDef/Recipe_Recycle.xml", "5a3417e31c711fb35c68f6ef0049ec85391043884d70b87e5b3893009717c09c"))
            { repaired = input.Replace("</DMS_ExtractCompFromSlag.", "</Cinder_ExtractCompFromSlag."); rule = "cinders-recycle-close"; }
            else if (matches("3622884272/Languages/ChineseTraditional (繁體中文)/DefInjected/DamageDef/MilitaryGun.xml", "ba066404bcbdb9757d8f0cc2cc484746e9b7cce559a8ee4ed2b33c6debded977"))
            { repaired = input.Replace("</Beam_CE.", "</DMSTT_Beam."); rule = "tritech-beam-close"; }
            else if (matches("725576127/Languages/ChineseTraditional/DefInjected/ThingDef/Items_AdvancedBodyParts.xml", "d78b8b9e8dc2664864a1c9724394c6908eee8875fe6c35d414f20d3956fe5788") ||
                matches("725576127/Languages/ChineseSimplified/DefInjected/ThingDef/Items_AdvancedBodyParts.xml", "577b30cee312dcd65c9cca2de03b78f356d55b5685af1a82e8c52b256982cf9e"))
            { repaired = input.Replace("/GTEar.description>", "</GTEar.description>"); rule = "glitter-ear-close"; }
            else if (input.Length == 0 && (p.EndsWith("/3473140562/Content/Languages/ChineseSimplified/DefInjected/Drugs/EclipseTrigger.xml", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith("/3473140562/Content/Languages/ChineseTraditional/DefInjected/Drugs/EclipseTrigger.xml", StringComparison.OrdinalIgnoreCase)))
            { repaired = "<LanguageData />"; rule = "empty-language-document"; }
            else if (matches("3569636392/Languages/ChineseTraditional/DefInjected/InteractionDef/InteractionDefs.xml", "50c88d889f2d6ae00f6f3f1d06f7182955044daa598b60f2abfd0f21ecac0fc1") ||
                matches("3569636392/Languages/ChineseSimplified/DefInjected/InteractionDef/InteractionDefs.xml", "50c88d889f2d6ae00f6f3f1d06f7182955044daa598b60f2abfd0f21ecac0fc1"))
            {
                repaired = SingleRuleLists(input, new Dictionary<string, string> { ["RimTalkInteraction.logRulesInitiator.rulesStrings"] = null });
                rule = "rimtalk-scalar-rule-list";
            }
            else if (matches("3473140562/Content/Languages/ChineseTraditional/DefInjected/QuestScriptDef/Script_MegaCropMechRaid.xml", "24e5ba9faa888d88d307b5661c3432525e96e67797644e2d6becfc130e75ff48") ||
                matches("3473140562/Content/Languages/ChineseSimplified/DefInjected/QuestScriptDef/Script_MegaCropMechRaid.xml", "24e5ba9faa888d88d307b5661c3432525e96e67797644e2d6becfc130e75ff48"))
            {
                // Both loaded source Defs explicitly use these empty grammar rules;
                // the translation lost '>' and the enclosing li, not translated prose.
                repaired = SingleRuleLists(input, new Dictionary<string, string> {
                    ["Wolfein_MegaCropMechRaid.questDescriptionRules.rulesStrings"] = "questDescription->",
                    ["Wolfein_MegaCropMechRaid.questNameRules.rulesStrings"] = "questName->"
                });
                rule = "wolfein-raid-rule-lists";
            }
            else if (matches("3473140562/Content/Languages/ChineseTraditional/DefInjected/QuestScriptDef/Script_Util.xml", "fe52659a433e36d3684db4772f058a40bc1a00ada9b6d75749ad2fb4c82f76b0") ||
                matches("3473140562/Content/Languages/ChineseSimplified/DefInjected/QuestScriptDef/Script_Util.xml", "0229e7c29cbfc5d70dd6c1ddebf49ca6a1aad12ef7e4dae6fa2107f58342f0c5"))
            {
                repaired = SingleRuleLists(input, new Dictionary<string, string> {
                    ["Wolfein_Util_MegaCropMechRaidWaves.questDescriptionRules.rulesStrings"] = "threatDescription->"
                });
                rule = "wolfein-wave-rule-list";
            }
            else if (matches("3819799478/1.7/NewContent/Languages/ChineseTraditional (繁體中文)/DefInjected/QuestScriptDef/DMS_Residential.xml", "298adf62ae1cd8d0d1724183f89c9d581b63e6f0d5df08ed7204b248c4c28d82") ||
                matches("3819799478/1.7/NewContent/Languages/ChineseSimplified (简体中文)/DefInjected/QuestScriptDef/DMS_Residential.xml", "f3f74dea551a5e9d903bd5e3856b29d701179cdaaa505edb7f37272e50116efd"))
            {
                repaired = SingleRuleLists(input, new Dictionary<string, string> {
                    ["DMS_ResidentialAssault.questNameRules.rulesStrings"] = null,
                    ["DMS_ResidentialAssault.questDescriptionRules.rulesStrings"] = null
                });
                rule = "dms-residential-rule-lists";
            }
            else if (matches("3477405110/1.6/Languages/English/DefInjected/QuestScriptDef/Script_TheChurchAndMilira_Milira.xml", "f75f0d8390076000540db4477eb443eaae0dccda4e5463dadcf00109c1ed8281") ||
                matches("3477405110/1.6/Languages/ChineseTraditional (繁體中文)/DefInjected/QuestScriptDef/Script_TheChurchAndMilira_Milira.xml", "ba684f3ac2508f3d415a6e69c9960aa71a1de2a0cd0cd4614e863154d2f7e63c"))
            {
                // The source Def has separate questNameRules / questDescriptionRules.
                var doc = new XmlDocument { XmlResolver = null }; doc.LoadXml(input);
                var nodes = doc.SelectNodes("/LanguageData/TheTaleOfMilira_Event_TheChurchAndMilira_Milira.questDescriptionRules.rulesStrings");
                if (nodes.Count != 2 || !nodes[1].InnerText.TrimStart().StartsWith("questName->", StringComparison.Ordinal)) return false;
                var nameRules = doc.CreateElement("TheTaleOfMilira_Event_TheChurchAndMilira_Milira.questNameRules.rulesStrings");
                nameRules.InnerXml = nodes[1].InnerXml;
                nodes[1].ParentNode.ReplaceChild(nameRules, nodes[1]);
                repaired = doc.OuterXml; rule = "milira-quest-name-list";
            }
            if (rule == null) return false;
            // Replacement of a missing '<' must not create a second '<'. A
            // fingerprint already proves this, but strict parse remains mandatory.
            try
            {
                var doc = new XmlDocument { XmlResolver = null };
                using (var reader = XmlReader.Create(new StringReader(repaired), new XmlReaderSettings {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
                if (doc.DocumentElement?.Name == "LanguageData") return true;
            }
            catch (XmlException) { }
            repaired = input; rule = null; return false;
        }

        // Only used behind exact path + content fingerprints above. Every removed
        // duplicate/UNUSED node remains in the archived unmodified source document.
        private static string SingleRuleLists(string input, Dictionary<string, string> rules)
        {
            var document = new XmlDocument { XmlResolver = null };
            document.LoadXml(input);
            foreach (var rule in rules)
            {
                var nodes = document.SelectNodes("/LanguageData/" + rule.Key).Cast<XmlElement>().ToList();
                var first = nodes.First();
                string original = first.InnerText;
                foreach (var duplicate in nodes.Skip(1))
                {
                    if (duplicate.InnerText != original && duplicate.InnerText != "UNUSED")
                        throw new InvalidDataException("Unexpected conflicting rule candidate in fingerprinted source.");
                    duplicate.ParentNode.RemoveChild(duplicate);
                }
                first.RemoveAll();
                var item = document.CreateElement("li");
                item.InnerText = rule.Value ?? original;
                first.AppendChild(item);
            }
            return document.OuterXml;
        }
    }
}
