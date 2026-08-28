using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Verse;
using RimWorld;
// 這個檔案負責聯絡原作者的信件產生。
// EN: This file builds contact-author message content.

namespace AutoTranslator_Core
{
    // 這個類別負責 對話框ContactAuthor 的主要流程與狀態。
    // EN: This class manages the main workflow and state for Dialog_ContactAuthor.
    public class Dialog_ContactAuthor : Window
    {
        // 這個欄位保存 exported模組 的執行狀態或快取資料。
        // EN: This field stores exported mods runtime state or cached data.
        private readonly List<ExportableModInfo> _exportedMods;
        // 這個欄位保存 selected模組 的執行狀態或快取資料。
        // EN: This field stores selected mod runtime state or cached data.
        private ExportableModInfo _selectedMod;
        // 這個欄位保存 scrollPos 的執行狀態或快取資料。
        // EN: This field stores scroll pos runtime state or cached data.
        private Vector2 _scrollPos = Vector2.zero;
        // 這個欄位保存 templateScrollPos 的執行狀態或快取資料。
        // EN: This field stores template scroll pos runtime state or cached data.
        private Vector2 _templateScrollPos = Vector2.zero;

        // 這個屬性提供 InitialSize 的讀寫或計算結果。
        // EN: This method handles vector2.
        public override Vector2 InitialSize => new Vector2(750f, 700f);

        // 這個方法負責處理 對話框ContactAuthor 相關流程。
        // EN: This constructor initializes dialog contact author.
        public Dialog_ContactAuthor(List<ExportableModInfo> exportedMods)
        {
            _exportedMods = exportedMods ?? new List<ExportableModInfo>();
            _selectedMod = _exportedMods.FirstOrDefault();

            doCloseButton = false;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        // 這個方法負責處理 Do視窗Contents 相關流程。
        // EN: This method handles do window contents.
        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 35f),
                "ATC_ContactAuthor_Title".Translate());
            Text.Font = GameFont.Small;
            Widgets.DrawLineHorizontal(0, 35f, inRect.width);

            float y = 45f;


            Widgets.Label(new Rect(0, y, inRect.width, 22f),
                "ATC_ContactAuthor_JustExported".Translate(_exportedMods.Count));
            y += 28f;

            GUI.color = new Color(1f, 0.9f, 0.4f);
            Widgets.Label(new Rect(0, y, inRect.width, 22f),
                "ATC_ContactAuthor_DidYouKnow".Translate());
            GUI.color = Color.white;
            y += 25f;

            Widgets.Label(new Rect(0, y, inRect.width, 22f),
                "ATC_ContactAuthor_Intro".Translate());
            y += 25f;

            GUI.color = new Color(0.6f, 1f, 0.6f);
            Widgets.Label(new Rect(20f, y, inRect.width - 20f, 22f),
                "ATC_ContactAuthor_Benefit1".Translate());
            y += 22f;
            Widgets.Label(new Rect(20f, y, inRect.width - 20f, 22f),
                "ATC_ContactAuthor_Benefit2".Translate());
            y += 22f;
            Widgets.Label(new Rect(20f, y, inRect.width - 20f, 22f),
                "ATC_ContactAuthor_Benefit3".Translate());
            y += 22f;
            Widgets.Label(new Rect(20f, y, inRect.width - 20f, 22f),
                "ATC_ContactAuthor_Benefit4".Translate());
            GUI.color = Color.white;
            y += 30f;

            Widgets.DrawLineHorizontal(0, y, inRect.width);
            y += 10f;


            float remainHeight = inRect.height - y - 60f;
            float leftWidth = 250f;
            float rightWidth = inRect.width - leftWidth - 10f;

            Rect leftRect = new Rect(0, y, leftWidth, remainHeight);
            Rect rightRect = new Rect(leftWidth + 10f, y, rightWidth, remainHeight);


