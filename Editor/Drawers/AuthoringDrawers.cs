using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public static class AttributeCatalog
    {
        private static AttributeCatalogEntry[] _entries;
        public static IReadOnlyList<AttributeCatalogEntry> Entries => _entries ??= AssetDatabase.FindAssets("t:AttributeCatalogAsset")
            .Select(g => AssetDatabase.LoadAssetAtPath<AttributeCatalogAsset>(AssetDatabase.GUIDToAssetPath(g)))
            .Where(a => a != null).SelectMany(a => a.entries).Where(e => e != null).GroupBy(e => e.id).Select(g => g.First()).OrderBy(e => e.category).ThenBy(e => e.id).ToArray();
        public static void Invalidate() => _entries = null;
        public static string Name(int id) => id < 0 ? "未使用" : Entries.FirstOrDefault(e => e.id == id)?.displayName ?? "属性 #" + id;
        [MenuItem("Tools/ZFramework GAS/从代码常量生成属性词表")]
        public static void Generate()
        {
            string path = EditorUtility.SaveFilePanelInProject("保存属性词表", "AttributeCatalog", "asset", "选择词表位置"); if (string.IsNullOrEmpty(path)) return;
            var entries = new List<AttributeCatalogEntry>();
            foreach (var type in TypeCache.GetTypesWithAttribute<AttributeCatalogSourceAttribute>())
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public))
                {
                    object value = field.GetValue(null);
                    if (value is AttributeId id) entries.Add(new AttributeCatalogEntry { id = id.Value, displayName = field.Name, category = type.Name });
                    else if (field.IsLiteral && value is int number) entries.Add(new AttributeCatalogEntry { id = number, displayName = field.Name, category = type.Name });
                }
            var catalog = ScriptableObject.CreateInstance<AttributeCatalogAsset>(); catalog.entries = entries.ToArray();
            AssetDatabase.CreateAsset(catalog, path); AssetDatabase.SaveAssets(); Invalidate(); Selection.activeObject = catalog;
        }
    }
    [CustomPropertyDrawer(typeof(AttributeIdFieldAttribute))]
    public sealed class AttributeIdDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect rect, SerializedProperty property, GUIContent label)
        {
            var entries = AttributeCatalog.Entries;
            if (entries.Count == 0) { EditorGUI.PropertyField(rect, property, label); return; }
            var labels = new List<string> { "未使用 (-1)" }; var ids = new List<int> { -1 };
            foreach (var item in entries) { labels.Add(item.Label + "  #" + item.id); ids.Add(item.id); }
            int index = ids.IndexOf(property.intValue);
            if (index < 0) { labels.Add("⚠ 未知属性 #" + property.intValue); ids.Add(property.intValue); index = ids.Count - 1; }
            EditorGUI.BeginProperty(rect, label, property);
            EditorGUI.BeginChangeCheck(); int selected = EditorGUI.Popup(rect, label.text, index, labels.ToArray());
            if (EditorGUI.EndChangeCheck()) property.intValue = ids[selected]; EditorGUI.EndProperty();
        }
    }
    [CustomPropertyDrawer(typeof(CallerKeyAttribute))]
    public sealed class CallerKeyDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect rect, SerializedProperty property, GUIContent label)
        {
            var text = rect; text.width -= 25;
            EditorGUI.PropertyField(text, property, label);
            var button = new Rect(rect.xMax - 23, rect.y, 23, rect.height);
            if (GUI.Button(button, "▾"))
            {
                var menu = new GenericMenu();
                foreach (string key in SkillIndex.CallerKeys)
                {
                    string selected = key; var target = property.serializedObject.targetObject; string path = property.propertyPath;
                    menu.AddItem(new GUIContent(selected), property.stringValue == selected, () =>
                    { using var so = new SerializedObject(target); so.FindProperty(path).stringValue = selected; so.ApplyModifiedProperties(); });
                }
                if (SkillIndex.CallerKeys.Count == 0) menu.AddDisabledItem(new GUIContent("暂无已声明参数，可直接输入"));
                menu.DropDown(button);
            }
        }
    }
    [CustomPropertyDrawer(typeof(MagnitudeAsset))]
    public sealed class MagnitudeDrawer : PropertyDrawer
    {
        public static string Summary(SerializedProperty p)
        {
            if (p == null) return "";
            int kind = p.FindPropertyRelative("kind").enumValueIndex;
            float constant = p.FindPropertyRelative("constant").floatValue;
            return kind == 0 ? constant.ToString("0.##") : kind == 2 ? "调用方 · " + p.FindPropertyRelative("callerKey").stringValue :
                (p.FindPropertyRelative("captureFrom").enumValueIndex == 0 ? "来源 · " : "目标 · ") + AttributeCatalog.Name(p.FindPropertyRelative("captureAttributeId").intValue) +
                " × " + p.FindPropertyRelative("coefficient").floatValue.ToString("0.##") + (constant != 0 ? " + " + constant.ToString("0.##") : "");
        }
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var root = new VisualElement();
            var fold = new Foldout { text = "数值：" + Summary(property), value = true }; root.Add(fold);
            void Build()
            {
                fold.Clear(); fold.text = "数值：" + Summary(property);
                var kind = property.FindPropertyRelative("kind"); var type = new PropertyField(kind, "来源类型"); fold.Add(type); type.BindProperty(kind);
                int index = kind.enumValueIndex;
                string[] paths = index == 0 ? new[] { "constant" } : index == 2 ? new[] { "callerKey" } : new[] { "captureFrom", "captureAttributeId", "coefficient", "constant" };
                foreach (var path in paths) { var field = new PropertyField(property.FindPropertyRelative(path)); fold.Add(field); field.BindProperty(property.FindPropertyRelative(path)); }
                type.RegisterCallback<SerializedPropertyChangeEvent>(_ => root.schedule.Execute(Build));
            }
            Build(); return root;
        }
        public override float GetPropertyHeight(SerializedProperty p, GUIContent label)
            => (p.FindPropertyRelative("kind").enumValueIndex == 1 ? 6 : 3) * (EditorGUIUtility.singleLineHeight + 2);
        public override void OnGUI(Rect rect, SerializedProperty p, GUIContent label)
        {
            float h = EditorGUIUtility.singleLineHeight; rect.height = h;
            EditorGUI.LabelField(rect, "数值：" + Summary(p)); rect.y += h + 2;
            EditorGUI.PropertyField(rect, p.FindPropertyRelative("kind"), new GUIContent("来源")); rect.y += h + 2;
            int kind = p.FindPropertyRelative("kind").enumValueIndex;
            foreach (var path in kind == 0 ? new[] { "constant" } : kind == 2 ? new[] { "callerKey" } : new[] { "captureFrom", "captureAttributeId", "coefficient", "constant" })
            { EditorGUI.PropertyField(rect, p.FindPropertyRelative(path)); rect.y += h + 2; }
        }
    }
    [CustomPropertyDrawer(typeof(ModifierAsset))]
    public sealed class ModifierDrawer : PropertyDrawer
    {
        public static string Summary(SerializedProperty p) => AttributeCatalog.Name(p.FindPropertyRelative("targetAttributeId").intValue) + " " +
            p.FindPropertyRelative("operation").enumDisplayNames[p.FindPropertyRelative("operation").enumValueIndex] + " " + MagnitudeDrawer.Summary(p.FindPropertyRelative("magnitude"));
        public override VisualElement CreatePropertyGUI(SerializedProperty p)
        {
            var fold = new Foldout { text = Summary(p), value = true };
            foreach (string name in new[] { "targetAttributeId", "operation", "magnitude" }) fold.Add(new PropertyField(p.FindPropertyRelative(name)));
            fold.RegisterCallback<SerializedPropertyChangeEvent>(_ => fold.text = Summary(p)); return fold;
        }
        public override float GetPropertyHeight(SerializedProperty p, GUIContent label)
            => EditorGUIUtility.singleLineHeight * 3 + 8 + EditorGUI.GetPropertyHeight(p.FindPropertyRelative("magnitude"));
        public override void OnGUI(Rect rect, SerializedProperty p, GUIContent label)
        {
            float h = EditorGUIUtility.singleLineHeight; rect.height = h; EditorGUI.LabelField(rect, Summary(p)); rect.y += h + 2;
            foreach (var field in new[] { "targetAttributeId", "operation", "magnitude" })
            { var child = p.FindPropertyRelative(field); rect.height = EditorGUI.GetPropertyHeight(child); EditorGUI.PropertyField(rect, child, true); rect.y += rect.height + 2; }
        }
    }
    [CustomPropertyDrawer(typeof(AnimationBackend), true), CustomPropertyDrawer(typeof(TaskSpawner), true)]
    public sealed class StrategyDrawer : PropertyDrawer
    {
        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var root = new VisualElement();
            var baseType = fieldInfo.FieldType;
            var types = TypeCache.GetTypesDerivedFrom(baseType).Where(t => !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null).OrderBy(t => t.Name).ToArray();
            var names = new List<string> { "未配置" }; names.AddRange(types.Select(t => t.Name));
            int index = Array.FindIndex(types, t => t == property.managedReferenceValue?.GetType()) + 1;
            var popup = new PopupField<string>(property.displayName, names, index); root.Add(popup);
            var content = new VisualElement(); root.Add(content);
            void Fields()
            {
                content.Clear(); var iterator = property.Copy(); var end = iterator.GetEndProperty();
                if (!iterator.NextVisible(true)) return;
                while (!SerializedProperty.EqualContents(iterator, end))
                { var field = new PropertyField(iterator.Copy()); content.Add(field); field.BindProperty(iterator.Copy()); if (!iterator.NextVisible(false)) break; }
            }
            Fields();
            popup.RegisterValueChangedCallback(e =>
            {
                property.serializedObject.Update(); int selected = names.IndexOf(e.newValue);
                property.managedReferenceValue = selected == 0 ? null : Activator.CreateInstance(types[selected - 1]);
                property.serializedObject.ApplyModifiedProperties(); Fields();
            });
            return root;
        }
    }
}
