using System;
using System.Collections;
using System.Collections.Generic;
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
        [Test] public void NestedModuleBuildErrorLocatesTheCallInEachOwningAsset()
        {
            var inner = SkillAssetFactory.AddEmbedded<SkillActionModuleAsset>(_skill, "Inner"); inner.actions = new SkillAction[] { new SpawnPrefabAction() };
            var outer = SkillAssetFactory.AddEmbedded<SkillActionModuleAsset>(_skill, "Outer"); var nested = new CallModuleAction { module = inner }; outer.actions = new SkillAction[] { nested };
            var call = new CallModuleAction { module = outer }; _skill.activationActions = new SkillAction[] { call };
            var error = Assert.Throws<SkillActionException>(() => _skill.BuildActions()); Assert.AreEqual(call.Id, error.ActionId);
            Assert.AreEqual(nested.Id, (error.InnerException as SkillActionException)?.ActionId);
            using var document = new SkillDocument(); document.Open(_skill); using var validation = new ValidationService(document); validation.ValidateNow();
            var issue = validation.Issues.Single(i => i.Id == "GAS_ACTION" && i.Asset == _skill);
            Assert.AreEqual("activationActions.Array.data[0]", issue.PropertyPath); StringAssert.Contains("Inner", issue.Message);
            Assert.IsTrue(SkillActionNavigation.Select(document, call.Id));
        }
        [Test] public void TerminalSectionEndMarkerHasANavigableWarning()
        {
            var animation = SkillAssetFactory.AddEmbedded<SkillAnimationAsset>(_skill, "Animation");
            animation.sections = new[] { new AnimationSection { displayName = "结束段", start = 0, end = 1 } };
            using (var so = new SerializedObject(animation))
            { so.FindProperty("clipName").stringValue = "a"; var markers = so.FindProperty("markers"); markers.arraySize = 1; markers.GetArrayElementAtIndex(0).FindPropertyRelative("time").floatValue = 1; so.ApplyModifiedPropertiesWithoutUndo(); }
            using var document = new SkillDocument(); document.Open(_skill); var issues = new List<Issue>();
            new SectionEndRule().Validate(new ValidationContext(document), animation, issues);
            Assert.AreEqual(1, issues.Count); Assert.AreEqual(IssueSeverity.Warning, issues[0].Severity);
            Assert.AreEqual("markers.Array.data[0]", issues[0].PropertyPath); Assert.IsNotEmpty(issues[0].ElementId);
        }
        [Serializable] private sealed class FaultAction : SkillAction
        {
            public bool DuringValidation;
            public override SkillActionDefinition Build(SkillActionBuildContext builder)
                => Define("fault",
                    _ => throw new InvalidOperationException("execution probe"));
        }
        [TestCase(0, 0), TestCase(1, 0), TestCase(2, 0), TestCase(3, 0), TestCase(3, -1), TestCase(3, 1)]
        public void SectionEndWarningsIncludeLoopsJumpsQueuedSuccessorsAndRounding(int successor, int ulp)
        {
            var animation = SkillAssetFactory.AddEmbedded<SkillAnimationAsset>(_skill, "Animation");
            var a = new AnimationSection { displayName = "A", start = 0, end = .5f };
            var b = new AnimationSection { displayName = "B", start = successor == 2 ? .75f : .5f, end = 1 };
            a.nextSectionId = successor == 0 ? null : successor == 1 ? a.Id : b.Id;
            animation.sections = new[] { a, b };
            float time = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(.5f) + ulp);
            using (var so = new SerializedObject(animation))
            {
                so.FindProperty("clipName").stringValue = "a";
                foreach (var name in new[] { "markers" })
                {
                    var events = so.FindProperty(name); events.arraySize = 1;
                    events.GetArrayElementAtIndex(0).FindPropertyRelative("time").floatValue = time;
                }
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            using var document = new SkillDocument(); document.Open(_skill); var issues = new List<Issue>();
            new SectionEndRule().Validate(new ValidationContext(document), animation, issues);
            Assert.AreEqual(ulp == 0 && b.start == time ? 0 : 1, issues.Count);
            foreach (var issue in issues) { StringAssert.Contains("排他终点", issue.Message); Assert.IsNotEmpty(issue.ElementId); }
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
        private sealed class DurationDriver : IAnimationDriver
        {
            public float Play(string clip, float duration, bool loop) => duration;
            public void Advance(float time) { }
            public void Stop() { }
        }
        [Test] public void RoundedExclusiveEndWarningPredictsAnActualSkippedMarkerOnlyWhenStretched()
        {
            const float marker = .7500001192092896f, end = .7500001788139343f, authored = 1f, stretched = .75f;
            Assert.Less(marker, end);
            Assert.AreEqual(BitConverter.SingleToInt32Bits(marker * (stretched / authored)), BitConverter.SingleToInt32Bits(end * (stretched / authored)));
            var animation = SkillAssetFactory.AddEmbedded<SkillAnimationAsset>(_skill, "Animation");
            animation.sections = new[] { new AnimationSection { start = 0, end = end, inputBufferEnd = end } };
            using (var so = new SerializedObject(animation))
            {
                so.FindProperty("clipName").stringValue = "a"; so.FindProperty("duration").floatValue = authored;
                var markers = so.FindProperty("markers"); markers.arraySize = 1;
                markers.GetArrayElementAtIndex(0).FindPropertyRelative("time").floatValue = marker; so.ApplyModifiedPropertiesWithoutUndo();
            }
            using var document = new SkillDocument(); document.Open(_skill); var issues = new List<Issue>();
            new SectionEndRule().Validate(new ValidationContext(document), animation, issues);
            Assert.AreEqual("markers.Array.data[0]", issues.Single().PropertyPath);
            Assert.AreEqual(BitConverter.SingleToInt32Bits(marker), BitConverter.SingleToInt32Bits(animation.Definition.Markers[0].Time));
            Assert.AreEqual(BitConverter.SingleToInt32Bits(end), BitConverter.SingleToInt32Bits(animation.Definition.Sections[0].End));
            int Hits(float actual)
            {
                var owner = new AbilitySystemComponent(); var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec);
                int hits = 0; owner.StartTask(spec, new AnimationTask(animation.Definition, new DurationDriver(), durationOverride: actual, onMarker: (_, _) => hits++));
                owner.Tick(actual); owner.Clear(); return hits;
            }
            Assert.AreEqual(1, Hits(authored)); Assert.AreEqual(0, Hits(stretched));
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