            Widgets.Label(new Rect(leftRect.x, leftRect.y, leftRect.width, 22f),
                "ATC_ContactAuthor_SelectMod".Translate());
            Rect listOutRect = new Rect(leftRect.x, leftRect.y + 25f, leftRect.width, leftRect.height - 25f);
            Rect listViewRect = new Rect(0, 0, listOutRect.width - 16f, _exportedMods.Count * 32f);
            Widgets.DrawBoxSolid(listOutRect, new Color(0.1f, 0.1f, 0.1f));

            Widgets.BeginScrollView(listOutRect, ref _scrollPos, listViewRect);
            float itemY = 0f;
            foreach (var mod in _exportedMods)
            {
                Rect itemRect = new Rect(0, itemY, listViewRect.width, 30f);
                bool isSelected = mod == _selectedMod;
                if (isSelected) Widgets.DrawHighlight(itemRect);
                else Widgets.DrawHighlightIfMouseover(itemRect);

                if (Widgets.ButtonInvisible(itemRect))
                {
                    _selectedMod = mod;
                }

                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(itemRect.x + 5f, itemRect.y, itemRect.width - 10f, itemRect.height),
                    mod.ModName);
                Text.Anchor = TextAnchor.UpperLeft;

                itemY += 32f;
            }
            Widgets.EndScrollView();


            if (_selectedMod != null)
            {
                Widgets.Label(new Rect(rightRect.x, rightRect.y, rightRect.width, 22f),
                    "ATC_ContactAuthor_TemplateLabel".Translate());

                string template = BuildEmailTemplate(_selectedMod);
                Rect templateOutRect = new Rect(rightRect.x, rightRect.y + 25f,
                    rightRect.width, rightRect.height - 25f);

                float textHeight = Text.CalcHeight(template, templateOutRect.width - 20f);
                Rect templateViewRect = new Rect(0, 0, templateOutRect.width - 20f,
                    Mathf.Max(textHeight + 20f, templateOutRect.height));

                Widgets.DrawBoxSolid(templateOutRect, new Color(0.08f, 0.08f, 0.08f));
                Widgets.BeginScrollView(templateOutRect, ref _templateScrollPos, templateViewRect);
                GUI.color = new Color(0.9f, 0.9f, 0.9f);
                Widgets.Label(new Rect(8f, 5f, templateViewRect.width - 16f, textHeight), template);
                GUI.color = Color.white;
                Widgets.EndScrollView();
            }


            float btnY = inRect.height - 45f;
            float btnWidth = (inRect.width - 30f) / 3f;
            Rect copyBtnRect = new Rect(0, btnY, btnWidth, 40f);
            Rect workshopBtnRect = new Rect(btnWidth + 10f, btnY, btnWidth, 40f);
            Rect closeBtnRect = new Rect((btnWidth + 10f) * 2, btnY, btnWidth, 40f);


            if (_selectedMod != null)
                GUI.color = new Color(0.4f, 1f, 0.8f);
            else
                GUI.color = new Color(0.5f, 0.5f, 0.5f);

            if (Widgets.ButtonText(copyBtnRect, "ATC_ContactAuthor_CopyTemplate".Translate()))
            {
                if (_selectedMod != null)
                {
                    string template = BuildEmailTemplate(_selectedMod);
                    GUIUtility.systemCopyBuffer = template;
                    Messages.Message("ATC_ContactAuthor_TemplateCopied".Translate(),
                        MessageTypeDefOf.PositiveEvent, false);
                }
            }


            GUI.color = new Color(0.4f, 0.8f, 1f);
            if (Widgets.ButtonText(workshopBtnRect, "ATC_ContactAuthor_OpenWorkshop".Translate()))
            {
                TryOpenWorkshopPage(_selectedMod);
            }


            GUI.color = Color.white;
            if (Widgets.ButtonText(closeBtnRect, "ATC_ContactAuthor_Close".Translate()))
            {
                Close();
            }
            GUI.color = Color.white;
        }


        // 這個方法負責建立 Email範本 所需資料。
        // EN: This method builds email template.
        private string BuildEmailTemplate(ExportableModInfo mod)
        {
            string targetLang = GetTargetLanguageName();
            string authorPlaceholder = TryGetModAuthor(mod) ?? "[Author Name]";

            string subject = "ATC_EmailTemplate_Subject".Translate(mod.ModName);
            string body = "ATC_EmailTemplate_Body".Translate(
                authorPlaceholder,
                mod.ModName,
                targetLang,
                mod.DefInjectedCount,
                mod.KeyedCount
            );

            return $"Subject: {subject}\n\n{body}";
        }

        // 這個方法負責取得 目標語言名稱 資料。
        // EN: This method gets target language name.
        private string GetTargetLanguageName()
        {
            switch (AutoTranslatorMod.Settings.TargetLang)
            {
                case TargetLanguage.Traditional: return "Traditional Chinese (繁體中文)";
                case TargetLanguage.Simplified: return "Simplified Chinese (简体中文)";
                case TargetLanguage.Japanese: return "Japanese (日本語)";
                case TargetLanguage.Korean: return "Korean (한국어)";
                case TargetLanguage.Russian: return "Russian (Русский)";
                case TargetLanguage.Ukrainian: return "Ukrainian (Українська)";
                case TargetLanguage.English: return "English";
                case TargetLanguage.Thai: return "Thai (ภาษาไทย)";
                default: return "English";
            }
        }

        // 這個方法負責嘗試執行 Get模組Author 並回報是否成功。
        // EN: This method tries to get mod author and reports whether it succeeded.
        private string TryGetModAuthor(ExportableModInfo info)
        {
            if (string.IsNullOrEmpty(info.PackageId)) return null;
            var meta = ModLister.AllInstalledMods.FirstOrDefault(m =>
                m.PackageId.Equals(info.PackageId, StringComparison.OrdinalIgnoreCase));
            if (meta == null) return null;


            try
            {
                var authorsStringProp = typeof(ModMetaData).GetProperty("AuthorsString");
                if (authorsStringProp != null)
                {
                    string s = authorsStringProp.GetValue(meta) as string;
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }


                var authorsProp = typeof(ModMetaData).GetProperty("Authors");
                if (authorsProp != null)
                {
                    var val = authorsProp.GetValue(meta);
                    if (val is System.Collections.IEnumerable enumerable)
                    {
                        var list = new List<string>();
                        foreach (var item in enumerable)
                        {
                            if (item != null) list.Add(item.ToString());
                        }
                        if (list.Count > 0) return string.Join(", ", list);
                    }
                }


                var authorProp = typeof(ModMetaData).GetProperty("Author");
                if (authorProp != null)
                {
                    string s = authorProp.GetValue(meta) as string;
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[AutoTranslationCore] Failed to read mod author: {ex.Message}");
            }

            return null;
        }


        // 這個方法負責嘗試執行 OpenWorkshopPage 並回報是否成功。
        // EN: This method tries to open workshop page and reports whether it succeeded.
        private void TryOpenWorkshopPage(ExportableModInfo info)
        {
            if (info == null) return;

            var meta = ModLister.AllInstalledMods.FirstOrDefault(m =>
                m.PackageId.Equals(info.PackageId, StringComparison.OrdinalIgnoreCase));

            if (meta == null || meta.RootDir == null)
            {
                Messages.Message("ATC_ContactAuthor_CannotOpenWorkshop".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }


            string idFile = Path.Combine(meta.RootDir.FullName, "About", "PublishedFileId.txt");
            if (!File.Exists(idFile))
            {
                idFile = Path.Combine(meta.RootDir.FullName, "PublishedFileId.txt");
            }

            if (File.Exists(idFile))
            {
                try
                {
                    string workshopId = File.ReadAllText(idFile).Trim();
                    if (!string.IsNullOrEmpty(workshopId) && workshopId.All(char.IsDigit))
                    {

                        string steamUrl = $"steam://url/CommunityFilePage/{workshopId}";
                        string webUrl = $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";

                        try
                        {
                            Application.OpenURL(steamUrl);
                        }
                        catch
                        {
                            Application.OpenURL(webUrl);
                        }
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[AutoTranslationCore] Read PublishedFileId failed: {ex.Message}");
                }
            }

            Messages.Message("ATC_ContactAuthor_CannotOpenWorkshop".Translate(),
                MessageTypeDefOf.RejectInput, false);
        }
    }
}
