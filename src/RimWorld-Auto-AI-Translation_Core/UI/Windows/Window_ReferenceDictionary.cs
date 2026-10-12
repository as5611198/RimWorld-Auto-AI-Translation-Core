using System;
using System.Collections.Generic;
using System.Linq;
using AutoTranslator_Core.Workflow;
using Newtonsoft.Json;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public sealed class Window_ReferenceDictionary : Window
    {
        private readonly WorkflowBackend _backend;
        private readonly string _language;
        private List<ReferenceDictionaryEntry> _entries = new List<ReferenceDictionaryEntry>();
        private List<WorkflowModMetadataSnapshot> _mods = new List<WorkflowModMetadataSnapshot>();
        private ReferenceDictionaryEntry _editing;
        private Vector2 _scroll;
        private string _search = string.Empty;
        private string _message = string.Empty;

        public Window_ReferenceDictionary()
        {
            doCloseX = true; absorbInputAroundWindow = true; closeOnClickedOutside = false;
            closeOnAccept = false;
            _backend = WorkflowBackendRuntime.GetOrCreate();
            _language = WorkflowRuntimeSettings.GetTargetLanguageFolder();
            NewEntry();
            Reload();
        }

        public override Vector2 InitialSize => new Vector2(1000f, 680f);
        private static string T(string chinese, string english) => AutoTranslatorMod.WfText(chinese, english);

        private void NewEntry() { _editing = new ReferenceDictionaryEntry { TargetLanguage = _language }; }
        private void Reload()
        {
            try { _entries = _backend.GetReferenceDictionaryEntries(_language); _mods = _backend.GetReferenceDictionaryScopes(); }
            catch (Exception ex) { _message = ex.Message; }
        }

        public override void DoWindowContents(Rect rect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f,0f,rect.width,32f),T("参考字典", "Reference dictionary") + " · " + _language);
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f,40f,rect.width,50f), T(
                "用户维护原词、建议译名、词性和语境。模型按当前语境选择义项；修改字典只影响后续新翻译，不重翻已有译文。",
                "Maintain terms, suggested translations, parts of speech and context. Changes guide future translations and do not retranslate existing content."));
            float leftWidth = rect.width * 0.47f;
            _search = Widgets.TextField(new Rect(0f,94f,leftWidth-184f,30f),_search);
            if (Widgets.ButtonText(new Rect(leftWidth-178f,94f,84f,30f),T("复制", "Copy")))
            {
                _editing=JsonConvert.DeserializeObject<ReferenceDictionaryEntry>(JsonConvert.SerializeObject(_editing));
                _editing.EntryId=string.Empty; _editing.IsBuiltIn=false;
            }
            if (Widgets.ButtonText(new Rect(leftWidth-88f,94f,88f,30f),T("新增", "Add"))) NewEntry();
            var visible = _entries.Where(entry => string.IsNullOrWhiteSpace(_search) ||
                (entry.SourceForm+" "+entry.TargetForm+" "+entry.ContextHint).IndexOf(_search,StringComparison.OrdinalIgnoreCase)>=0).ToList();
            Rect list = new Rect(0f,132f,leftWidth,rect.height-178f);
            Rect view = new Rect(0f,0f,list.width-18f,Math.Max(list.height,visible.Count*58f));
            Widgets.BeginScrollView(list,ref _scroll,view);
            for (int i=0;i<visible.Count;i++)
            {
                ReferenceDictionaryEntry entry=visible[i];
                Rect row=new Rect(0f,i*58f,view.width,54f);
                if (_editing.EntryId==entry.EntryId) Widgets.DrawBoxSolid(row,new Color(0.18f,0.28f,0.32f));
                string status=entry.Enabled ? string.Empty : T("[停用] ","[Disabled] ");
                Widgets.Label(new Rect(row.x+5f,row.y+3f,row.width-10f,24f),status+entry.SourceForm+" → "+entry.TargetForm);
                Text.Font=GameFont.Tiny;
                Widgets.Label(new Rect(row.x+5f,row.y+27f,row.width-10f,22f),ScopeName(entry.ScopeModIdentity)+" · "+entry.ContextHint);
                Text.Font=GameFont.Small;
                if (Widgets.ButtonInvisible(row)) _editing=JsonConvert.DeserializeObject<ReferenceDictionaryEntry>(JsonConvert.SerializeObject(entry));
            }
            Widgets.EndScrollView();
            float x=leftWidth+24f, width=rect.width-x, y=94f;
            bool previousEnabled=GUI.enabled;
            GUI.enabled=previousEnabled && !WorkflowTaskCoordinator.Instance.IsBusy;
            _editing.SourceForm=Field(x,ref y,width,T("原词／短语", "Source term / phrase"),_editing.SourceForm);
            _editing.TargetForm=Field(x,ref y,width,T("建议译名", "Suggested translation"),_editing.TargetForm);
            _editing.PartOfSpeech=Field(x,ref y,width,T("词性（可选）", "Part of speech (optional)"),_editing.PartOfSpeech);
            _editing.ContextHint=Field(x,ref y,width,T("适用语境／含义（可选）", "Context / meaning (optional)"),_editing.ContextHint);
            _editing.ExampleText=Field(x,ref y,width,T("例句（可选）", "Example (optional)"),_editing.ExampleText);
            if (Widgets.ButtonText(new Rect(x,y,width,30f),T("适用范围：", "Scope: ")+ScopeName(_editing.ScopeModIdentity)))
            {
                var options=new List<FloatMenuOption> { new FloatMenuOption(T("全部 Mod 共用", "Shared by all Mods"),()=>_editing.ScopeModIdentity=string.Empty) };
                foreach (var mod in _mods)
                {
                    string identity=mod.ModIdentity;
                    options.Add(new FloatMenuOption(mod.DisplayName+" · "+mod.PackageId,()=>_editing.ScopeModIdentity=identity));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            y+=38f;
            bool enabled=_editing.Enabled;
            Widgets.CheckboxLabeled(new Rect(x,y,width,28f),T("启用此义项", "Enable this sense"),ref enabled);
            _editing.Enabled=enabled; y+=34f;
            if (Widgets.ButtonText(new Rect(x,y,width,32f),T("保存", "Save")))
            {
                try
                {
                    _backend.SaveReferenceDictionaryEntry(_editing);
                    _editing.IsBuiltIn=false;
                    _message=T("已保存；只影响后续翻译。", "Saved; applies to future translations only."); Reload();
                }
                catch (Exception ex) { _message=ex.Message; }
            }
            GUI.enabled=previousEnabled;
            Text.Font=GameFont.Tiny;
            Widgets.Label(new Rect(x,y+42f,width,54f),T("来源记录：", "Source record: ")+_editing.SourceReference);
            Text.Font=GameFont.Small;
            Widgets.Label(new Rect(0f,rect.height-34f,rect.width,30f),_message);
        }

        private string ScopeName(string identity) => string.IsNullOrEmpty(identity)
            ? T("全部 Mod 共用", "Shared by all Mods")
            : _mods.FirstOrDefault(mod=>mod.ModIdentity==identity)?.DisplayName ?? identity;
        private static string Field(float x, ref float y, float width, string label, string value)
        {
            Widgets.Label(new Rect(x,y,width,22f),label);
            string edited=Widgets.TextField(new Rect(x,y+22f,width,28f),value ?? string.Empty);
            y+=58f; return edited;
        }
    }
}
