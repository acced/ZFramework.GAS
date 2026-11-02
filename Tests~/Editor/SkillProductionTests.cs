using System;
using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class SkillProductionTests
    {
        private const string Folder = "Assets/GASProductionRegression";
        private GameplayAbilityAsset _skill;
        [SetUp] public void Setup()
        {
            TestTags.Initialize(); AssetDatabase.CreateFolder("Assets", "GASProductionRegression");
            _skill = ScriptableObject.CreateInstance<GameplayAbilityAsset>(); AssetDatabase.CreateAsset(_skill, Folder + "/Skill.asset");
        }
        [TearDown] public void Cleanup()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<SkillEditorWindow>()) window.Close();
            AssetDatabase.DeleteAsset(Folder); Undo.ClearAll(); SkillIndex.Invalidate();
        }
        [Test] public void SemanticDiffUsesActionIdentityAndSeparatesLayout()
        {
            var first = new WaitSkillAction { displayName = "first" }; var second = new WaitSkillAction { displayName = "second" };
            _skill.activationActions = new SkillAction[] { first, second }; EditorUtility.SetDirty(_skill); AssetDatabase.SaveAssets();
            var before = SkillSemanticDiff.Capture(_skill);
            _skill.activationActions = new SkillAction[] { second, first }; first.displayName = "renamed";
            var layout = new LayoutStore(_skill); layout.SetPosition(_skill, new Vector2(250, 500)); layout.Save();
            var after = SkillSemanticDiff.Capture(_skill); var differences = SkillSemanticDiff.Compare(before, after);
            Assert.AreEqual(2, differences.Count, string.Join("\n", differences.Select(d => d.Key)));
            Assert.IsTrue(differences.Any(d => d.Key.EndsWith("/order")));
            Assert.IsTrue(differences.Any(d => d.Key.Contains(first.Id) && d.After == "renamed")); Assert.AreNotEqual(before.layout, after.layout);
        }
        [Test] public void MissingActionConfigurationHasNavigableStableIdentity()
        {
            var action = new SpawnPrefabAction(); _skill.activationActions = new SkillAction[] { action };
            using var document = new SkillDocument(); document.Open(_skill);
            using var validation = new ValidationService(document); validation.ValidateNow();
            var issue = validation.Issues.Single(i => i.Id == "GAS_ACTION");
            Assert.AreEqual("activationActions.Array.data[0]", issue.PropertyPath);
            Assert.IsTrue(SkillActionNavigation.Select(document, action.Id)); Assert.AreEqual(issue.PropertyPath, document.Selection.ElementId);
        }
        [Test] public void BatchCooldownWritesVariantOverrideAndReferenceResultsLocateField()
        {
            var parameter = new SkillParameter { defaultValue = 2, displayName = "冷却" }; _skill.parameters = new[] { parameter };
            var cooldown = SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(_skill, "Cooldown");
            using (var effect = new SerializedObject(cooldown))
            {
                effect.FindProperty("durationPolicy").enumValueIndex = (int)EffectDurationPolicy.Duration;
                effect.FindProperty("useDurationMagnitude").boolValue = true; effect.FindProperty("durationMagnitude.kind").enumValueIndex = (int)MagnitudeKind.Parameter;
                effect.FindProperty("durationMagnitude.parameterId").stringValue = parameter.Id;
                effect.FindProperty("tags.granted").arraySize = 1; effect.FindProperty("tags.granted.Array.data[0].m_Name").stringValue = "Test.Cooldown";
                effect.ApplyModifiedPropertiesWithoutUndo();
            }
            using (var so = new SerializedObject(_skill)) { so.FindProperty("cooldown").objectReferenceValue = cooldown; so.ApplyModifiedPropertiesWithoutUndo(); }
            string before = EditorJsonUtility.ToJson(cooldown); SkillBatchWindow.SetCooldown(_skill, 7);
            Assert.AreEqual(7, _skill.ResolveParameters(1).Get(parameter.Id).Number); Assert.AreEqual(before, EditorJsonUtility.ToJson(cooldown));
            Assert.IsTrue(SkillReferenceWindow.Find(cooldown).Any(r => r.Asset == _skill && r.Path == "cooldown"));
        }
        [UnityTest] public IEnumerator ReroutesAndCollapsedNodesPersistWithoutChangingRuntimeReferences()
        {
            var effect = SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(_skill, "Cost");
            using (var so = new SerializedObject(_skill)) { so.FindProperty("cost").objectReferenceValue = effect; so.ApplyModifiedPropertiesWithoutUndo(); }
            var window = SkillEditorWindow.Open(); window.Show(_skill);
            for (int i = 0; i < 10; i++) yield return null;
            var graph = window.rootVisualElement.Q<SkillGraphView>(); var link = PortRules.Links(_skill).Single();
            string before = EditorJsonUtility.ToJson(_skill);
            graph.AddReroute(link.Key, new Vector2(500, 150));
            var root = graph.nodes.ToList().Single(n => n.title == _skill.name);
            graph.ClearSelection(); graph.AddToSelection(root); graph.CollapseSelection(true); graph.AddBookmark();
            Assert.AreEqual(before, EditorJsonUtility.ToJson(_skill)); Assert.AreEqual(1, window.Document.Layout.Reroutes.Count);
            window.Document.Notify(new ChangeSet(_skill, true)); yield return null;
            Assert.AreEqual(3, graph.nodes.ToList().Count); Assert.AreEqual(2, graph.edges.ToList().Count);
            var saved = new LayoutStore(_skill); Assert.IsTrue(saved.IsCollapsed(_skill)); Assert.AreEqual(1, saved.Bookmarks.Count);
            graph.SearchNodes("Cost"); Assert.Less(root.style.opacity.value, 1);
        }
    }
}
