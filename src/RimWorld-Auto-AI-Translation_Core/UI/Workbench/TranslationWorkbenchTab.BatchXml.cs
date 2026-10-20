using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public static partial class TranslationWorkbenchTab
    {
        private static string _lastBatchXmlFolder;

        private static ManualXmlBatchStore.ModData CaptureBatchXmlData(WorkbenchModSnapshot mod,
            Dictionary<string, List<WorkbenchItem>> data)
        {
            return new ManualXmlBatchStore.ModData { PackageId = mod.PackageId, Name = mod.ModName,
                Categories = data.ToDictionary(c => c.Key, c => c.Value.Where(i => i != null).Select(i =>
                    new ManualXmlBatchStore.Entry { Key = i.Key, OriginalText = i.OriginalText, TranslatedText = i.TranslatedText }).ToList(),
                    StringComparer.OrdinalIgnoreCase) };
        }

        private sealed class Window_WorkbenchBatchXml : Window
        {
            private readonly List<WorkbenchModSnapshot> mods;
            private readonly TargetLanguage language;
            private readonly string languageFolder;
            private readonly HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private Dictionary<string, ManualXmlBatchStore.Plan> plans;
            private string search = "", folder, status = "";
            private string cachedSearch;
            private List<WorkbenchModSnapshot> visibleMods;
            private Vector2 scroll;
            private bool busy;
            private volatile int progress, total;
            private volatile string currentMod = "";
            public override Vector2 InitialSize { get { return new Vector2(Mathf.Min(1000f, UI.screenWidth - 40f), Mathf.Min(740f, UI.screenHeight - 40f)); } }

            internal Window_WorkbenchBatchXml()
            {
                language = AutoTranslatorMod.Settings.TargetLang;
                languageFolder = AutoTranslatorScanner.GetFolderNameByLanguage(language);
                mods = AutoTranslatorMod.GetValidModsCached().Where(m => m != null)
                    .GroupBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase).Select(g => CreateWorkbenchModSnapshot(g.First()))
                    .Where(m => m != null).OrderBy(m => m.ModName, StringComparer.OrdinalIgnoreCase).ToList();
                folder = _lastBatchXmlFolder ?? "";
                absorbInputAroundWindow = true;
                closeOnCancel = false;
                closeOnClickedOutside = false;
                doCloseX = false;
                draggable = true;
            }

            public override void DoWindowContents(Rect rect)
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0, 0, rect.width - 155f, 36f), "ATC_BatchXml_Title".Translate());
                Text.Font = GameFont.Small;
                if (Widgets.ButtonText(new Rect(rect.width - 145f, 0, 145f, 30f), "ATC_BatchXml_HelpButton".Translate()))
                    Find.WindowStack.Add(new TutorialWindow(true));
                Widgets.Label(new Rect(0, 40f, rect.width, 48f), "ATC_BatchXml_Help".Translate(languageFolder));
                bool oldEnabled = GUI.enabled;
                GUI.enabled = oldEnabled && !busy;
                string newFolder = Widgets.TextField(new Rect(0, 94f, rect.width - 310f, 30f), folder);
                if (newFolder != folder) { folder = newFolder; plans = null; }
                if (Widgets.ButtonText(new Rect(rect.width - 300f, 94f, 145f, 30f), "ATC_BatchXml_ChooseBatch".Translate())) ChooseBatch();
                if (Widgets.ButtonText(new Rect(rect.width - 145f, 94f, 145f, 30f), "ATC_BatchXml_OpenFolder".Translate())) OpenFolder();
                search = Widgets.TextField(new Rect(0, 134f, rect.width - 350f, 30f), search);
                if (visibleMods == null || search != cachedSearch)
                {
                    cachedSearch = search;
                    visibleMods = mods.Where(m => string.IsNullOrEmpty(search) ||
                        m.ModName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        m.PackageId.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    scroll = Vector2.zero;
                }
                var visible = visibleMods;
                if (Widgets.ButtonText(new Rect(rect.width - 340f, 134f, 170f, 30f), "ATC_BatchXml_SelectVisible".Translate()))
                { foreach (var mod in visible) selected.Add(mod.PackageId); plans = null; }
                if (Widgets.ButtonText(new Rect(rect.width - 160f, 134f, 160f, 30f), "ATC_BatchXml_Clear".Translate()))
                { selected.Clear(); plans = null; }
                Widgets.Label(new Rect(0, 172f, rect.width, 24f), "ATC_BatchXml_Selected".Translate(selected.Count, mods.Count));
                Rect viewport = new Rect(0, 202f, rect.width, rect.height - 334f);
                Rect content = new Rect(0, 0, rect.width - 20f, Math.Max(viewport.height, visible.Count * 66f));
                Widgets.BeginScrollView(viewport, ref scroll, content);
                int first = Math.Max(0, (int)(scroll.y / 66f) - 1);
                int last = Math.Min(visible.Count, (int)((scroll.y + viewport.height) / 66f) + 2);
                for (int i = first; i < last; i++)
                {
                    WorkbenchModSnapshot mod = visible[i];
                    bool check = selected.Contains(mod.PackageId), before = check;
                    Rect row = new Rect(0, i * 66f, content.width, 62f);
                    Widgets.DrawHighlightIfMouseover(row);
                    Widgets.CheckboxLabeled(new Rect(5, row.y, row.width - 10f, 28f), mod.ModName, ref check);
                    if (before != check) { if (check) selected.Add(mod.PackageId); else selected.Remove(mod.PackageId); plans = null; }
                    ManualXmlBatchStore.Plan plan;
                    string detail = mod.PackageId;
                    if (plans != null && plans.TryGetValue(mod.PackageId, out plan))
                        detail = !string.IsNullOrEmpty(plan.Error) ? "ATC_BatchXml_ModError".Translate(plan.Error).ToString()
                            : "ATC_BatchXml_ModSummary".Translate(plan.Changes.Count, plan.Unchanged, plan.Skipped, plan.MissingFiles).ToString();
                    Text.Font = GameFont.Tiny;
                    Text.WordWrap = false;
                    Widgets.Label(new Rect(5, row.y + 30f, row.width - 10f, 24f), detail);
                    TooltipHandler.TipRegion(row, mod.PackageId + "\n" + detail);
                    Text.Font = GameFont.Small;
                    Text.WordWrap = true;
                }
                Widgets.EndScrollView();
                GUI.enabled = oldEnabled;
                Widgets.Label(new Rect(0, rect.height - 120f, rect.width, 70f), busy
                    ? "ATC_BatchXml_Working".Translate() + "\n" + progress + " / " + total + "  " + currentMod : status);
                bool canWork = !busy && !_isManualXmlBusy && !_isSavingModifications && !AutoTranslatorSettings.IsRunning;
                float width = (rect.width - 30f) / 4f;
                GUI.enabled = oldEnabled && canWork && selected.Count > 0;
                if (Widgets.ButtonText(new Rect(0, rect.height - 40f, width, 36f), "ATC_BatchXml_Export".Translate())) Run(false);
                GUI.enabled = oldEnabled && canWork && selected.Count > 0 && !string.IsNullOrWhiteSpace(folder);
                if (Widgets.ButtonText(new Rect(width + 10f, rect.height - 40f, width, 36f), "ATC_BatchXml_Preview".Translate())) Run(true);
                GUI.enabled = oldEnabled && canWork && plans != null && plans.Values.Any(p => p.Changes.Count > 0);
                if (Widgets.ButtonText(new Rect((width + 10f) * 2f, rect.height - 40f, width, 36f), "ATC_BatchXml_Apply".Translate())) Apply();
                GUI.enabled = oldEnabled && !busy;
                if (Widgets.ButtonText(new Rect((width + 10f) * 3f, rect.height - 40f, width, 36f), "CloseButton".Translate())) Close();
                GUI.enabled = oldEnabled;
            }

            private string BatchRoot { get { return Path.Combine(AutoTranslatorScanner.GetLocalPackPath(), "Manual_XML_Batches"); } }

            private void ChooseBatch()
            {
                try
                {
                    var paths = Directory.Exists(BatchRoot) ? Directory.GetDirectories(BatchRoot)
                        .Where(p => File.Exists(Path.Combine(p, "manifest.json"))).OrderByDescending(p => p).ToList() : new List<string>();
                    if (paths.Count == 0) { status = "ATC_BatchXml_NoBatches".Translate().ToString(); return; }
                    Find.WindowStack.Add(new FloatMenu(paths.Select(p => new FloatMenuOption(Path.GetFileName(p), () =>
                    { folder = p; plans = null; })).ToList()));
                }
                catch (Exception ex) { status = ex.Message; }
            }

            private void OpenFolder()
            {
                try
                {
                    string path = string.IsNullOrWhiteSpace(folder) ? BatchRoot : Path.GetFullPath(folder);
                    Directory.CreateDirectory(path);
                    Application.OpenURL(new Uri(path + Path.DirectorySeparatorChar).AbsoluteUri);
                }
                catch (Exception ex) { status = ex.Message; }
            }

            private void Run(bool import)
            {
                var chosen = mods.Where(m => selected.Contains(m.PackageId)).ToList();
                string path = import ? folder : Path.Combine(BatchRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                busy = true; _isManualXmlBusy = true; plans = null; progress = 0; total = chosen.Count; currentMod = "";
                Task.Run(() =>
                {
                    var preview = new Dictionary<string, ManualXmlBatchStore.Plan>(StringComparer.OrdinalIgnoreCase);
                    string error = null;
                    int exported = 0, exportedMods = 0;
                    try
                    {
                        ManualXmlBatchStore.Manifest manifest = import ? ManualXmlBatchStore.ReadManifest(path, languageFolder) : null;
                        var exportData = new List<ManualXmlBatchStore.ModData>();
                        foreach (var mod in chosen)
                        {
                            currentMod = mod.ModName;
                            try
                            {
                                var data = ReadWorkbenchData(mod, out _);
                                var captured = CaptureBatchXmlData(mod, data);
                                if (import) preview[mod.PackageId] = ManualXmlBatchStore.Preview(path, languageFolder, manifest, captured,
                                    AutoTranslatorScanner.IsManualXmlTranslationFormatValid);
                                else if (captured.Categories.Count > 0) exportData.Add(captured);
                            }
                            catch (Exception ex)
                            {
                                preview[mod.PackageId] = new ManualXmlBatchStore.Plan { PackageId = mod.PackageId, Name = mod.ModName, Error = ex.Message };
                            }
                            progress++;
                        }
                        if (!import) { exported = ManualXmlBatchStore.Export(path, languageFolder, exportData); exportedMods = exportData.Count; }
                    }
                    catch (Exception ex) { error = ex.Message; }
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        busy = false; _isManualXmlBusy = false;
                        if (error != null) { status = error; return; }
                        folder = path; _lastBatchXmlFolder = path;
                        plans = preview;
                        status = import ? "ATC_BatchXml_PreviewSummary".Translate(preview.Values.Sum(p => p.Changes.Count),
                            preview.Values.Count(p => !string.IsNullOrEmpty(p.Error))).ToString()
                            : "ATC_BatchXml_Exported".Translate(exportedMods, exported, preview.Count).ToString();
                        if (!import) OpenFolder();
                    });
                });
            }

            private void Apply()
            {
                if (AutoTranslatorMod.Settings.TargetLang != language)
                { status = "ATC_BatchXml_LanguageChanged".Translate().ToString(); plans = null; return; }
                var capturedPlans = plans.Values.Where(p => p.Changes.Count > 0).ToList();
                busy = true; _isManualXmlBusy = true; progress = 0; total = capturedPlans.Count; currentMod = "";
                Task.Run(() =>
                {
                    var saved = new List<Tuple<WorkbenchSaveSnapshot, WorkbenchSaveResult>>();
                    int conflicts = 0, failed = 0;
                    var errors = new List<string>();
                    foreach (var plan in capturedPlans)
                    {
                        var mod = mods.First(m => m.PackageId == plan.PackageId);
                        currentMod = mod.ModName;
                        try
                        {
                            var data = ReadWorkbenchData(mod, out _);
                            foreach (var change in plan.Changes)
                            {
                                List<WorkbenchItem> items;
                                WorkbenchItem item = data.TryGetValue(change.Category, out items)
                                    ? items.FirstOrDefault(i => string.Equals(i.Key, change.Key, StringComparison.OrdinalIgnoreCase)) : null;
                                var current = item == null ? null : new ManualXmlBatchStore.Entry { OriginalText = item.OriginalText, TranslatedText = item.TranslatedText };
                                if (!ManualXmlBatchStore.StillMatches(change, current)) { conflicts++; continue; }
                                item.TranslatedText = change.TranslatedText;
                                RefreshWorkbenchItemModifiedState(item);
                            }
                            var snapshot = CreateSaveSnapshot(mod, data);
                            if (snapshot.Categories.Count == 0) continue;
                            var result = SaveSnapshot(snapshot);
                            if (result.Error != null) { failed++; errors.Add(mod.ModName + ": " + result.Error); }
                            else saved.Add(Tuple.Create(snapshot, result));
                        }
                        catch (Exception ex) { failed++; errors.Add(mod.ModName + ": " + ex.Message); }
                        finally { progress++; }
                    }
                    ATC_Dispatcher.RunOnMainThread(() =>
                    {
                        busy = false; _isManualXmlBusy = false; plans = null;
                        foreach (var pair in saved) FinalizeWorkbenchSave(pair.Item1, pair.Item2);
                        status = "ATC_BatchXml_Applied".Translate(saved.Count, saved.Sum(p => p.Item2.SavedCount), conflicts, failed).ToString();
                        if (errors.Count > 0) status += "\n" + string.Join("\n", errors.Take(2));
                        Messages.Message(status, failed > 0 ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.PositiveEvent, false);
                    });
                });
            }
        }
    }
}
