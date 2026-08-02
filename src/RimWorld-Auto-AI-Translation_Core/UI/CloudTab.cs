using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using static AutoTranslator_Core.DeleteTranslationWindow;
// 這個檔案負責雲端分頁的 UI 與操作入口。
// EN: This file draws the cloud tab and exposes cloud actions.

namespace AutoTranslator_Core
{
    // 這個類別負責 自動翻譯器模組 的主要流程與狀態。
    // EN: This class manages the main workflow and state for AutoTranslatorMod.
    public partial class AutoTranslatorMod : Mod
    {
        // 這個常數定義 雲端RowHeight 的固定值。
        // EN: This constant defines the fixed value for cloud row height.
        private const float CloudRowHeight = 40f;

        // 這個方法負責繪製 雲端分頁 介面。
        // EN: This method draws cloud tab.
        private void DrawCloudTab(Listing_Standard l, Rect viewRect)
        {
            DrawCloudToolbarAndSettings(l, viewRect);

            if (AutoTranslatorSettings.IsFetchingCloud) return;

            Text.Font = GameFont.Small;
            string targetLangFolder = AutoTranslatorScanner.GetFolderNameByLanguage(Settings.CloudTargetLang);
            EnsureCloudStatsCache(targetLangFolder);

            if (AutoTranslatorSettings.CloudConnectionFailed)
            {
                GUI.color = new Color(1f, 0.4f, 0.4f);
                Widgets.Label(l.GetRect(25f), "ATC_Cloud_ConnectionFailed".Translate());
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = new Color(0.4f, 0.8f, 1f);
                Widgets.Label(l.GetRect(25f), "ATC_Cloud_ConnectionNormal".Translate(_cachedCloudCurrentLangCount));
                GUI.color = Color.white;
            }

            Rect searchRect = l.GetRect(30f);
            string oldSearch = AutoTranslatorSettings.CloudSearchText ?? "";
            AutoTranslatorSettings.CloudSearchText = Widgets.TextField(searchRect, oldSearch);
            if (!string.Equals(oldSearch, AutoTranslatorSettings.CloudSearchText ?? "", StringComparison.Ordinal))
            {
                _cachedCloudDisplayMods = null;
            }

            if (string.IsNullOrEmpty(AutoTranslatorSettings.CloudSearchText))
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(searchRect.x + 5f, searchRect.y + 2f, searchRect.width, searchRect.height), "ATC_Cloud_SearchHint".Translate());
                GUI.color = Color.white;
            }

            Rect filterRow = l.GetRect(28f);
            bool showMineOnly = AutoTranslatorSettings.CloudShowMineOnly;
            Widgets.CheckboxLabeled(new Rect(filterRow.x, filterRow.y + 2f, 230f, 24f), "ATC_Cloud_ShowMineOnly".Translate(), ref showMineOnly);
            if (showMineOnly != AutoTranslatorSettings.CloudShowMineOnly)
            {
                AutoTranslatorSettings.CloudShowMineOnly = showMineOnly;
                _cachedCloudDisplayMods = null;
            }

            GUI.color = new Color(0.7f, 0.9f, 1f);
            Widgets.Label(new Rect(filterRow.x + 240f, filterRow.y + 3f, filterRow.width - 240f, 24f), "ATC_Cloud_MyUploadsCount".Translate(_cachedCloudOwnUploadCount));
            GUI.color = Color.white;
            l.Gap(10f);

            Dictionary<string, List<CloudModRecord>> cloudLookup = BuildCloudLookup(targetLangFolder);

            if (AutoTranslatorSettings.CloudShowMineOnly)
            {
                List<CloudModRecord> ownRecords = GetOwnLatestCloudRecordsCached(targetLangFolder);
                if (ownRecords.Count == 0)
                {
                    GUI.color = Color.gray;
                    Widgets.Label(l.GetRect(40f), "ATC_Cloud_NoMyUploads".Translate());
                    GUI.color = Color.white;
                    return;
                }

                float ownListStartY = l.CurHeight;
                int ownFirstVisible = Math.Max(0, Mathf.FloorToInt((AutoTranslatorSettings.mainScrollPos.y - ownListStartY) / CloudRowHeight) - 2);
                int ownLastVisible = Math.Min(ownRecords.Count - 1, Mathf.CeilToInt((AutoTranslatorSettings.mainScrollPos.y - ownListStartY + 900f) / CloudRowHeight) + 2);

                for (int i = 0; i < ownRecords.Count; i++)
                {
                    Rect rowRect = l.GetRect(CloudRowHeight);
                    if (i < ownFirstVisible || i > ownLastVisible)
                    {
                        continue;
                    }

                    DrawOwnCloudRecordRow(ownRecords[i], rowRect, cloudLookup, targetLangFolder);
                }

                return;
            }

            List<ModMetaData> localMods = GetCloudDisplayMods();
            if (localMods.Count == 0)
            {
                GUI.color = Color.gray;
                Widgets.Label(l.GetRect(40f), "ATC_Cloud_NoModsWarning".Translate());
                GUI.color = Color.white;
                return;
            }

            float listStartY = l.CurHeight;
            int firstVisible = Math.Max(0, Mathf.FloorToInt((AutoTranslatorSettings.mainScrollPos.y - listStartY) / CloudRowHeight) - 2);
            int lastVisible = Math.Min(localMods.Count - 1, Mathf.CeilToInt((AutoTranslatorSettings.mainScrollPos.y - listStartY + 900f) / CloudRowHeight) + 2);

