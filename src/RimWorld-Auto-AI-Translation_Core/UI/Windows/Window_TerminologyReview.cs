using AutoTranslator_Core.Terminology;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace AutoTranslator_Core
{
    public sealed class Window_TerminologyReview : Window
    {
        private const float RowHeight = 142f;
        private readonly Dictionary<string, string> _targets = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _roles = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, ScopeChoice> _scopes = new Dictionary<string, ScopeChoice>(StringComparer.Ordinal);
        private List<TerminologyReviewItem> _items = new List<TerminologyReviewItem>();
        private Vector2 _scroll = Vector2.zero;
        private string _search = string.Empty;

        private sealed class ScopeChoice
        {
            internal string Kind;
            internal string Id;
            internal string Label;
        }

        public override Vector2 InitialSize => new Vector2(1120f, 780f);

        public Window_TerminologyReview()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            Reload();
        }

        public override void DoWindowContents(Rect inRect)
        {
            bool previousBypass = Patch_GUI_Label_GUIContent.BypassInterceptor;
            Patch_GUI_Label_GUIContent.BypassInterceptor = true;
            try
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "ATC_Terminology_ReviewTitle".Translate());
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(0f, 36f, inRect.width, 42f), "ATC_Terminology_ReviewNotice".Translate());
                Text.Font = GameFont.Small;
                _search = Widgets.TextField(new Rect(0f, 82f, inRect.width - 150f, 30f), _search ?? string.Empty);
                if (Widgets.ButtonText(new Rect(inRect.width - 140f, 82f, 140f, 30f), "ATC_Terminology_Refresh".Translate()))
                    Reload();

                List<TerminologyReviewItem> visible = FilteredItems();
                Rect outRect = new Rect(0f, 120f, inRect.width, inRect.height - 172f);
                Rect viewRect = new Rect(0f, 0f, outRect.width - 20f, Math.Max(outRect.height, visible.Count * RowHeight));
                Widgets.BeginScrollView(outRect, ref _scroll, viewRect);
                int first = Mathf.Max(0, Mathf.FloorToInt(_scroll.y / RowHeight) - 1);
                int last = Mathf.Min(visible.Count - 1, Mathf.CeilToInt((_scroll.y + outRect.height) / RowHeight) + 1);
                for (int index = first; index <= last; index++)
                    DrawRow(visible[index], new Rect(0f, index * RowHeight, viewRect.width, RowHeight));
                Widgets.EndScrollView();

                Widgets.Label(new Rect(0f, inRect.height - 42f, inRect.width - 160f, 36f),
                    "ATC_Terminology_ReviewCount".Translate(visible.Count));
                if (Widgets.ButtonText(new Rect(inRect.width - 140f, inRect.height - 42f, 140f, 36f), "ATC_ContactAuthor_Close".Translate()))
                    Close();
            }
            finally
            {
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                Patch_GUI_Label_GUIContent.BypassInterceptor = previousBypass;
            }
        }

        private void DrawRow(TerminologyReviewItem item, Rect row)
        {
            TerminologyCandidate term = item.Term;
            Widgets.DrawHighlightIfMouseover(row);
            if (item.HasConflict) Widgets.DrawBoxSolid(new Rect(row.x, row.y, 5f, row.height - 2f), new Color(0.75f, 0.18f, 0.12f));
            string heading = term.SourceForm + "  <size=10><color=#888888>" + term.Status + " · " + term.EvidenceKind + "</color></size>";
            Widgets.Label(new Rect(row.x + 10f, row.y + 5f, row.width - 20f, 26f), heading);
            string detail = item.HasConflict
                ? "ATC_Terminology_Conflict".Translate(string.Join(" / ", item.ConflictingTargets.ToArray())).ToString()
                : (term.AgentReason ?? string.Empty);
            GUI.color = item.HasConflict ? new Color(1f, 0.55f, 0.4f) : Color.gray;
            Widgets.Label(new Rect(row.x + 10f, row.y + 31f, row.width - 20f, 22f), detail);
            GUI.color = Color.white;

            float fieldWidth = (row.width - 330f) * 0.58f;
            Widgets.Label(new Rect(row.x + 10f, row.y + 58f, 72f, 28f), "ATC_Terminology_Target".Translate());
            _targets[term.TermId] = Widgets.TextField(
                new Rect(row.x + 80f, row.y + 56f, fieldWidth, 30f),
                _targets.TryGetValue(term.TermId, out string target) ? target : string.Empty);
            Widgets.Label(new Rect(row.x + 90f + fieldWidth, row.y + 58f, 72f, 28f), "ATC_Terminology_Role".Translate());
            _roles[term.TermId] = Widgets.TextField(
                new Rect(row.x + 148f + fieldWidth, row.y + 56f, row.width - fieldWidth - 488f, 30f),
                _roles.TryGetValue(term.TermId, out string role) ? role : string.Empty);

            ScopeChoice scope = _scopes.TryGetValue(term.TermId, out ScopeChoice selected) ? selected : DefaultScope(term);
            _scopes[term.TermId] = scope;
            if (Widgets.ButtonText(new Rect(row.xMax - 322f, row.y + 56f, 160f, 30f), scope.Label))
                OpenScopeMenu(term);
            GUI.color = string.IsNullOrWhiteSpace(_targets[term.TermId]) ? Color.gray : Color.white;
            if (Widgets.ButtonText(new Rect(row.xMax - 156f, row.y + 56f, 92f, 30f), "ATC_Terminology_Approve".Translate()) &&
                !string.IsNullOrWhiteSpace(_targets[term.TermId]))
            {
                TerminologyRuntime.GetCache().Approve(
                    term.TermId, _targets[term.TermId], _roles[term.TermId], scope.Kind, scope.Id);
                Reload();
            }
            GUI.color = Color.white;
            if (Widgets.ButtonText(new Rect(row.xMax - 58f, row.y + 56f, 58f, 30f), "ATC_Terminology_Reject".Translate()))
            {
                TerminologyRuntime.GetCache().Reject(term.TermId);
                Reload();
            }

            string context = (term.Contexts ?? new List<string>()).FirstOrDefault() ?? string.Empty;
            Widgets.Label(new Rect(row.x + 10f, row.y + 92f, row.width - 20f, 42f),
                "<size=10><color=#777777>" + context + "</color></size>");
            Widgets.DrawLineHorizontal(row.x, row.yMax - 2f, row.width);
        }

        private void OpenScopeMenu(TerminologyCandidate term)
        {
            List<FloatMenuOption> options = BuildScopes(term)
                .Select(choice => new FloatMenuOption(choice.Label, () => _scopes[term.TermId] = choice))
                .ToList();
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static List<ScopeChoice> BuildScopes(TerminologyCandidate term)
        {
            var choices = new List<ScopeChoice>();
            foreach (string packageId in (term.PackageIds ?? new List<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
                choices.Add(new ScopeChoice { Kind = TerminologyScope.Mod, Id = packageId, Label = "ATC_Terminology_ScopeMod".Translate(packageId) });
            string sourceKind = string.IsNullOrWhiteSpace(term.SourceScopeKind) ? term.ScopeKind : term.SourceScopeKind;
            string sourceId = string.IsNullOrWhiteSpace(term.SourceScopeId) ? term.ScopeId : term.SourceScopeId;
            if (sourceKind == TerminologyScope.Mod && !string.IsNullOrWhiteSpace(sourceId) &&
                !choices.Any(choice => choice.Kind == TerminologyScope.Mod && choice.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new ScopeChoice { Kind = TerminologyScope.Mod, Id = sourceId, Label = "ATC_Terminology_ScopeMod".Translate(sourceId) });
            if (sourceKind == TerminologyScope.ModGroup && !string.IsNullOrWhiteSpace(sourceId))
                choices.Add(new ScopeChoice { Kind = TerminologyScope.ModGroup, Id = sourceId, Label = "ATC_Terminology_ScopeGroup".Translate(sourceId) });
            choices.Add(new ScopeChoice { Kind = TerminologyScope.Global, Id = "global", Label = "ATC_Terminology_ScopeGlobal".Translate() });
            return choices;
        }

        private static ScopeChoice DefaultScope(TerminologyCandidate term)
        {
            return BuildScopes(term).First();
        }

        private List<TerminologyReviewItem> FilteredItems()
        {
            string search = (_search ?? string.Empty).Trim();
            if (search.Length == 0) return _items;
            return _items.Where(item =>
                (item.Term.SourceForm ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (_targets.TryGetValue(item.Term.TermId, out string target) ? target : string.Empty)
                    .IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                (item.Term.AgentReason ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        private void Reload()
        {
            _items = TerminologyRuntime.GetCache().GetReviewQueue().ToList();
            foreach (TerminologyReviewItem item in _items)
            {
                if (!_targets.ContainsKey(item.Term.TermId)) _targets[item.Term.TermId] = item.Term.Target ?? string.Empty;
                if (!_roles.ContainsKey(item.Term.TermId)) _roles[item.Term.TermId] = item.Term.SemanticRole ?? string.Empty;
                if (!_scopes.ContainsKey(item.Term.TermId)) _scopes[item.Term.TermId] = DefaultScope(item.Term);
            }
        }
    }
}
