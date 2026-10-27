using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    [InitializeOnLoad]
    public static class SkillIndex
    {
        private static GameplayAbilityAsset[] _abilities;
        private static GameplayEffectAsset[] _effects;
        private static string[] _keys;
        public static event Action Changed;
        static SkillIndex() { EditorApplication.projectChanged += Invalidate; }
        public static IReadOnlyList<GameplayAbilityAsset> Abilities => _abilities ??= Find<GameplayAbilityAsset>();
        public static IReadOnlyList<GameplayEffectAsset> Effects => _effects ??= Find<GameplayEffectAsset>();
        private static T[] Find<T>() where T : UnityEngine.Object
            => AssetDatabase.FindAssets("t:" + typeof(T).Name).Select(AssetDatabase.GUIDToAssetPath).Distinct()
                .SelectMany(AssetDatabase.LoadAllAssetsAtPath).OfType<T>().OrderBy(a => a.name, StringComparer.OrdinalIgnoreCase).ToArray();
        public static IReadOnlyList<string> CallerKeys => _keys ??= Abilities.SelectMany(ability =>
        {
            var keys = new List<string>(); using var so = new SerializedObject(ability); var array = so.FindProperty("callerParameters");
            for (int i = 0; i < array.arraySize; i++) { string key = array.GetArrayElementAtIndex(i).FindPropertyRelative("key").stringValue; if (!string.IsNullOrEmpty(key)) keys.Add(key); }
            return keys;
        }).Distinct().OrderBy(k => k).ToArray();
        public static void Invalidate() { _abilities = null; _effects = null; _keys = null; AttributeCatalog.Invalidate(); Changed?.Invoke(); }
    }
    public sealed class LibraryPanel : VisualElement, IDisposable
    {
        private readonly SkillDocument _doc;
        private readonly Action<GameplayAbilityAsset> _open;
        private readonly ToolbarSearchField _search;
        private readonly ScrollView _library, _outline;
        public LibraryPanel(SkillDocument doc, Action<GameplayAbilityAsset> open)
        {
            _doc = doc; _open = open; AddToClassList("library-panel");
            var heading = new Label("技能库"); heading.AddToClassList("panel-heading"); Add(heading);
            _search = new ToolbarSearchField(); _search.AddToClassList("library-search"); _search.RegisterValueChangedCallback(_ => Refresh()); Add(_search);
            _library = new ScrollView { style = { flexGrow = 1, minHeight = 100 } }; Add(_library);
            var current = new Label("当前技能"); current.AddToClassList("panel-heading"); Add(current);
            _outline = new ScrollView { style = { flexGrow = 1, minHeight = 150 } }; Add(_outline);
            SkillIndex.Changed += Refresh; doc.Changed += Changed; doc.Selection.Changed += SelectionChanged; Refresh();
        }
        public void FocusSearch() => _search.Focus();
        private void Changed(ChangeSet change) { RefreshOutline(); if (change.Structure) Refresh(); }
        private bool Matches(UnityEngine.Object asset)
        {
            string query = _search.value?.Trim(); if (string.IsNullOrEmpty(query)) return true;
            if (asset.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || AssetDatabase.GetAssetPath(asset).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            using var so = new SerializedObject(asset); var p = so.GetIterator();
            while (p.Next(true)) if (p.propertyType == SerializedPropertyType.String && p.stringValue.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
        public void Refresh()
        {
            _library.Clear();
            var groups = SkillIndex.Abilities.Where(Matches).GroupBy(a => Path.GetDirectoryName(AssetDatabase.GetAssetPath(a))?.Replace('\\', '/'));
            foreach (var group in groups)
            {
                var fold = new Foldout { text = (group.Key?.Split('/').LastOrDefault() ?? "技能") + $"  ({group.Count()})", value = true };
                _library.Add(fold);
                foreach (var ability in group)
                {
                    var row = Row(ability, "◆", SkillColors.Ability, () => _open(ability)); fold.Add(row);
                    row.AddManipulator(new ContextualMenuManipulator(e =>
                    {
                        e.menu.AppendAction("打开技能", _ => _open(ability));
                        e.menu.AppendAction("复制为变体…", _ =>
                        {
                            string path = EditorUtility.SaveFilePanelInProject("复制为变体", ability.name + "_变体", "asset", "内嵌资产会复制，外部引用保持共享");
                            if (!string.IsNullOrEmpty(path)) { var copy = SkillAssetFactory.Duplicate(ability, path); SkillIndex.Invalidate(); _open(copy); }
                        });
                        e.menu.AppendAction("在 Project 中定位", _ => EditorGUIUtility.PingObject(ability));
                    }));
                }
            }
            var shared = new Foldout { text = "共享效果", value = false }; _library.Add(shared);
            foreach (var effect in SkillIndex.Effects.Where(e => !AssetDatabase.IsSubAsset(e) && Matches(e))) shared.Add(Row(effect, "↗", SkillColors.Instant, () => { _doc.AddLoose(effect); _doc.Selection.Select(effect); }));
            if (SkillIndex.Abilities.Count == 0)
                _library.Add(new HelpBox("还没有技能。点击「新建技能」选择模板开始。", HelpBoxMessageType.Info));
            RefreshOutline();
        }
        private VisualElement Row(UnityEngine.Object asset, string icon, Color color, Action click)
        {
            var button = new Button(click) { text = icon + "  " + asset.name, userData = asset, tooltip = AssetDatabase.GetAssetPath(asset) };
            button.AddToClassList("library-row"); button.style.borderLeftColor = color;
            Vector2 down = default;
            button.RegisterCallback<PointerDownEvent>(e => down = e.position);
            button.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (e.pressedButtons == 1 && Vector2.Distance(down, e.position) > 7)
                { DragAndDrop.PrepareStartDrag(); DragAndDrop.objectReferences = new[] { asset }; DragAndDrop.StartDrag(asset.name); e.StopPropagation(); }
            });
            return button;
        }
        private void RefreshOutline()
        {
            _outline.Clear(); if (_doc.Root == null) return;
            foreach (var group in _doc.Assets.Where(a => a != _doc.Root).GroupBy(a => a is SkillAnimationAsset ? "动画" : AssetDatabase.GetAssetPath(a) == AssetDatabase.GetAssetPath(_doc.Root) ? "效果 · 内嵌" : "引用 · 外部"))
            {
                var fold = new Foldout { text = group.Key + $"  ({group.Count()})", value = true }; _outline.Add(fold);
                foreach (var asset in group)
                {
                    var row = Row(asset, asset is SkillAnimationAsset ? "▶" : "▪", asset is SkillAnimationAsset ? SkillColors.Animation : SkillColors.Instant, () => _doc.Selection.Select(asset));
                    fold.Add(row);
                    row.AddManipulator(new ContextualMenuManipulator(e => e.menu.AppendAction("查找引用", _ =>
                    { var references = SkillAssetFactory.FindReferences(asset); _doc.Report(references.Count == 0 ? "没有其他资产引用" : string.Join("\n", references)); })));
                }
            }
            var parameters = _doc.Serialized(_doc.Root).FindProperty("callerParameters");
            var keys = new Foldout { text = "参数 · SetByCaller", value = true }; _outline.Add(keys);
            for (int i = 0; i < parameters.arraySize; i++) keys.Add(new Label("◇  " + parameters.GetArrayElementAtIndex(i).FindPropertyRelative("key").stringValue));
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