            for (int i = 0; i < localMods.Count; i++)
            {
                Rect rowRect = l.GetRect(CloudRowHeight);
                if (i < firstVisible || i > lastVisible)
                {
                    continue;
                }

                DrawCloudModRow(localMods[i], rowRect, cloudLookup, targetLangFolder);
            }
        }

        // 這個方法負責取得 雲端Display模組 資料。
        // EN: This method gets cloud display mods.
        private List<ModMetaData> GetCloudDisplayMods()
        {
            List<ModMetaData> validMods = GetValidModsCached() ?? new List<ModMetaData>();
            string searchText = AutoTranslatorSettings.CloudSearchText ?? "";

            if (_cachedCloudDisplayMods != null &&
                _cachedCloudSearchText == searchText &&
                _cachedCloudValidModCount == validMods.Count &&
                _cachedCloudDisplayValidVersion == ValidModsCacheVersion)
            {
                return _cachedCloudDisplayMods;
            }

            IEnumerable<ModMetaData> mods = validMods.Where(m => m != null && !ShouldSkipCloudSharingMod(m));
            if (!string.IsNullOrEmpty(searchText))
            {
                string searchLower = searchText.ToLowerInvariant();
                mods = mods.Where(m =>
                    ((m.Name ?? "").ToLowerInvariant().Contains(searchLower)) ||
                    ((m.PackageId ?? "").ToLowerInvariant().Contains(searchLower)));
            }

            _cachedCloudDisplayMods = mods
                .OrderByDescending(m => AutoTranslatorScanner.IsOfficialBaseGameOrDlcPackage(m.PackageId))
                .ThenBy(m => m.Name ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
            _cachedCloudSearchText = searchText;
            _cachedCloudValidModCount = validMods.Count;
            _cachedCloudDisplayValidVersion = ValidModsCacheVersion;
            return _cachedCloudDisplayMods;
        }

        private static void EnsureCloudStatsCache(string targetLangFolder)
        {
            int registryCount = AutoTranslatorSettings.CloudRegistry.Count;
            int generation = AutoTranslatorSettings.CloudFetchGeneration;
            if (_cachedCloudStatsRegistryCount == registryCount &&
                _cachedCloudStatsGeneration == generation &&
                string.Equals(_cachedCloudStatsLangFolder, targetLangFolder, StringComparison.Ordinal))
            {
                return;
            }

            int currentLangCount = 0;
            int ownUploadCount = 0;
            foreach (CloudModRecord record in AutoTranslatorSettings.CloudRegistry)
            {
                if (record == null || record.Language != targetLangFolder) continue;
                currentLangCount++;
                if (IsOwnCloudRecord(record)) ownUploadCount++;
            }

            _cachedCloudCurrentLangCount = currentLangCount;
            _cachedCloudOwnUploadCount = ownUploadCount;
            _cachedCloudStatsRegistryCount = registryCount;
            _cachedCloudStatsGeneration = generation;
            _cachedCloudStatsLangFolder = targetLangFolder;
        }

        private static bool IsOwnCloudRecord(CloudModRecord record)
        {
            if (record == null || string.IsNullOrWhiteSpace(record.UploaderID)) return false;
            return string.Equals(record.UploaderID, UnityEngine.SystemInfo.deviceUniqueIdentifier, StringComparison.OrdinalIgnoreCase);
        }

        private List<CloudModRecord> GetOwnLatestCloudRecordsCached(string targetLangFolder)
        {
            string searchText = AutoTranslatorSettings.CloudSearchText ?? "";
            int registryCount = AutoTranslatorSettings.CloudRegistry.Count;
            int generation = AutoTranslatorSettings.CloudFetchGeneration;
            if (_cachedOwnCloudRecords != null &&
                _cachedOwnCloudRecordsRegistryCount == registryCount &&
                _cachedOwnCloudRecordsGeneration == generation &&
                string.Equals(_cachedOwnCloudRecordsLangFolder, targetLangFolder, StringComparison.Ordinal) &&
                string.Equals(_cachedOwnCloudRecordsSearchText, searchText, StringComparison.Ordinal))
            {
                return _cachedOwnCloudRecords;
            }

            IEnumerable<CloudModRecord> records = AutoTranslatorSettings.CloudRegistry
                .Where(r => r != null && r.Language == targetLangFolder && IsOwnCloudRecord(r));

            if (!string.IsNullOrEmpty(searchText))
            {
                string searchLower = searchText.ToLowerInvariant();
                records = records.Where(r =>
                    ((r.ModName ?? "").ToLowerInvariant().Contains(searchLower)) ||
                    ((r.PackageId ?? "").ToLowerInvariant().Contains(searchLower)) ||
                    ((r.Author ?? "").ToLowerInvariant().Contains(searchLower)));
            }

            _cachedOwnCloudRecords = records
                .GroupBy(r => r.PackageId ?? "", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(r => r.LastUpdated).First())
                .OrderByDescending(r => r.LastUpdated)
                .ToList();
            _cachedOwnCloudRecordsRegistryCount = registryCount;
            _cachedOwnCloudRecordsGeneration = generation;
            _cachedOwnCloudRecordsLangFolder = targetLangFolder;
            _cachedOwnCloudRecordsSearchText = searchText;
            return _cachedOwnCloudRecords;
        }
    }
}
