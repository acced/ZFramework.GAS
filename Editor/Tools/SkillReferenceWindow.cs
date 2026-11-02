using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor
{
    public sealed class SkillReferenceWindow : EditorWindow
    {
        public readonly struct Result
        {
            public readonly ScriptableObject Asset; public readonly string Path;
            public Result(ScriptableObject asset, string path) { Asset = asset; Path = path; }
        }
        private ObjectField _asset; private TextField _tag; private ScrollView _results;
        [MenuItem("Window/ZFramework GAS/查找引用")]
        public static SkillReferenceWindow Open() => GetWindow<SkillReferenceWindow>("GAS 引用");
        public static void Show(Object asset) { var window = Open(); if (window._asset == null) window.CreateGUI(); window._asset.value = asset; window.Search(); }
        private void CreateGUI()
        {
            rootVisualElement.Clear();
            _asset = new ObjectField("资产") { objectType = typeof(ScriptableObject), allowSceneObjects = false }; rootVisualElement.Add(_asset);
            _tag = new TextField("标签（完整名称）"); rootVisualElement.Add(_tag);
            rootVisualElement.Add(new Button(Search) { text = "查找引用与字段" }); _results = new ScrollView(); rootVisualElement.Add(_results);
        }
        public static List<Result> Find(Object asset, string tag = null)
        {
            var result = new List<Result>(); var visited = new HashSet<ScriptableObject>();
            void Visit(ScriptableObject current)
            {
                if (current == null || !visited.Add(current)) return;
                using var so = new SerializedObject(current); var property = so.GetIterator();
                while (property.Next(true))
                {
                    if (asset != null && property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue == asset ||
                        !string.IsNullOrEmpty(tag) && property.propertyType == SerializedPropertyType.String && property.name == "m_Name" && property.propertyPath != "m_Name" && property.stringValue == tag)
                        result.Add(new Result(current, property.propertyPath));
                }
                foreach (var child in AssetReferences.Children(current)) Visit(child);
            }
            // Explicit reference searches include newly created assets before the incremental index's delayed update.
            foreach (string guid in AssetDatabase.FindAssets("t:GameplayAbilityAsset")) Visit(AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(AssetDatabase.GUIDToAssetPath(guid)));
            foreach (string guid in AssetDatabase.FindAssets("t:SkillActionModuleAsset")) Visit(AssetDatabase.LoadAssetAtPath<SkillActionModuleAsset>(AssetDatabase.GUIDToAssetPath(guid)));
            return result;
        }
        private void Search()
        {
            _results.Clear(); var results = Find(_asset.value, _tag.value);
            _results.Add(new Label(results.Count + " 个引用字段"));
            foreach (var result in results)
                _results.Add(new Button(() =>
                {
                    var owner = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GetAssetPath(result.Asset));
                    if (owner is GameplayAbilityAsset ability) { var window = SkillEditorWindow.Open(); window.Show(ability); window.Document.Selection.Select(result.Asset, "@field", result.Path); }
                    else if (result.Asset is SkillActionModuleAsset module) SkillModuleWindow.Show(module);
                    else Selection.activeObject = result.Asset;
                }) { text = result.Asset.name + " · " + result.Path });
        }
    }
}

