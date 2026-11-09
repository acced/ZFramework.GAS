using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public static class SkillIndex
    {
        private static GameplayAbilityAsset[] _abilities;
        private static GameplayEffectAsset[] _effects;
        private static readonly Dictionary<UnityEngine.Object, string> SearchCache = new();
        public static event Action Changed;
        public static IReadOnlyList<GameplayAbilityAsset> Abilities => _abilities ??= Find<GameplayAbilityAsset>();
        public static IReadOnlyList<GameplayEffectAsset> Effects => _effects ??= Find<GameplayEffectAsset>();
        private static T[] Find<T>() where T : UnityEngine.Object
            => AssetDatabase.FindAssets("t:" + typeof(T).Name).Select(AssetDatabase.GUIDToAssetPath).Distinct()
                .SelectMany(AssetDatabase.LoadAllAssetsAtPath).OfType<T>().OrderBy(a => a.name, StringComparer.OrdinalIgnoreCase).ToArray();
        public static string SearchText(UnityEngine.Object asset)
        {
            if (SearchCache.TryGetValue(asset, out var text)) return text;
            var builder = new System.Text.StringBuilder(asset.name).Append(' ').Append(AssetDatabase.GetAssetPath(asset));
            using var so = new SerializedObject(asset); var property = so.GetIterator();
            while (property.Next(true)) if (property.propertyType == SerializedPropertyType.String) builder.Append(' ').Append(property.stringValue);
            return SearchCache[asset] = builder.ToString();
        }
        public static void Search(string query, List<GameplayAbilityAsset> results)
        {
            results.Clear(); var assets = Abilities;
            for (int i = 0; i < assets.Count; i++) if (string.IsNullOrEmpty(query) || SearchText(assets[i]).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) results.Add(assets[i]);
        }
        internal static void UpdatePaths(string[] imported, string[] deleted, string[] moved, string[] oldPaths)
        {
            var touched = new HashSet<string>(imported.Concat(deleted).Concat(moved).Concat(oldPaths).Where(p => p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)));
            if (touched.Count == 0) return;
            foreach (var key in SearchCache.Keys.ToArray()) if (key == null || touched.Contains(AssetDatabase.GetAssetPath(key))) SearchCache.Remove(key);
            if (_abilities != null || _effects != null)
            {
                var assets = imported.Concat(moved).Where(p => touched.Contains(p)).Distinct().SelectMany(AssetDatabase.LoadAllAssetsAtPath).ToArray();
                if (_abilities != null) _abilities = _abilities.Where(a => a != null && !touched.Contains(AssetDatabase.GetAssetPath(a))).Concat(assets.OfType<GameplayAbilityAsset>()).Distinct().OrderBy(a => a.name, StringComparer.OrdinalIgnoreCase).ToArray();
                if (_effects != null) _effects = _effects.Where(a => a != null && !touched.Contains(AssetDatabase.GetAssetPath(a))).Concat(assets.OfType<GameplayEffectAsset>()).Distinct().OrderBy(a => a.name, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            AttributeCatalog.Invalidate(); Changed?.Invoke();
        }
        public static void Invalidate() { _abilities = null; _effects = null; SearchCache.Clear(); AttributeCatalog.Invalidate(); Changed?.Invoke(); }
    }
    public sealed class SkillIndexPostprocessor : AssetPostprocessor
    {
        private static readonly HashSet<string> Imported = new(), Deleted = new();
        internal static event Action<string[]> ImportedAssets;
        private static bool _queued;
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths)
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            if (!imported.Concat(deleted).Concat(moved).Concat(oldPaths)
                .Any(p => p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))) return;
            Imported.UnionWith(imported); Imported.UnionWith(moved); Deleted.UnionWith(deleted); Deleted.UnionWith(oldPaths);
            if (_queued) return; _queued = true;
            EditorApplication.delayCall += () =>
            {
                _queued = false; var added = Imported.ToArray(); var removed = Deleted.ToArray(); Imported.Clear(); Deleted.Clear();
                SkillIndex.UpdatePaths(added, removed, Array.Empty<string>(), Array.Empty<string>());
                ImportedAssets?.Invoke(added);
            };
        }
    }
    public sealed class LibraryPanel : VisualElement, IDisposable
    {
        private readonly SkillDocument _doc;
        private readonly Action<GameplayAbilityAsset> _open;
        private readonly ToolbarSearchField _search;
        private readonly ScrollView _outline;
        private readonly ListView _library;
        private bool _parameters;
        private readonly ScrollView _paramView=new();
        private readonly List<ScriptableObject> _visible = new();
        public LibraryPanel(SkillDocument doc, Action<GameplayAbilityAsset> open)
        {
            _doc = doc; _open = open; AddToClassList("library-panel");
            var heading = new Label("技能库"); heading.AddToClassList("panel-heading"); Add(heading);
            var tabs=new Toolbar(); tabs.Add(new ToolbarButton(()=>{_parameters=false;Refresh();}){text="资产"});
            tabs.Add(new ToolbarButton(()=>{_parameters=true;Refresh();}){text="参数"}); Add(tabs);
            _paramView.style.display=DisplayStyle.None;Add(_paramView);
            _search = new ToolbarSearchField(); _search.AddToClassList("library-search"); _search.RegisterValueChangedCallback(_ => RefreshList()); Add(_search);
            _library = new ListView { itemsSource = _visible, fixedItemHeight = 30, selectionType = SelectionType.None,
                style = { flexGrow = 1, minHeight = 100 } }; Add(_library);
            _library.makeItem = () =>
            {
                var row = new Button(); row.AddToClassList("library-row");
                row.clicked += () => { if (row.userData is GameplayAbilityAsset ability) _open(ability); else if (row.userData is ScriptableObject asset) { _doc.AddLoose(asset); _doc.Selection.Select(asset); } };
                row.AddManipulator(new ContextualMenuManipulator(e =>
                {
                    e.menu.AppendAction("在 Project 中定位", _ => EditorGUIUtility.PingObject(row.userData as UnityEngine.Object));
                }));
                InstallAssetDrag(row);
                return row;
            };
            _library.bindItem = (element, i) =>
            {
                var row = (Button)element; var asset = _visible[i]; row.userData = asset;
                row.text = (asset is GameplayAbilityAsset ? "◆  " : "↗  ") + asset.name; row.tooltip = AssetDatabase.GetAssetPath(asset);
                row.EnableInClassList("selected-row", asset == _doc.Selection.Asset);
            };
            var current = new Label("当前技能"); current.AddToClassList("panel-heading"); Add(current);
            _outline = new ScrollView { style = { flexGrow = 1, minHeight = 150 } }; Add(_outline);
            SkillIndex.Changed += Refresh; doc.Changed += Changed; doc.Selection.Changed += SelectionChanged; Refresh();
        }
        public void FocusSearch() => _search.Focus();
        public void Search(string query) => _search.value = query;
        private void Changed(ChangeSet change)
        { if (_parameters) RefreshParameters();
          if (change.Structure || change.Paths.Any(p => p == "m_Name" || p.Contains("displayName") || p.Contains("cue"))) RefreshOutline(); }
        public void Refresh()
        { RefreshList(); if (!_parameters) RefreshOutline(); }
        private void RefreshList()
        {
            _paramView.style.display = _parameters ? DisplayStyle.Flex : DisplayStyle.None;
            _library.style.display = _parameters ? DisplayStyle.None : DisplayStyle.Flex;
            if (_parameters) { RefreshParameters(); return; }
            string query = _search.value?.Trim();
            bool Matches(UnityEngine.Object asset) => string.IsNullOrEmpty(query) || SkillIndex.SearchText(asset).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            _visible.Clear();
            foreach (var asset in SkillIndex.Abilities) if (Matches(asset)) _visible.Add(asset);
            foreach (var asset in SkillIndex.Effects) if (!AssetDatabase.IsSubAsset(asset) && Matches(asset)) _visible.Add(asset);
            _library.RefreshItems();
        }
        private void RefreshParameters()
        {
            _paramView.Clear(); var owner = _doc.Root;
            if (owner == null) return;
            foreach (var p in owner.parameters.Where(p => p != null))
            {
                if (!string.IsNullOrEmpty(_search.value) && p.displayName?.IndexOf(_search.value, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int index = Array.IndexOf(owner.parameters, p);
                var button = new Button(() => _doc.Selection.Select(owner, "@field", $"parameters.Array.data[{index}]"))
                { text = p.displayName + " · " + p.kind, tooltip = "点击编辑声明，拖到数值字段以绑定", userData = p };
                InstallParameterDrag(button); _paramView.Add(button);
            }
        }
        private VisualElement Row(UnityEngine.Object asset, string icon, Color color, Action click)
        {
            var button = new Button(click) { text = icon + "  " + asset.name, userData = asset, tooltip = AssetDatabase.GetAssetPath(asset) };
            button.AddToClassList("library-row"); button.style.borderLeftColor = color;
            button.EnableInClassList("selected-row", asset == _doc.Selection.Asset);
            InstallAssetDrag(button);
            return button;
        }
        // Clickable captures the same pointer for clicks. Listen before Clickable and only
        // promote the gesture we own. A virtualized row may be rebound while pressed.
        internal static void InstallAssetDrag(Button row,Action<UnityEngine.Object> begin=null)
            => InstallRowDrag<UnityEngine.Object>(row,begin??(asset=>{
                DragAndDrop.PrepareStartDrag();DragAndDrop.objectReferences=new[]{asset};DragAndDrop.StartDrag(asset.name);
            }));
        internal static void InstallParameterDrag(Button row,Action<SkillParameter> begin=null)
            => InstallRowDrag<SkillParameter>(row,begin??(p=>{
                DragAndDrop.PrepareStartDrag();DragAndDrop.SetGenericData("GAS.SkillParameter",p.Id);DragAndDrop.StartDrag(p.displayName);
            }));
        private static void InstallRowDrag<T>(Button row,Action<T> begin) where T:class
        {
            int pointer=-1;Vector2 down=default;T item=null;
            void Reset(){int id=pointer;pointer=-1;item=null;if(id>=0 && row.HasPointerCapture(id))row.ReleasePointer(id);}
            row.RegisterCallback<PointerDownEvent>(e=>{
                if(e.button!=0 || pointer>=0 || row.userData is not T asset || asset is UnityEngine.Object o && o==null)return;
                pointer=e.pointerId;down=e.position;item=asset;row.CapturePointer(pointer);
            },TrickleDown.TrickleDown);
            row.RegisterCallback<PointerMoveEvent>(e=>{
                if(e.pointerId!=pointer)return;
                if((e.pressedButtons&1)==0 || !row.HasPointerCapture(pointer) || !ReferenceEquals(row.userData,item) || item is UnityEngine.Object o && o==null){Reset();return;}
                if(((Vector2)e.position-down).sqrMagnitude<=49)return;
                var dragged=item;Reset();e.StopImmediatePropagation();
                begin(dragged);
            },TrickleDown.TrickleDown);
            row.RegisterCallback<PointerUpEvent>(e=>{if(e.pointerId==pointer && e.button==0){pointer=-1;item=null;}},TrickleDown.TrickleDown);
            row.RegisterCallback<PointerCaptureOutEvent>(e=>{if(e.pointerId==pointer){pointer=-1;item=null;}});
            row.RegisterCallback<PointerCancelEvent>(e=>{if(e.pointerId==pointer)Reset();},TrickleDown.TrickleDown);
            row.RegisterCallback<DetachFromPanelEvent>(_=>Reset());
        }
        private void RefreshOutline()
        {
            _outline.Clear(); if (_doc.Root == null) return;
            foreach (var group in _doc.Assets.Where(a => a != _doc.Root).GroupBy(a => SkillEditorExtensions.Supports(a) ? SkillEditorExtensions.Timeline.DisplayName : AssetDatabase.GetAssetPath(a) == AssetDatabase.GetAssetPath(_doc.Root) ? "效果 · 内嵌" : "引用 · 外部"))
            {
                var fold = new Foldout { text = group.Key + $"  ({group.Count()})", value = true }; _outline.Add(fold);
                foreach (var asset in group)
                {
                    var row = Row(asset, SkillEditorExtensions.Supports(asset) ? "▶" : "▪", SkillEditorExtensions.Supports(asset) ? SkillColors.Animation : SkillColors.Instant, () => _doc.Selection.Select(asset));
                    fold.Add(row);
                }
            }
            var cues = new Foldout { text = "Cue", value = true }; _outline.Add(cues);
            var seen = new HashSet<string>();
            foreach (var asset in _doc.Assets)
            {
                var p = _doc.Serialized(asset).GetIterator();
                while (p.Next(true)) if (p.propertyType == SerializedPropertyType.String && p.propertyPath.Contains("cue") && p.name == "m_Name" && !string.IsNullOrEmpty(p.stringValue) && seen.Add(p.stringValue)) cues.Add(new Label("◆  " + p.stringValue));
            }
            SelectionChanged();
        }
        private void SelectionChanged()
        { this.Query<Button>(className: "library-row").ForEach(row => row.EnableInClassList("selected-row", row.userData as UnityEngine.Object == _doc.Selection.Asset)); }
        public void Dispose() { SkillIndex.Changed -= Refresh; _doc.Changed -= Changed; _doc.Selection.Changed -= SelectionChanged; }
    }
}
