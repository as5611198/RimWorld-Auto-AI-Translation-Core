using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個資料模型保存雲端模組的版本與發佈資訊。
// EN: This model stores cloud mod version and publishing metadata.

namespace AutoTranslator_Core
{
    // 這個類別負責 雲端模組Record 的主要流程與狀態。
    // EN: This class manages the main workflow and state for CloudModRecord.
    public class CloudModRecord
    {
        // 這個屬性提供 RecordId 的讀寫或計算結果。
        // EN: This property exposes record id.
        public string RecordId { get; set; }
        // 這個屬性提供 PackageId 的讀寫或計算結果。
        // EN: This property exposes package id.
        public string PackageId { get; set; }
        // 這個屬性提供 語言 的讀寫或計算結果。
        // EN: This property exposes language.
        public string Language { get; set; }
        // 這個屬性提供 模組名稱 的讀寫或計算結果。
        // EN: This property exposes mod name.
        public string ModName { get; set; }
        // 這個屬性提供 Latest版本 的讀寫或計算結果。
        // EN: This property exposes latest version.
        public string LatestVersion { get; set; }
        // 這個屬性提供 LastUpdated 的讀寫或計算結果。
        // EN: This property exposes last updated.
        public DateTime LastUpdated { get; set; }
        // 這個屬性提供 模組LastUpdated 的讀寫或計算結果。
        // EN: This property exposes mod last updated.
        public DateTime ModLastUpdated { get; set; }
        // 這個屬性提供 UploaderID 的讀寫或計算結果。
        // EN: This property exposes uploader ID.
        public string UploaderID { get; set; }
        // 這個屬性提供 Author 的讀寫或計算結果。
        // EN: This property exposes author.
        public string Author { get; set; }
        // 這個屬性提供 翻譯Type 的讀寫或計算結果。
        // EN: This property exposes translation type.
        public string TranslationType { get; set; }
        // 這個屬性提供 翻譯SourceKind 的讀寫或計算結果。
        // EN: This property exposes translation source kind.
        public string TranslationSourceKind { get; set; }
        // 這個屬性提供 SourceKind 的讀寫或計算結果，用於相容雲端欄位別名。
        // EN: This property exposes source kind alias for cloud compatibility.
        public string SourceKind { get; set; }
        // 這個屬性提供 翻譯SourceSchemaVersion 的讀寫或計算結果。
        // EN: This property exposes translation source schema version.
        public int TranslationSourceSchemaVersion { get; set; }
        // 這個屬性提供 IsVerified 的讀寫或計算結果。
        // EN: This property exposes is verified.
        public bool IsVerified { get; set; }
        // 這個屬性提供 File網址 的讀寫或計算結果。
        // EN: This property exposes file URL.
        public string FileUrl { get; set; }
        // 這個屬性提供 目標模組版本 的讀寫或計算結果。
        // EN: This property exposes target mod version.
        public string TargetModVersion { get; set; }
        // 這個屬性提供 翻譯Date 的讀寫或計算結果。
        // EN: This property exposes translation date.
        public DateTime TranslationDate { get; set; }
        // 這個屬性提供 IsSmartMerged 的讀寫或計算結果。
        // EN: This property exposes is smart merged.
        public bool IsSmartMerged { get; set; }
        // 這個屬性提供 MergedAiCount 的讀寫或計算結果。
        // EN: This property exposes merged ai count.
        public int MergedAiCount { get; set; }
        // 這個屬性提供 UpdateLog 的讀寫或計算結果。
        // EN: This property exposes update log.
        public string UpdateLog { get; set; }
    }
}
