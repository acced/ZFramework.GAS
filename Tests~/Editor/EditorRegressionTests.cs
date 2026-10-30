using System;
using System.Collections;
using System.Linq;
using GameplayTags;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class EditorRegressionTests
    {
        private const string Folder = "Assets/GASRegression";
        private SkillDocument _doc;
        private GameplayAbilityAsset _ability;
        [OneTimeSetUp]
        public void InitializeTags()
        {
            TestTags.Initialize();
        }
        [SetUp]
        public void SetUp()
        {
            AssetDatabase.CreateFolder("Assets", "GASRegression");
            _ability = ScriptableObject.CreateInstance<GameplayAbilityAsset>(); _ability.name = "TestAbility";
            AssetDatabase.CreateAsset(_ability, Folder + "/Ability.asset");
            _doc = new SkillDocument(); _doc.Open(_ability); Undo.ClearAll();
        }
        [TearDown]
        public void TearDown()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<SkillEditorWindow>()) window.Close();
            _doc.Dispose(); Undo.ClearAll(); AssetDatabase.DeleteAsset(Folder); SkillIndex.Invalidate();
        }
        private GameplayEffectAsset Effect(string name = "Effect", EffectDurationPolicy policy = EffectDurationPolicy.Instant)
        {
            var asset = SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(_ability, name);
            Edit(asset, so => { so.FindProperty("durationPolicy").enumValueIndex = (int)policy; so.FindProperty("duration").floatValue = 1; });
            _doc.Notify(new ChangeSet(asset, true)); return asset;
        }
        private SkillAnimationAsset Animation()
            => (SkillAnimationAsset)SkillAssetFactory.Create(_doc, typeof(SkillAnimationAsset), "Animation", 0, _ability, new PortId(PortKind.Animation));
        private static void Edit(Object asset, Action<SerializedObject> change)
        { using var so = new SerializedObject(asset); change(so); so.ApplyModifiedPropertiesWithoutUndo(); }
        private static void GrantTag(Object asset)
            => Edit(asset, so => { var list = so.FindProperty("tags.granted"); list.arraySize = 1; list.GetArrayElementAtIndex(0).FindPropertyRelative("m_Name").stringValue = "Test.Cooldown"; });
        private string Snapshot(Object asset) => EditorJsonUtility.ToJson(asset);
        [Test]
        public void CommandUndoRedoRestoresDataAndChangeSet()
        {
            var effect = Effect(); string before = Snapshot(effect); ChangeSet observed = default; _doc.Changed += change => observed = change;
            EditCommand.Run(_doc, "设置测试时长", effect, so => so.FindProperty("duration").floatValue = 2.5f, false, "duration");
            string after = Snapshot(effect); Assert.AreNotEqual(before, after); Assert.AreEqual(effect, observed.Asset); CollectionAssert.Contains(observed.Paths, "duration");
            Undo.PerformUndo(); Assert.AreEqual(before, Snapshot(effect)); Undo.PerformRedo(); Assert.AreEqual(after, Snapshot(effect));
        }
        [TestCase(EffectDurationPolicy.Instant, true)]
        [TestCase(EffectDurationPolicy.Duration, false)]
        [TestCase(EffectDurationPolicy.Infinite, false)]
        public void CostPortEnforcesInstant(EffectDurationPolicy policy, bool accepted)
        { Assert.AreEqual(accepted, PortRules.CanAttach(_ability, new PortId(PortKind.Cost), Effect(policy: policy), out _)); }
        [Test]
        public void CooldownRequiresGrantedTag()
        {
            var effect = Effect(); Assert.IsFalse(PortRules.CanAttach(_ability, new PortId(PortKind.Cooldown), effect, out _));
            GrantTag(effect); Assert.IsTrue(PortRules.CanAttach(_ability, new PortId(PortKind.Cooldown), effect, out _));
        }
        [Test]
        public void PortsRejectWrongTypeSelfReferenceAndCycle()
        {
            var a = Effect("A"); var b = Effect("B"); var animation = Animation(); var applied = new PortId(PortKind.Applied);
            Assert.IsFalse(PortRules.CanAttach(_ability, new PortId(PortKind.Cost), animation, out _));
            Assert.IsFalse(PortRules.CanAttach(a, applied, a, out _)); Assert.IsTrue(PortRules.Attach(_doc, a, applied, b));
            Assert.IsFalse(PortRules.CanAttach(b, applied, a, out _)); Assert.IsFalse(PortRules.CanAttach(a, applied, b, out _));
            Assert.IsTrue(PortRules.CanAttach(a, new PortId(PortKind.Removed), b, out _));
        }
        [TestCase("", 1f, false)]
        [TestCase("Attack", 0f, false)]
        [TestCase("Attack", -1f, false)]
        [TestCase("Attack", 1f, true)]
        public void AnimationPortValidatesClipAndLength(string name, float duration, bool accepted)
        {
            var animation = Animation(); Edit(animation, so => { so.FindProperty("clipName").stringValue = name; so.FindProperty("duration").floatValue = duration; });
            Assert.AreEqual(accepted, PortRules.CanAttach(_ability, new PortId(PortKind.Animation), animation, out _));
        }
        [Test]
        public void MarkerConnectionSurvivesArrayReorderAndUndo()
        {
            var animation = Animation(); var effect = Effect(); var track = new EventTrack();
            string first = TimelineCommands.Add(_doc, track, .2f); string second = TimelineCommands.Add(_doc, track, .7f);
            Assert.IsTrue(PortRules.Attach(_doc, animation, new PortId(PortKind.Marker, first), effect));
            EditCommand.Run(_doc, "重新排序", animation, so => so.FindProperty("markers").MoveArrayElement(0, 1), true);
            using (var so = new SerializedObject(animation)) Assert.AreEqual(effect, TimelineCommands.Find(so, "markers", first).FindPropertyRelative("effect").objectReferenceValue);
            Assert.AreEqual(first, PortRules.Links(animation).Single().Port.ElementId);
            Undo.PerformUndo(); Assert.AreEqual(first, PortRules.Links(animation).Single().Port.ElementId); Assert.AreNotEqual(first, second);
        }
        [Test]
        public void NewElementDoesNotCopyPreviousPayload()
        {
            var animation = Animation(); var effect = Effect(); string first = TimelineCommands.Add(_doc, new EventTrack(), .2f, effect);
            string second = TimelineCommands.Add(_doc, new EventTrack(), .4f);
            using var so = new SerializedObject(animation);
            Assert.IsNull(TimelineCommands.Find(so, "markers", second).FindPropertyRelative("effect").objectReferenceValue); Assert.AreNotEqual(first, second);
        }
        [Test]
        public void StableIdAssignmentIsIdempotent()
        {
            var animation = Animation(); Edit(animation, so => so.FindProperty("markers").arraySize = 2);
            Assert.IsTrue(TimelineCommands.EnsureIds(animation)); string first = Snapshot(animation);
            Assert.IsFalse(TimelineCommands.EnsureIds(animation)); Assert.AreEqual(first, Snapshot(animation));
        }
        [Test]
        public void SubassetCreationAndConnectionIsOneUndo()
        {
            int before = _doc.Assets.Count;
            var effect = SkillAssetFactory.Create(_doc, typeof(GameplayEffectAsset), "Cost", 0, _ability, new PortId(PortKind.Cost));
            Assert.AreEqual(before + 1, _doc.Assets.Count); Assert.AreEqual(effect, PortRules.Links(_ability).Single().Child);
            Undo.PerformUndo(); Assert.IsFalse(PortRules.Links(_ability).Any()); Assert.AreEqual(before, _doc.Assets.Count);
            Undo.PerformRedo(); Assert.AreEqual(before + 1, _doc.Assets.Count); Assert.AreEqual(1, PortRules.Links(_ability).Count());
        }
        [Test]
        public void DeleteEmbeddedNodeAndUndoRestoreReferences()
        {
            var effect = Effect(); PortRules.Attach(_doc, _ability, new PortId(PortKind.Cost), effect);
            SkillAssetFactory.DeleteNode(_doc, effect); Assert.IsFalse(PortRules.Links(_ability).Any());
            Undo.PerformUndo(); _doc.Save(); Assert.AreEqual(1, PortRules.Links(_ability).Count()); Assert.IsTrue(AssetDatabase.IsSubAsset(PortRules.Links(_ability).Single().Child));
        }
        [Test]
        public void ExternalNodeDeletionKeepsFile()
        {
            var external = ScriptableObject.CreateInstance<GameplayEffectAsset>(); AssetDatabase.CreateAsset(external, Folder + "/Shared.asset");
            PortRules.Attach(_doc, _ability, new PortId(PortKind.Cost), external); SkillAssetFactory.DeleteNode(_doc, external);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameplayEffectAsset>(Folder + "/Shared.asset")); Assert.IsFalse(PortRules.Links(_ability).Any());
        }
        [Test]
        public void VariantCopiesOwnedGraphAndKeepsSharedReferences()
        {
            new MeleeTemplate().Build(_ability); var sourceAnimation = _ability.Animation;
            var external = ScriptableObject.CreateInstance<GameplayEffectAsset>(); AssetDatabase.CreateAsset(external, Folder + "/Shared.asset");
            Edit(_ability, so => so.FindProperty("cost").objectReferenceValue = external); AssetDatabase.SaveAssets();
            var copy = SkillAssetFactory.Duplicate(_ability, Folder + "/Variant.asset");
            Assert.AreNotSame(sourceAnimation, copy.Animation);
            Assert.AreEqual(Folder + "/Variant.asset", AssetDatabase.GetAssetPath(copy.Animation));
            using var copied = new SerializedObject(copy); Assert.AreEqual(external, copied.FindProperty("cost").objectReferenceValue);
            Assert.AreEqual(Folder + "/Variant.asset", AssetDatabase.GetAssetPath(PortRules.Links(copy.Animation).Single().Child));
        }
        [Test]
        public void EmbedPreservesReferencesAndUndo()
        {
            var external = ScriptableObject.CreateInstance<GameplayEffectAsset>(); AssetDatabase.CreateAsset(external, Folder + "/Shared.asset");
            PortRules.Attach(_doc, _ability, new PortId(PortKind.Cost), external);
            var embedded = SkillAssetFactory.Convert(_doc, external, true); _doc.Save(); Assert.IsTrue(AssetDatabase.IsSubAsset(embedded), "Embedded: " + AssetDatabase.GetAssetPath(embedded) + " main=" + AssetDatabase.IsMainAsset(embedded) + " persistent=" + EditorUtility.IsPersistent(embedded));
            Assert.AreEqual(embedded, PortRules.Links(_ability).Single().Child); Assert.IsNotNull(external);
            Undo.PerformUndo(); Assert.AreEqual(external, PortRules.Links(_ability).Single().Child);
        }
        [Test]
        public void LayoutRoundTripsWithoutOverwritingOtherImporterData()
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(_ability)); importer.userData = "other tool data";
            var layout = new LayoutStore(_ability); layout.SetPosition(_ability, new Vector2(123, 456)); layout.Save();
            var reopened = new LayoutStore(_ability); Assert.AreEqual(new Vector2(123, 456), reopened.Position(_ability, Vector2.zero));
            Assert.That(importer.userData, Does.Contain("other tool data"));
        }
        [Test]
        public void ReferenceCandidatesExcludeOtherSkillsEmbeddedEffects()
        {
            Effect("OwnEmbedded");
            var other = ScriptableObject.CreateInstance<GameplayAbilityAsset>(); AssetDatabase.CreateAsset(other, Folder + "/Other.asset");
            SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(other, "OtherEmbedded");
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<GameplayEffectAsset>(), Folder + "/Shared.asset");
            AssetDatabase.SaveAssets(); SkillIndex.Invalidate();
            var provider = ScriptableObject.CreateInstance<SkillNodeSearchProvider>();
            try
            {
                provider.Initialize(_doc, null, null, Vector2.zero);
                var names = provider.CreateSearchTree(default).Select(e => e.content.text).ToArray();
                CollectionAssert.Contains(names, "Shared  ↗"); CollectionAssert.Contains(names, "OwnEmbedded  ↗");
                CollectionAssert.DoesNotContain(names, "OtherEmbedded  ↗");
            }
            finally { Object.DestroyImmediate(provider); }
        }
        [Test]
        public void LayoutSavesToRenamedRootAsset()
        {
            var layout = new LayoutStore(_ability);
            Assert.IsEmpty(AssetDatabase.RenameAsset(AssetDatabase.GetAssetPath(_ability), "Renamed"));
            Assert.AreEqual("Renamed", _ability.name);
            layout.SetPosition(_ability, new Vector2(7, 8)); layout.Save();
            Assert.AreEqual(new Vector2(7, 8), new LayoutStore(_ability).Position(_ability, Vector2.zero));
        }
        [Test]
        public void NestedCommandsAreOneUndoStep()
        {
            EditCommand.Transaction(_doc, "添加两个效果", _ability, () =>
            {
                SkillAssetFactory.Create(_doc, typeof(GameplayEffectAsset), "A", EffectDurationPolicy.Instant);
                SkillAssetFactory.Create(_doc, typeof(GameplayEffectAsset), "B", EffectDurationPolicy.Instant);
            });
            Assert.AreEqual(3, _doc.Assets.Count);
            Undo.PerformUndo(); Assert.AreEqual(1, _doc.Assets.Count);
            Undo.PerformRedo(); Assert.AreEqual(3, _doc.Assets.Count);
        }
        [Test]
        public void ChainDepthIncludesAncestorsOfTheEditedNode()
        {
            var chain = Enumerable.Range(0, 10).Select(i => Effect("Chain" + i)).ToArray();
            for (int i = 0; i < 8; i++) Assert.IsTrue(PortRules.Attach(_doc, chain[i], new PortId(PortKind.Applied), chain[i + 1]));
            Assert.IsFalse(PortRules.CanAttach(chain[8], new PortId(PortKind.Applied), chain[9], out _, _doc));
        }
        [Test]
        public void InspectorInsertionRepairsDuplicateIdsInSameUndoGroup()
        {
            var animation = Animation(); string original = TimelineCommands.Add(_doc, new EventTrack(), .2f);
            EditCommand.Run(_doc, "Inspector 添加事件", animation, so => so.FindProperty("markers").arraySize++);
            using (var so = new SerializedObject(animation))
            {
                var array = so.FindProperty("markers"); Assert.AreEqual(2, array.arraySize);
                Assert.AreEqual(original, array.GetArrayElementAtIndex(0).FindPropertyRelative("id").stringValue);
                Assert.AreNotEqual(original, array.GetArrayElementAtIndex(1).FindPropertyRelative("id").stringValue);
            }
            Undo.PerformUndo(); Assert.AreEqual(1, _doc.Serialized(animation).FindProperty("markers").arraySize);
            Undo.PerformRedo(); Assert.IsFalse(TimelineCommands.EnsureIds(animation));
        }
        [Test]
        public void ValidationOnlyDocumentDoesNotAssignIds()
        {
            var animation = Animation(); Edit(animation, so => so.FindProperty("markers").arraySize = 1);
            string before = Snapshot(animation);
            using (var readOnly = new SkillDocument())
            {
                readOnly.Open(_ability, false);
                using var validation = new ValidationService(readOnly); validation.ValidateNow(true);
            }
            Assert.AreEqual(before, Snapshot(animation));
        }
        [Test]
        public void ValidationDoesNotTreatAssetNamesOrUnusedCaptureIdsAsConfiguration()
        {
            var effect = Effect("Not a tag");
            var catalog = ScriptableObject.CreateInstance<AttributeCatalogAsset>();
            catalog.entries = new[] { new AttributeCatalogEntry { id = 99, displayName = "Test" } };
            AssetDatabase.CreateAsset(catalog, Folder + "/Attributes.asset"); AttributeCatalog.Invalidate();
            Edit(effect, so =>
            {
                var array = so.FindProperty("modifiers"); array.arraySize = 1;
                array.GetArrayElementAtIndex(0).FindPropertyRelative("targetAttributeId").intValue = 99;
                var magnitude = array.GetArrayElementAtIndex(0).FindPropertyRelative("magnitude");
                magnitude.FindPropertyRelative("kind").enumValueIndex = 0;
                magnitude.FindPropertyRelative("captureAttributeId").intValue = 9999;
            });
            using var validation = new ValidationService(_doc); validation.ValidateNow(true);
            Assert.IsFalse(validation.Issues.Any(i => i.Id == "GAS022" || i.Id == "GAS021"));
        }
        [Test]
        public void ExternalizeUndoRedoRetainsExternalPath()
        {
            var source = Effect(); PortRules.Attach(_doc, _ability, new PortId(PortKind.Cost), source);
            string path = Folder + "/External.asset";
            var external = SkillAssetFactory.Convert(_doc, source, false, path); _doc.Save();
            Assert.AreEqual(path, AssetDatabase.GetAssetPath(PortRules.Links(_ability).Single().Child));
            Undo.PerformUndo(); _doc.Save();
            Assert.IsTrue(SkillAssetFactory.IsEmbedded(_ability, PortRules.Links(_ability).Single().Child));
            Undo.PerformRedo(); _doc.Save();
            Assert.AreEqual(path, AssetDatabase.GetAssetPath(PortRules.Links(_ability).Single().Child));
        }
        [Test]
        public void CopyNodeClonesOwnedDescendantsAndRemapsLinks()
        {
            var parent = Effect("Parent"); var child = Effect("Child");
            PortRules.Attach(_doc, parent, new PortId(PortKind.Applied), child);
            var copied = SkillAssetFactory.CopyNode(_doc, parent); var copiedChild = PortRules.Links(copied).Single().Child;
            Assert.AreNotEqual(parent, copied); Assert.AreNotEqual(child, copiedChild);
            Assert.IsTrue(SkillAssetFactory.IsEmbedded(_ability, copiedChild));
            Assert.AreEqual(child, PortRules.Links(parent).Single().Child);
            Undo.PerformUndo(); Assert.AreEqual(3, _doc.Assets.Count);
        }
        [UnityTest]
        public IEnumerator TimelineSelectionSurvivesGraphSynchronization()
        {
            var animation = Animation(); string id = TimelineCommands.Add(_doc, new EventTrack(), .2f, Effect());
            var window = SkillEditorWindow.Open(); window.Show(_ability); yield return null; yield return null;
            window.Document.Selection.Select(animation, "markers", id);
            window.Document.Notify(new ChangeSet(animation, true)); yield return null;
            Assert.AreEqual("markers", window.Document.Selection.Track); Assert.AreEqual(id, window.Document.Selection.ElementId);
        }
        [UnityTest]
        public IEnumerator TimelinePointerDragWritesOnReleaseAndUndoesOnce()
        {
            var animation = Animation(); string id = TimelineCommands.Add(_doc, new EventTrack(), .2f);
            var window = SkillEditorWindow.Open(); window.Show(_ability); yield return null; yield return null;
            var timeline = window.rootVisualElement.Q<TimelinePanel>(); timeline.Fit(); yield return null;
            var view = timeline.Q<VisualElement>(className: "timeline-event");
            float pixelsPerSecond = timeline.Q<Label>(className: "animation-clip").layout.width / animation.Duration;
            Vector2 start = view.worldBound.position + new Vector2(8, 10), end = start + new Vector2(pixelsPerSecond * .2f, 0);
            float Time() => TimelineCommands.Find(_doc.Serialized(animation), "markers", id).FindPropertyRelative("time").floatValue;
            Undo.ClearAll();
            using (var e = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, mousePosition = start, button = 0 })) { e.target = view; view.SendEvent(e); }
            using (var e = PointerMoveEvent.GetPooled(new Event { type = EventType.MouseDrag, mousePosition = end, button = 0 })) { e.target = view; view.SendEvent(e); }
            Assert.AreEqual(.2f, Time(), .001f);
            using (var e = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, mousePosition = end, button = 0 })) { e.target = view; view.SendEvent(e); }
            Assert.AreEqual(.4f, Time(), .5f / 30);
            Undo.PerformUndo(); Assert.AreEqual(.2f, Time(), .001f);
            Undo.PerformRedo(); Assert.AreEqual(.4f, Time(), .5f / 30);
        }
        [TestCase(.8f, 30f)]
        [TestCase(10f, 60f)]
        public void FrameSnappingErrorDoesNotExceedHalfFrame(float duration, float fps)
        { for (float t = 0; t < duration; t += .00137f) Assert.LessOrEqual(Mathf.Abs(TimelineMath.Snap(t, fps) - t), .5001f / fps); }
        [Test]
        public void AllTemplatesCreateOwnedAnimations()
        {
            int index = 0;
            foreach (var template in SkillTemplateRegistry.Templates)
            {
                var ability = SkillTemplateRegistry.Create(Folder + "/Template" + index++ + ".asset", template);
                Assert.IsNotNull(ability.Animation); Assert.IsTrue(AssetDatabase.IsSubAsset(ability.Animation));
            }
            Assert.AreEqual(5, index);
        }
        [UnityTest]
        public IEnumerator WindowLoadsGraphAndRetainsNodeAcrossFieldEdit()
        {
            new MeleeTemplate().Build(_ability); var window = SkillEditorWindow.Open(); window.Show(_ability);
            yield return null; yield return null; Assert.AreEqual(5, window.NodeCount);
            var graph = window.rootVisualElement.Q<SkillGraphView>(); var before = graph.nodes.ToList();
            EditCommand.Run(window.Document, "修改时长", _ability.Animation, so => so.FindProperty("duration").floatValue = 1.5f);
            yield return null; CollectionAssert.AreEquivalent(before, graph.nodes.ToList()); window.Close();
        }
        [UnityTest]
        public IEnumerator DeletingNodeAndUnrelatedEdgeTogetherAppliesBothInOneUndo()
        {
            new MeleeTemplate().Build(_ability); var window = SkillEditorWindow.Open(); window.Show(_ability);
            yield return null; yield return null;
            var graph = window.rootVisualElement.Q<SkillGraphView>();
            var cooldown = graph.nodes.First(n => n.title == "冷却");
            var costEdge = graph.edges.First(e => e.userData is AssetLink link && link.Port.Kind == PortKind.Cost);
            var feedback = new System.Collections.Generic.List<string>(); window.Document.Feedback += feedback.Add;
            graph.DeleteElements(new GraphElement[] { cooldown, costEdge });
            using var so = new SerializedObject(_ability);
            // 写回由面板调度器在下一次更新时执行。
            for (int frame = 0; frame < 120 && so.FindProperty("cooldown").objectReferenceValue != null; frame++) { window.Repaint(); yield return null; so.Update(); }
            Assert.IsNull(so.FindProperty("cooldown").objectReferenceValue, string.Join(" | ", feedback));
            Assert.IsNull(so.FindProperty("cost").objectReferenceValue, "混选删除时连线被忽略");
            Undo.PerformUndo(); so.Update();
            Assert.IsNotNull(so.FindProperty("cooldown").objectReferenceValue); Assert.IsNotNull(so.FindProperty("cost").objectReferenceValue);
            window.Close();
        }
        [Test]
        public void AnimatorEnumeratesNestedStatesAndStateSpeed()
        {
            var clip = new AnimationClip { frameRate = 30 }; clip.SetCurve("", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, .8f, 1));
            AssetDatabase.CreateAsset(clip, Folder + "/Attack.anim");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(Folder + "/Character.controller");
            var nested = controller.layers[0].stateMachine.AddStateMachine("Combat"); var state = nested.AddState("Attack"); state.motion = clip; state.speed = 2;
            var go = new GameObject("Character"); go.AddComponent<Animator>().runtimeAnimatorController = controller;
            try { var info = new AnimatorEditorAdapter().EnumerateClips(go).Single(); Assert.AreEqual("Base Layer.Combat.Attack", info.Name); Assert.AreEqual(.4f, info.Length, .001); }
            finally { Object.DestroyImmediate(go); }
        }
        [Test]
        public void LegacyAdapterSamplesAndRestoresPose()
        {
            var clip = new AnimationClip { legacy = true, frameRate = 30 }; clip.SetCurve("", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, 1, 2));
            var go = new GameObject("Legacy"); go.AddComponent<UnityEngine.Animation>().AddClip(clip, "Attack"); go.transform.localPosition = new Vector3(3, 0, 0);
            try
            {
                var adapter = new LegacyAnimationEditorAdapter(); Assert.AreEqual(1, adapter.EnumerateClips(go).Count);
                using (var sampler = adapter.BeginPreview(go)) { sampler.Sample("Attack", .5f); Assert.AreEqual(1, go.transform.localPosition.x, .01f); }
                Assert.AreEqual(3, go.transform.localPosition.x, .01f);
            }
            finally { Object.DestroyImmediate(go); Object.DestroyImmediate(clip); }
        }
        [Test]
        public void ValidationFindsAndFixesErrorsWithoutBuildingCyclicDefinitions()
        {
            var effect = Effect(policy: EffectDurationPolicy.Duration); var child = Effect("Child");
            Edit(_ability, so => { so.FindProperty("cost").objectReferenceValue = effect; so.FindProperty("cooldown").objectReferenceValue = child; });
            Edit(effect, so => { var array = so.FindProperty("conditionalEffects"); array.arraySize = 2; foreach (int i in new[] { 0, 1 }) array.GetArrayElementAtIndex(i).FindPropertyRelative("effect").objectReferenceValue = child; });
            Edit(child, so => { var array = so.FindProperty("conditionalEffects"); array.arraySize = 1; array.GetArrayElementAtIndex(0).FindPropertyRelative("effect").objectReferenceValue = effect; });
            _doc.Notify(new ChangeSet(_ability, true));
            using var validation = new ValidationService(_doc); validation.ValidateNow(true);
            foreach (var id in new[] { "GAS001", "GAS002", "GAS003", "GAS004" }) Assert.IsTrue(validation.Issues.Any(i => i.Id == id), id);
            validation.Issues.First(i => i.Id == "GAS001").FixAction(); validation.ValidateNow(true); Assert.IsFalse(validation.Issues.Any(i => i.Id == "GAS001"));
        }
        [Test]
        public void CooldownWithOnlyUnregisteredTagsIsAnError()
        {
            // 模板自动填写的冷却标签未注册时，运行期构造能力定义会抛异常；编辑期必须报错而不只是警告。
            var cooldown = Effect("Cooldown", EffectDurationPolicy.Duration);
            Edit(cooldown, so => { var list = so.FindProperty("tags.granted"); list.arraySize = 1; list.GetArrayElementAtIndex(0).FindPropertyRelative("m_Name").stringValue = "Cooldown.NotRegistered"; });
            Edit(_ability, so => so.FindProperty("cooldown").objectReferenceValue = cooldown);
            _doc.Notify(new ChangeSet(_ability, true)); using var validation = new ValidationService(_doc); validation.ValidateNow(true);
            var issue = validation.Issues.SingleOrDefault(i => i.Id == "GAS002");
            Assert.IsNotNull(issue); Assert.AreEqual(IssueSeverity.Error, issue.Severity); StringAssert.Contains("Cooldown.NotRegistered", issue.Message);
            Assert.Throws<ArgumentException>(() => _ = _ability.Definition);
            GrantTag(cooldown); _ability.InvalidateDefinition(); _doc.Notify(new ChangeSet(cooldown)); validation.ValidateNow(true);
            Assert.IsFalse(validation.Issues.Any(i => i.Id == "GAS002")); Assert.IsNotNull(_ability.Definition);
        }
        [Test]
        public void ValidationFindsTimelineBoundsKeysCallerAndUnknownTags()
        {
            var animation = Animation(); var effect = Effect();
            TimelineCommands.Add(_doc, new EventTrack(), .2f, effect); TimelineCommands.Add(_doc, new EventTrack(), .4f);
            Edit(animation, so =>
            {
                var markers = so.FindProperty("markers"); for (int i = 0; i < 2; i++) markers.GetArrayElementAtIndex(i).FindPropertyRelative("key").stringValue = "Hit";
                markers.GetArrayElementAtIndex(1).FindPropertyRelative("time").floatValue = 2;
                var cues = so.FindProperty("cueRegions"); cues.arraySize = 1; cues.GetArrayElementAtIndex(0).FindPropertyRelative("cueTag.m_Name").stringValue = "Missing.Cue";
                so.FindProperty("clipName").stringValue = "";
            });
            Edit(effect, so => { var m = so.FindProperty("modifiers"); m.arraySize = 1; var p = m.GetArrayElementAtIndex(0).FindPropertyRelative("magnitude"); p.FindPropertyRelative("kind").enumValueIndex = 2; p.FindPropertyRelative("callerKey").stringValue = "Damage.Scale"; });
            _doc.Notify(new ChangeSet(_ability, true)); using var validation = new ValidationService(_doc); validation.ValidateNow(true);
            foreach (var id in new[] { "GAS010", "GAS011", "GAS012", "GAS020", "GAS022" }) Assert.IsTrue(validation.Issues.Any(i => i.Id == id), id);
            foreach (var issue in validation.Issues.Where(i => i.FixAction != null).ToArray()) issue.FixAction();
            validation.ValidateNow(true); foreach (var id in new[] { "GAS011", "GAS012", "GAS020" }) Assert.IsFalse(validation.Issues.Any(i => i.Id == id), id);
        }
    }
}
