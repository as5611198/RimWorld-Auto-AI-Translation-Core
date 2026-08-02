using Newtonsoft.Json;
using RimWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責掃描器的共用黑名單、路徑判斷與基礎工具。
// EN: This file stores shared scanner filters, path checks, and helper data.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器掃描器 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorScanner.
    public static partial class AutoTranslatorScanner
    {

        // 這個欄位保存 BlacklistedModules 的執行狀態或快取資料。
        // EN: This field stores blacklisted modules runtime state or cached data.
        private static readonly HashSet<string> BlacklistedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.ideology",
            "ludeon.rimworld.biotech", "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey",
            "auto.aitranslation.core", "aitranslation.pack"
        };


        private static readonly object _pendingInjectLock = new object();
        // 這個欄位保存 pending記憶體Drop 的執行狀態或快取資料。
        // EN: This field stores pending memory drop runtime state or cached data.
        private static bool _pendingMemoryDrop = false;
        private static bool _pendingKeyedMemoryDrop = false;
        private static readonly HashSet<string> _pendingMemoryDropPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, HashSet<string>>> _pendingMemoryDropClearKeysByPackage =
            new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);
        private static int _startupFullMemoryDropQueued = 0;
        private static int _memoryDropPrepareRunning = 0;
        private static int _memoryDropGeneration = 0;
        private static MemoryDropPayload _pendingMemoryDropPayload = null;
        private static MemoryDropApplyState _activeMemoryDropApply = null;
        private static bool _pendingStaticCachedTranslationRefresh = false;
        private static readonly object _memoryDropStampLock = new object();
        private static string _lastKeyedMemoryDropStamp = null;
        private static string _lastFullMemoryDropStamp = null;
        private static Dictionary<string, string> GlobalPrimaryDefDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> GlobalSecondaryDefDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> GlobalPrimaryKeyedDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string> GlobalSecondaryKeyedDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, TranslationProvenanceEntry> GlobalPrimaryDefSourceDict = new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, TranslationProvenanceEntry> GlobalSecondaryDefSourceDict = new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, TranslationProvenanceEntry> GlobalPrimaryKeyedSourceDict = new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, TranslationProvenanceEntry> GlobalSecondaryKeyedSourceDict = new Dictionary<string, TranslationProvenanceEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object GlobalTranslationDatabaseCacheLock = new object();
        private static int GlobalTranslationDatabaseCacheGeneration = 0;
        private static string GlobalTranslationDatabaseCacheKey = null;
        private static Dictionary<string, string> GlobalTranslationDatabaseCachedPrimaryDefDict = null;
        private static Dictionary<string, string> GlobalTranslationDatabaseCachedSecondaryDefDict = null;
        private static Dictionary<string, string> GlobalTranslationDatabaseCachedPrimaryKeyedDict = null;
        private static Dictionary<string, string> GlobalTranslationDatabaseCachedSecondaryKeyedDict = null;
        private static Dictionary<string, TranslationProvenanceEntry> GlobalTranslationDatabaseCachedPrimaryDefSourceDict = null;
        private static Dictionary<string, TranslationProvenanceEntry> GlobalTranslationDatabaseCachedSecondaryDefSourceDict = null;
        private static Dictionary<string, TranslationProvenanceEntry> GlobalTranslationDatabaseCachedPrimaryKeyedSourceDict = null;
        private static Dictionary<string, TranslationProvenanceEntry> GlobalTranslationDatabaseCachedSecondaryKeyedSourceDict = null;
        private static readonly TranslationValidationStats _validationStats = new TranslationValidationStats();
        private static readonly HashSet<string> _loggedEnglishResidualContexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 這個類別負責 翻譯ValidationStats 的主要流程與狀態。
        // EN: This class manages the main workflow and state for TranslationValidationStats.
        private class TranslationValidationStats
        {
            // 這個欄位保存 NewlineFixed 的執行狀態或快取資料。
            // EN: This field stores newline fixed runtime state or cached data.
            public int NewlineFixed;
            // 這個欄位保存 規則PrefixFixed 的執行狀態或快取資料。
            // EN: This field stores rule prefix fixed runtime state or cached data.
            public int RulePrefixFixed;
            // 這個欄位保存 TokenFixed 的執行狀態或快取資料。
            // EN: This field stores token fixed runtime state or cached data.
            public int TokenFixed;
            // 這個欄位保存 StructureFallback 的執行狀態或快取資料。
            // EN: This field stores structure fallback runtime state or cached data.
            public int StructureFallback;
            // 這個欄位保存 XmlKeySkipped 的執行狀態或快取資料。
            // EN: This field stores XML key skipped runtime state or cached data.
            public int XmlKeySkipped;
            // 這個欄位保存 EnglishResidualDetected 的執行狀態或快取資料。
            // EN: This field stores english residual detected runtime state or cached data.
            public int EnglishResidualDetected;
            // 這個欄位保存 EnglishResidualRetried 的執行狀態或快取資料。
            // EN: This field stores english residual retried runtime state or cached data.
            public int EnglishResidualRetried;
            // 這個欄位保存 EnglishResidualFallback 的執行狀態或快取資料。
            // EN: This field stores english residual fallback runtime state or cached data.
            public int EnglishResidualFallback;
            public int ProtectedTokenMismatchDetected;
            public int ProtectedTokenMismatchRetried;
            public int ProtectedTokenMismatchFallback;

            // 這個方法負責重置 這段邏輯 狀態。
            // EN: This method resets .
            public void Reset()
            {
                NewlineFixed = 0;
                RulePrefixFixed = 0;
                TokenFixed = 0;
                StructureFallback = 0;
                XmlKeySkipped = 0;
                EnglishResidualDetected = 0;
                EnglishResidualRetried = 0;
                EnglishResidualFallback = 0;
                ProtectedTokenMismatchDetected = 0;
                ProtectedTokenMismatchRetried = 0;
                ProtectedTokenMismatchFallback = 0;
            }
        }

        // 這個欄位保存 ExactTextTags 的執行狀態或快取資料。
        // EN: This field stores exact text tags runtime state or cached data.
        private static readonly HashSet<string> ExactTextTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "label", "description", "jobString", "reportString", "text", "labelShort", "customLabel", "descriptionShort",
            "pawnLabel", "gerund", "verb", "deathMessage", "inspectString", "baseInspectString", "helpText",
            "letterLabel", "letterText", "message", "messageSuccess", "messageFailed", "rejectInputMessage",
            "skillLabel", "endMessage", "beginLetterLabel", "beginLetter", "recoveryMessage", "destroyedLabel",
            "pawnSingular", "pawnPlural", "pawnsPlural", "leaderTitle", "adjective", "royalFavorLabel", "arrivalText", "arrivalTextEnemy",
            "logRulesInitiator", "logRulesRecipient", "useLabel", "ingestCommandString", "ingestReportString",
            "meatLabel", "corpseLabel", "discoverLetterTitle", "discoverLetterText", "letterLabelEnemy", "letterTextEnemy",
            "commandLabel", "commandDescription", "formatString", "outfitName", "labelNoun", "labelNounPretty",
            "customSummary", "summary", "title", "titleShort", "titleFemale", "titleShortFemale", "titleMale",
            "titleShortMale", "subtitle", "theme", "member", "ideoName", "successMessage",
            "successMessageNoNegativeThought", "failureMessage", "failMessage", "warningMessage",
            "tooltip", "explanation", "caption", "labelShortAdj", "baseInspectLine", "inspectLine",
            "fuelLabel", "fuelGizmoLabel", "permanentLabel", "destroyedOutLabel",
            "customLetterLabel", "customLetterText",
            "extraTooltip", "disabledReason", "confirmationDialogText", "invalidTargetMessage",
            "cannotUseMessage", "targetingLabel", "targetLabel", "gizmoLabel", "gizmoDescription",
            "settingsLabel", "settingsDescription", "settingsTooltip",
            "rulesStrings"
        };

        public static bool IsWorkbenchManualExportPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            string normalized = filePath.Replace('\\', '/');
            return normalized.IndexOf("/Upload_Workspace/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   normalized.IndexOf("/Manual_Translation/", StringComparison.OrdinalIgnoreCase) >= 0;
        }


        // 這個欄位保存 BlacklistedFields 的執行狀態或快取資料。
        // EN: This field stores blacklisted fields runtime state or cached data.
        private static readonly HashSet<string> BlacklistedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{

    "alienRace", "texPath", "graphicPath", "soundDef", "effecter",
    "iconPath", "shader", "soundCast", "soundCastTail", "soundInteract",
    "soundHitPawn", "soundMiss", "soundMeleeHit", "soundMeleeMiss",
    "soundAmbience", "linkSound", "fleckDef",


    "thingDef", "itemDef", "pawnKindDef", "hediffDef", "recipeDef",
    "researchProjectDef", "terrainDef", "traitDef", "skillDef",
    "damageDef", "weaponDef", "apparelDef", "projectileDef",


    "defName", "dollName", "dollPartName", "methodName", "class", "worker",


    "eyeTexPath", "browTexPath", "lidTexPath", "lashTexPath",
    "mouthTexPath", "noseTexPath", "earTexPath", "hairTexPath",
    "headTexPath", "bodyTexPath", "skinTexPath",
    "eyeballTexPath", "irisTexPath", "pupilTexPath",
    "expressionPath", "animationPath", "facialDef",

    "texture", "texturePath", "path", "graphicPath", "maskPath",
    "headGraphicPath", "bodyGraphicPath", "crownGraphicPath",
    "frontTexPath", "sideTexPath", "backTexPath",


    "bodyGraphicData", "headGraphicData", "graphicData",
    "bodyAddon", "bodyAddons", "headAddons", "bodyPart",
    "skinColorChannel", "hairColorChannel", "channelName",
    "linkedBodyPartsGroup", "renderNodeProperties",
    "maskPath", "shaderType", "subPath",
    "targetJobs", "animationFrames", "faceAnimationDef",
    "browOffset", "lidOffset", "headOffset", "mouthOffset",
    "noseOffset", "earOffset", "eyeballOffset", "eyeballOffsetL", "eyeballOffsetR",
    "layerOffset", "angle", "scale", "drawSize", "offset", "offsets",


    "li_ref", "parent", "parentName", "abstract", "inherit",
    "compClass", "thingClass", "race", "category", "categories",
    "tradeTags", "weaponTags", "apparelTags", "tags",
    "linkFlags", "renderNodeTagDef", "tagDef"
};
        private static readonly Regex FilePathRegex = new Regex(@"\.(png|jpg|jpeg|wav|mp3|ogg|xml|txt|lua|tex|dds)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ProtectedTokenRegex = new Regex(@"(\{[^{}\r\n]+\}|\[[^\[\]\r\n]+\])", RegexOptions.Compiled);
        private static readonly Regex FormatArgumentRegex = new Regex(@"\{(\d+)(?:,[^{}:]+)?(?::[^{}]+)?\}", RegexOptions.Compiled);

        private static readonly Regex ValidXmlNameRegex = new Regex(
            @"^[A-Za-z_][A-Za-z0-9_\-\.]*$",
            RegexOptions.Compiled
        );

    }

}
