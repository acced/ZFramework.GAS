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
            using (var so = new SerializedObject(animation)) Assert.AreEqual(effect, ((ApplyEffectAction)TimelineCommands.Find(so, "markers", first).FindPropertyRelative("actions").GetArrayElementAtIndex(0).managedReferenceValue).effect);
            Assert.AreSame(effect, PortRules.Links(animation).Single().Child);
            Undo.PerformUndo(); Assert.AreSame(effect, PortRules.Links(animation).Single().Child); Assert.AreNotEqual(first, second);
        }
        [Test]
        public void NewElementDoesNotCopyPreviousPayload()
        {
            var animation = Animation(); var effect = Effect(); string first = TimelineCommands.Add(_doc, new EventTrack(), .2f, effect);
            string second = TimelineCommands.Add(_doc, new EventTrack(), .4f);
            using var so = new SerializedObject(animation);
            Assert.AreEqual(0, TimelineCommands.Find(so, "markers", second).FindPropertyRelative("actions").arraySize); Assert.AreNotEqual(first, second);
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
                so.FindProperty("useDurationMagnitude").boolValue = true;
                so.FindProperty("usePeriodMagnitude").boolValue = true;
                so.FindProperty("durationMagnitude.targetAttributeId").intValue = 9999;
                so.FindProperty("periodMagnitude.targetAttributeId").intValue = 9999;
            });
            using var validation = new ValidationService(_doc); validation.ValidateNow(true);
            Assert.IsFalse(validation.Issues.Any(i => i.Id == "GAS022" || i.Id == "GAS021"));
            Edit(effect, so =>
            {
                so.FindProperty("durationMagnitude.kind").enumValueIndex = (int)MagnitudeKind.Calculation;
                so.FindProperty("durationMagnitude.captureAttributeId").intValue = 99;
            });
            validation.ValidateNow(true);
            Assert.IsTrue(validation.Issues.Any(i => i.Id == "GAS021" && i.PropertyPath == "durationMagnitude.targetAttributeId"));
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
            double layoutDeadline = EditorApplication.timeSinceStartup + 8;
            bool LaidOut() => timeline.Q<Label>(className: "animation-clip")?.layout.width > 0
                && float.IsFinite(timeline.Q<VisualElement>(className: "timeline-event").worldBound.x);
            while (!LaidOut() && EditorApplication.timeSinceStartup < layoutDeadline) { window.Repaint(); yield return null; }
            Assert.IsTrue(LaidOut(), "时间轴必须先完成真实布局再模拟指针输入。");
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

        private void SetRanges(SkillAnimationAsset animation, string track, params (string id, string tag, float start, float end)[] spans)
        {
            Edit(animation, so =>
            {
                so.FindProperty("duration").floatValue = 1;
                var array = so.FindProperty(track); array.arraySize = spans.Length;
                for (int i = 0; i < spans.Length; i++)
                {
                    var p = array.GetArrayElementAtIndex(i); var s = spans[i];
                    p.FindPropertyRelative("id").stringValue = s.id;
                    p.FindPropertyRelative("start").floatValue = s.start; p.FindPropertyRelative("end").floatValue = s.end;
                    if (track == "cueRegions") p.FindPropertyRelative("cueTag.m_Name").stringValue = s.tag;
                }
            });
            _doc.Notify(new ChangeSet(animation, true));
        }
        private static VisualElement TimelineItem(TimelinePanel timeline, string track, string id)
            => timeline.Query<VisualElement>().ToList().Single(e => e.userData as string == track + "/" + id);
        private static void Pointer(VisualElement view, EventType type, Vector2 point)
        {
            var system = new Event { type = type, mousePosition = point, button = 0 };
            if (type == EventType.MouseDown) { using var e = PointerDownEvent.GetPooled(system); e.target = view; view.SendEvent(e); }
            else if (type == EventType.MouseDrag) { using var e = PointerMoveEvent.GetPooled(system); e.target = view; view.SendEvent(e); }
            else { using var e = PointerUpEvent.GetPooled(system); e.target = view; view.SendEvent(e); }
        }
        [UnityTest] public IEnumerator LibraryDragRequiresAPressOnThatSameBoundRow()
        {
            var window=SkillEditorWindow.Open();window.Show(_ability);yield return null;
            var host=new VisualElement();var other=new Button();var row=new Button{userData=_ability};
            window.rootVisualElement.Add(host);host.Add(other);host.Add(row);int drags=0;
            LibraryPanel.InstallAssetDrag(row,asset=>{Assert.AreSame(_ability,asset);drags++;});
            yield return null;
            try{
                Vector2 p=row.worldBound.center;
                Pointer(other,EventType.MouseDown,other.worldBound.center);Pointer(row,EventType.MouseDrag,p);
                Pointer(other,EventType.MouseUp,p);Assert.AreEqual(0,drags);
                Pointer(row,EventType.MouseDown,p);Pointer(row,EventType.MouseDrag,p+Vector2.right*2);Assert.AreEqual(0,drags);
                Pointer(row,EventType.MouseDrag,p+Vector2.right*12);Pointer(row,EventType.MouseDrag,p+Vector2.right*18);
                Pointer(row,EventType.MouseUp,p);Assert.AreEqual(1,drags,"one native drag per press");
                Pointer(row,EventType.MouseDown,p);row.userData=null;Pointer(row,EventType.MouseDrag,p+Vector2.right*12);
                Pointer(row,EventType.MouseUp,p);Assert.AreEqual(1,drags,"a recycled row cannot start the previous asset's drag");
            }finally{host.RemoveFromHierarchy();window.Close();}
        }
        [UnityTest] public IEnumerator ParameterTabOnlyStartsADragOwnedByItsBoundRow(){
            var p=new SkillParameter{displayName="拖拽参数"};_ability.parameters=new[]{p};
            var window=SkillEditorWindow.Open();window.Show(_ability);yield return null;
            var panel=window.rootVisualElement.Q<LibraryPanel>();var tab=panel.Query<Button>().ToList().Single(b=>b.text=="参数");
            try{
                var point=tab.worldBound.center;Pointer(tab,EventType.MouseDown,point);Pointer(tab,EventType.MouseUp,point);yield return null;
                var real=panel.Query<Button>().ToList().Single(b=>ReferenceEquals(b.userData,p));
                var other=new Button();window.rootVisualElement.Add(other);
                var q=real.worldBound.center;Pointer(other,EventType.MouseDown,other.worldBound.center);
                Pointer(real,EventType.MouseDrag,q);Pointer(real,EventType.MouseDrag,q+Vector2.right*20);
                Pointer(other,EventType.MouseUp,q);Assert.AreNotEqual(p.Id,DragAndDrop.GetGenericData("GAS.SkillParameter"));
                var row=new Button{userData=p};window.rootVisualElement.Add(row);int starts=0;
                LibraryPanel.InstallParameterDrag(row,value=>{Assert.AreSame(p,value);starts++;});
                yield return null;q=row.worldBound.center;Pointer(row,EventType.MouseDown,q);
                Pointer(row,EventType.MouseDrag,q+Vector2.right);Assert.Zero(starts);
                Pointer(row,EventType.MouseDrag,q+Vector2.right*20);Pointer(row,EventType.MouseDrag,q+Vector2.right*30);Pointer(row,EventType.MouseUp,q);
                Assert.AreEqual(1,starts);
            }finally{window.Close();}
        }
        [UnityTest]
        public IEnumerator LibraryClicksRecoverAfterReloadAndKeepBackForwardInSync()
        {
            GameplayAbilityAsset Create(string name)
            {
                var asset = ScriptableObject.CreateInstance<GameplayAbilityAsset>();
                AssetDatabase.CreateAsset(asset, Folder + "/" + name + ".asset");
                return asset;
            }
            var second = Create("Second"); var third = Create("Third");
            SkillIndex.Invalidate();
            var window = SkillEditorWindow.Open(); window.Show(_ability);
            for (int i = 0; i < 5; i++) yield return null;
            var library = window.rootVisualElement.Q<LibraryPanel>();
            Foldout ParametersOutline() => library.Query<Foldout>().ToList().Single(f => f.text == "参数 · SetByCaller");
            var outline = ParametersOutline(); outline.value = false;
            library.Search("Third"); library.Search("");
            Assert.AreSame(outline, ParametersOutline(), "Filtering the skill library must not rebuild the current skill's outline.");
            Assert.IsFalse(outline.value);
            yield return null; // The filtered asset rows need a layout pass before pointer coordinates are valid.
            void Click(Button button)
            {
                var point = button.worldBound.center;
                Pointer(button, EventType.MouseDown, point); Pointer(button, EventType.MouseUp, point);
            }
            void Row(GameplayAbilityAsset skill) => Click(library.Query<Button>(className: "library-row").ToList().Single(b => b.userData as Object == skill));
            void Arrow(string text) => Click(window.rootVisualElement.Q(className: "main-toolbar").Query<Button>().ToList().Single(b => b.text == text));

            Row(second); Assert.AreSame(second, window.Document.Root, "Control: a normal library click opens the skill.");
            // A pre-fix window could retain its history cursor across a script reload but lose the readonly list.
            const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            ((IList)typeof(SkillEditorWindow).GetField("_history", fields).GetValue(window)).Clear();
            Row(third); Assert.AreSame(third, window.Document.Root, "A stale cursor must not abort the next click.");
            Arrow("◀"); Assert.AreSame(second, window.Document.Root);
            Arrow("▶"); Assert.AreSame(third, window.Document.Root);
            Arrow("◀"); Row(_ability); Arrow("▶");
            Assert.AreSame(_ability, window.Document.Root, "Opening from the library starts a new branch of history.");
            using var saved = new SerializedObject(window);
            var history = saved.FindProperty("_history");
            Assert.IsNotNull(history, "The list must survive a reload along with the cursor.");
            Assert.AreEqual(2, history.arraySize);
            Assert.AreSame(_ability, history.GetArrayElementAtIndex(saved.FindProperty("_historyIndex").intValue).objectReferenceValue);
        }
        [UnityTest]
        public IEnumerator TimelineGroupsCuesAndPacksOverlappingRangesWithoutChangingData()
        {
            var animation = Animation();
            SetRanges(animation, "cueRegions", ("a", "Cue.Ground.Sigil", 0, .5f), ("b", "Cue.Ground.Sigil", .2f, .4f),
                ("c", "Cue.Ground.Sigil", .5f, .8f), ("d", "Cue.Warcry.Aura", .1f, .7f));
            SetRanges(animation, "tagWindows", ("t1", "", 0, .3f), ("t2", "", .2f, .6f), ("t3", "", .6f, -1));
            TimelineCommands.Add(_doc, new EventTrack(), .2f); TimelineCommands.Add(_doc, new EventTrack(), .2f);
            string before = Snapshot(animation);
            var window = SkillEditorWindow.Open(); window.Show(_ability); yield return null; yield return null;
            var timeline = window.rootVisualElement.Q<TimelinePanel>(); timeline.Fit(); yield return null;
            var a = TimelineItem(timeline, "cueRegions", "a"); var b = TimelineItem(timeline, "cueRegions", "b");
            var c = TimelineItem(timeline, "cueRegions", "c"); var d = TimelineItem(timeline, "cueRegions", "d");
            Assert.AreSame(a.parent, b.parent); Assert.AreNotSame(a.parent, d.parent);
            Assert.AreEqual(a.style.top.value.value, c.style.top.value.value); Assert.Greater(b.style.top.value.value, a.style.top.value.value);
            Assert.AreEqual(2, timeline.Query(className: "cue-track-row").ToList().Count);
            Assert.Greater(TimelineItem(timeline, "tagWindows", "t2").style.top.value.value, TimelineItem(timeline, "tagWindows", "t1").style.top.value.value);
            Assert.AreEqual(TimelineItem(timeline, "tagWindows", "t1").style.top.value.value, TimelineItem(timeline, "tagWindows", "t3").style.top.value.value);
            Assert.AreEqual("×2", timeline.Q<Label>(className: "event-count").text);
            Assert.AreEqual(before, Snapshot(animation));
            // 折叠后仍可由结构图选中区间并自动展开。
            var heading = a.parent.parent.Q<Label>(className: "track-heading");
            Pointer(heading, EventType.MouseDown, heading.worldBound.center);
            Assert.AreEqual(DisplayStyle.None, a.style.display.value);
            window.Document.Selection.Select(animation, "cueRegions", "b"); yield return null;
            Assert.AreEqual(DisplayStyle.Flex, b.style.display.value);
            Assert.IsTrue(b.ClassListContains("selected-element"));
        }
        [UnityTest]
        public IEnumerator CueDragFreezesLanesUntilReleaseAndUndoRestoresGrouping()
        {
            var animation = Animation();
            SetRanges(animation, "cueRegions", ("a", "Cue.Test", 0, .5f), ("b", "Cue.Test", .6f, .8f));
            var window = SkillEditorWindow.Open(); window.Show(_ability); yield return null; yield return null;
            var timeline = window.rootVisualElement.Q<TimelinePanel>(); timeline.Fit(); yield return null;
            var view = TimelineItem(timeline, "cueRegions", "b"); float top = view.style.top.value.value;
            float scale = timeline.Q<Label>(className: "animation-clip").layout.width;
            Vector2 origin = view.worldBound.center, end = origin - new Vector2(.3f * scale, 0);
            Undo.ClearAll(); Pointer(view, EventType.MouseDown, origin); Pointer(view, EventType.MouseDrag, end);
            Assert.AreEqual(top, view.style.top.value.value);
            Assert.AreEqual(.6f, TimelineCommands.Find(_doc.Serialized(animation), "cueRegions", "b").FindPropertyRelative("start").floatValue, .001f);
            Pointer(view, EventType.MouseUp, end); yield return null;
            Assert.Greater(TimelineItem(timeline, "cueRegions", "b").style.top.value.value, top);
            Assert.AreEqual(.3f, TimelineCommands.Find(_doc.Serialized(animation), "cueRegions", "b").FindPropertyRelative("start").floatValue, .02f);
            Undo.PerformUndo(); yield return null;
            Assert.AreEqual(.6f, TimelineCommands.Find(_doc.Serialized(animation), "cueRegions", "b").FindPropertyRelative("start").floatValue, .001f);
            Assert.AreEqual(top, TimelineItem(timeline, "cueRegions", "b").style.top.value.value);
        }
        [Test]
        public void CueRowInsertionKeepsTagInSameUndoStep()
        {
            var animation = Animation(); Undo.ClearAll();
            string id = TimelineCommands.Add(_doc, new CueRegionTrack(), .2f, cueTag: "Cue.Ground.Sigil");
            Assert.AreEqual("Cue.Ground.Sigil", TimelineCommands.Find(_doc.Serialized(animation), "cueRegions", id).FindPropertyRelative("cueTag.m_Name").stringValue);
            Undo.PerformUndo(); Assert.AreEqual(0, _doc.Serialized(animation).FindProperty("cueRegions").arraySize);
            Undo.PerformRedo(); Assert.IsNotNull(TimelineCommands.Find(_doc.Serialized(animation), "cueRegions", id));
        }
        [Test]
        public void CueOverlapWarningIgnoresTouchingOtherTagsAndInvalidSpans()
        {
            var animation = Animation();
            SetRanges(animation, "cueRegions", ("a", "Cue.A", 0, .4f), ("b", "Cue.A", .4f, .6f),
                ("other", "Cue.B", .1f, .9f), ("overlap", "Cue.A", .5f, -1), ("empty", "Cue.A", .2f, .2f), ("invalid", "Cue.A", .8f, .2f));
            using var validation = new ValidationService(_doc); validation.ValidateNow(true);
            var issue = validation.Issues.Single(i => i.Id == "GAS015");
            Assert.AreEqual(IssueSeverity.Warning, issue.Severity); Assert.AreEqual("overlap", issue.ElementId); Assert.AreEqual("cueRegions", issue.Track);
        }
        private static bool GraphHasOverlaps(SkillGraphView graph)
        {
            var bounds = graph.nodes.Select(n => new Rect(n.GetPosition().position, n.layout.size)).ToArray();
            for (int i = 0; i < bounds.Length; i++)
                for (int j = i + 1; j < bounds.Length; j++) if (bounds[i].Overlaps(bounds[j])) return true;
            return false;
        }
        private static IEnumerator WaitForGraph(SkillEditorWindow window, Func<bool> done)
        {
            double until = EditorApplication.timeSinceStartup + 8;
            while (!done() && EditorApplication.timeSinceStartup < until) { window.Repaint(); yield return null; }
            Assert.IsTrue(done(), "结构图没有在有效尺寸计算完成后排好布局。窗口=" + window.position +
                " 根=" + window.rootVisualElement.layout + " 状态=" + window.rootVisualElement.Q<SkillGraphView>().LayoutDiagnostics + " 节点=" + string.Join("; ", window.rootVisualElement.Q<SkillGraphView>().nodes.Select(n => n.title + ":" + n.layout + "/" + n.GetPosition())));
            yield return null; yield return null;
        }
        [UnityTest]
        public IEnumerator EmbeddedCueStripSelectsStableTimelineRegionAndDoesNotCreateNodes()
        {
            var animation = Animation(); var effect = Effect();
            SetRanges(animation, "cueRegions", ("a", "Cue.Test", .1f, .5f), ("b", "Cue.Test", .6f, -1));
            Edit(effect, so => { var cues = so.FindProperty("cueTags"); cues.arraySize = 1; cues.GetArrayElementAtIndex(0).FindPropertyRelative("m_Name").stringValue = "Cue.Effect"; });
            var window = SkillEditorWindow.Open(); window.Show(_ability); yield return null; yield return null;
            var graph = window.rootVisualElement.Q<SkillGraphView>();
            yield return WaitForGraph(window, () => window.Document.Assets.All(a => window.Document.Layout.TryGetPosition(a, out _)));
            Assert.AreEqual(3, graph.NodeCount); Assert.AreEqual(3, graph.Query<Button>(className: "node-cue-strip").ToList().Count);
            var strip = graph.Query<Button>(className: "node-cue-strip").ToList().Single(b => b.userData as string == "b");
            Pointer(strip, EventType.MouseDown, strip.worldBound.center); Pointer(strip, EventType.MouseUp, strip.worldBound.center);
            Assert.AreEqual(animation, window.Document.Selection.Asset); Assert.AreEqual("cueRegions", window.Document.Selection.Track); Assert.AreEqual("b", window.Document.Selection.ElementId);
            Assert.IsTrue(strip.ClassListContains("selected-cue"));
            EditCommand.Run(window.Document, "重排 Cue", animation, so => so.FindProperty("cueRegions").MoveArrayElement(1, 0));
            Assert.AreEqual("b", window.Document.Selection.ElementId);
            Assert.AreEqual("b", graph.Q<Button>(className: "selected-cue").userData);
        }
        [UnityTest]
        public IEnumerator NewGraphNodesAvoidMeasuredBoundsAndKeepSavedPositions()
        {
            var animation = Animation(); var blocker = Effect("Blocker");
            SetRanges(animation, "cueRegions", Enumerable.Range(0, 8).Select(i => ("c" + i, "Cue.Tall" + i, .1f, .9f)).ToArray());
            _doc.Layout.SetPosition(_ability, new Vector2(40, 50));
            _doc.Layout.SetPosition(blocker, new Vector2(345, 50)); _doc.Layout.Save();
            var window = SkillEditorWindow.Open(); window.Show(_ability); yield return null;
            var graph = window.rootVisualElement.Q<SkillGraphView>();
            yield return WaitForGraph(window, () => window.Document.Layout.TryGetPosition(animation, out _) && !GraphHasOverlaps(graph));
            Assert.AreEqual(new Vector2(40, 50), window.Document.Layout.Position(_ability, Vector2.zero));
            Assert.AreEqual(new Vector2(345, 50), window.Document.Layout.Position(blocker, Vector2.zero));
            // UI Toolkit 会按系统 DPI 对屏幕布局取整；存储坐标必须精确不变。
            Assert.Less(Vector2.Distance(new Vector2(40, 50), graph.nodes.First(n => n.title == _ability.name).GetPosition().position), 1);
            Assert.Less(Vector2.Distance(new Vector2(345, 50), graph.nodes.First(n => n.title == "Blocker").GetPosition().position), 1);
            var positions = graph.nodes.ToDictionary(n => n.title, n => n.GetPosition().position);
            var added = SkillAssetFactory.Create(window.Document, typeof(GameplayEffectAsset), "New effect", EffectDurationPolicy.Instant,
                blocker, new PortId(PortKind.Applied));
            yield return WaitForGraph(window, () => window.Document.Layout.TryGetPosition(added, out _) && !GraphHasOverlaps(graph));
            foreach (var node in graph.nodes.Where(n => positions.ContainsKey(n.title))) Assert.Less(Vector2.Distance(positions[node.title], node.GetPosition().position), 1);
            positions = graph.nodes.ToDictionary(n => n.title, n => n.GetPosition().position);
            EditCommand.Run(window.Document, "改动画时长", animation, so => so.FindProperty("duration").floatValue = 1.2f);
            yield return null; yield return null;
            foreach (var node in graph.nodes) Assert.Less(Vector2.Distance(positions[node.title], node.GetPosition().position), 1);
            window.Close(); window = SkillEditorWindow.Open(); window.Show(_ability); yield return null; yield return null;
            graph = window.rootVisualElement.Q<SkillGraphView>();
            foreach (var node in graph.nodes) Assert.Less(Vector2.Distance(positions[node.title], node.GetPosition().position), 1);
        }
        [UnityTest]
        public IEnumerator AutoArrangeWaitsForTallNodesAndExplicitlyReplacesSavedLayout()
        {
            new MeleeTemplate().Build(_ability); _doc.Notify(new ChangeSet(_ability, true));
            SetRanges(_ability.Animation, "cueRegions", Enumerable.Range(0, 10).Select(i => ("c" + i, "Cue.Tall" + i, .1f, .9f)).ToArray());
            foreach (var asset in _doc.Assets) _doc.Layout.SetPosition(asset, new Vector2(40, 50)); _doc.Layout.Save();
            var window = SkillEditorWindow.Open(); window.Show(_ability);
            var graph = window.rootVisualElement.Q<SkillGraphView>(); graph.AutoArrange(); // 故意在首轮布局前调用。
            yield return WaitForGraph(window, () => graph.nodes.All(n => n.layout.height > 0) && !GraphHasOverlaps(graph));
            Assert.IsFalse(GraphHasOverlaps(graph));
            Assert.Greater(graph.nodes.First(n => n.title == _ability.Animation.name).layout.height, 300);
            Assert.Greater(graph.nodes.Select(n => n.GetPosition().x).Distinct().Count(), 1);
        }
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
        public IEnumerator InlineNodeFieldsKeepBindingUndoAndDefinitionInvalidation()
        {
            var animation = Animation(); var effect = Effect("Effect", EffectDurationPolicy.Duration);
            var window = SkillEditorWindow.Open(); window.Show(_ability); yield return null; yield return null;
            var graph = window.rootVisualElement.Q<SkillGraphView>();
            var node = graph.nodes.First(n => n.title == "Effect"); var oldDefinition = effect.Definition;
            Assert.AreEqual(0, node.inputContainer.resolvedStyle.backgroundColor.a);
            Assert.AreEqual(0, node.outputContainer.resolvedStyle.backgroundColor.a);
            Assert.IsTrue(node.titleContainer.worldBound.Overlaps(node.inputContainer.worldBound), "输入连接点应该位于标题栏");
            Undo.ClearAll(); node.Q<FloatField>("node-duration").value = 2.5f;
            yield return WaitForGraph(window, () => window.Document.Serialized(effect).FindProperty("duration").floatValue == 2.5f && !ReferenceEquals(oldDefinition, effect.Definition));
            Undo.PerformUndo(); yield return null; yield return null;
            Assert.AreEqual(1, window.Document.Serialized(effect).FindProperty("duration").floatValue);
            Assert.AreEqual(1, node.Q<FloatField>("node-duration").value);
            node.Q<EnumField>("node-durationPolicy").value = EffectDurationPolicy.Infinite;
            var animationNode = graph.nodes.First(n => n.title == animation.name);
            animationNode.Q<TextField>("node-clipName").value = "Slash";
            animationNode.Q<Toggle>("node-loop").value = true;
            yield return WaitForGraph(window, () => window.Document.Serialized(effect).FindProperty("durationPolicy").intValue == (int)EffectDurationPolicy.Infinite &&
                animation.ClipName == "Slash" && window.Document.Serialized(animation).FindProperty("loop").boolValue);
            Assert.AreEqual(0, graph.Query<UnityEditor.UIElements.PropertyField>().ToList().Count);
        }
        [UnityTest]
        public IEnumerator MiniMapRemainsInCornerAfterResizeAndCanBeHidden()
        {
            var window = SkillEditorWindow.Open(); window.Show(_ability); yield return null; yield return null;
            var graph = window.rootVisualElement.Q<SkillGraphView>(); var host = graph.Q("minimap-overlay");
            var map = graph.Q<MiniMap>(); bool previous = graph.MiniMapVisible;
            try
            {
                graph.MiniMapVisible = true;
                foreach (var width in new[] { 1080f, 1280f })
                {
                    window.position = new Rect(window.position.position, new Vector2(width, 800));
                    yield return null; yield return null; yield return null;
                    Assert.AreEqual(10, graph.worldBound.xMax - host.worldBound.xMax, 1);
                    Assert.AreEqual(10, graph.worldBound.yMax - host.worldBound.yMax, 1);
                    Assert.Less(Vector2.Distance(host.worldBound.position, map.worldBound.position), 1);
                    Assert.AreEqual(DisplayStyle.None, map.Q<Label>().resolvedStyle.display);
                }
                var toggle = window.rootVisualElement.Q<UnityEditor.UIElements.ToolbarToggle>("show-minimap");
                toggle.value = false; Assert.IsFalse(graph.MiniMapVisible);
                toggle.value = true; Assert.IsTrue(graph.MiniMapVisible);
            }
            finally { EditorPrefs.SetBool("ZFramework.GAS.ShowMiniMap", previous); }
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
        [Test] public void TimelineBranchChecksTheCallingSkillsParameterType()
        {
            var p = new SkillParameter { kind = SkillParameterKind.Float };
            _ability.parameters = new[] { p }; var animation = Animation();
            Edit(animation, so => {
                var markers = so.FindProperty("markers"); markers.arraySize = 1;
                var actions = markers.GetArrayElementAtIndex(0).FindPropertyRelative("actions"); actions.arraySize = 1;
                actions.GetArrayElementAtIndex(0).managedReferenceValue = new ConditionalAction { booleanParameterId = p.Id };
            });
            var issues = new System.Collections.Generic.List<Issue>(); var rule = new SkillConfigurationRule();
            rule.Validate(new ValidationContext(_doc), animation, issues);
            Assert.IsTrue(issues.Any(i => i.PropertyPath.EndsWith(".booleanParameterId") && i.Message.Contains("Bool")));
            p.kind = SkillParameterKind.Bool; issues.Clear(); rule.Validate(new ValidationContext(_doc), animation, issues);
            Assert.IsFalse(issues.Any(i => i.PropertyPath.EndsWith(".booleanParameterId")));
        }
        [Test]
        public void MecanimWarnsForRecastAndOnlyForAJumpThatCanOccurDuringEntryBlend()
        {
            var animation=Animation();var host=new GameObject("Mecanim preview");host.AddComponent<Animator>();host.AddComponent<SkillAnimator>();
            try{
                _doc.PreviewPrefab=host;Edit(animation,so=>so.FindProperty("blendIn").floatValue=.3f);
                var first=new AnimationSection{start=0,end=.5f,inputBufferEnd=.2f};
                var second=new AnimationSection{start=.6f,end=1,transition=new SkillBlend(.1f)};
                animation.sections=new[]{first,second};
                var issues=new System.Collections.Generic.List<Issue>();var rule=new BlendRule();
                void Check()=>rule.Validate(new ValidationContext(_doc),animation,issues);
                Check();Assert.IsTrue(issues.Any(i=>i.Message.Contains("重放同一技能")));
                Assert.IsFalse(issues.Any(i=>i.Message.Contains("输入连招")),"Early buffered input waits until the late section end.");
                first.end=.2f;issues.Clear();Check();Assert.IsTrue(issues.Any(i=>i.PropertyPath.Contains("[1]") && i.Message.Contains("输入连招")));
                second.start=.2f;issues.Clear();Check();Assert.IsFalse(issues.Any(i=>i.PropertyPath.Contains("[1]") && i.Message.Contains("输入连招")));
                second.start=.6f;second.transition=default;issues.Clear();Check();
                Assert.IsTrue(issues.Any(i=>i.PropertyPath.Contains("[1]") && i.Message.Contains("输入连招")),"zero-duration entry still cuts");
                first.start=.15f;first.end=.21f;first.inputBufferStart=first.inputBufferEnd=0;
                second.start=first.end;second.end=.30f;second.inputBufferStart=second.inputBufferEnd=0;
                var last=new AnimationSection{start=.7f,end=1};first.nextSectionId=second.Id;second.nextSectionId=last.Id;
                animation.sections=new[]{first,second,last};issues.Clear();Check();
                Assert.IsTrue(issues.Any(i=>i.PropertyPath.Contains("[2]") && i.Message.Contains("输入连招")),"adjacent sections can carry the entry blend to a later jump");
                // A later jump starts its own blend, independent of the original entry.
                second.start=.6f;
                Edit(animation,so=>so.FindProperty("blendIn").floatValue=0);
                first.inputBufferStart=first.inputBufferEnd=0;first.nextSectionId=second.Id;
                second.end=.68f;second.transition=new SkillBlend(.12f);
                var third=new AnimationSection{start=.8f,end=1};second.nextSectionId=third.Id;second.inputBufferStart=second.inputBufferEnd=0;
                animation.sections=new[]{first,second,third};issues.Clear();Check();
                Assert.IsTrue(issues.Any(i=>i.PropertyPath.Contains("[2]") && i.Message.Contains("输入连招")),"later section transitions must be inspected too");
            }finally{_doc.PreviewPrefab=null;Object.DestroyImmediate(host);}
        }
        [Test]
        public void BlendLongerThanAnimationIsReportedAndFixed()
        {
            var animation = Animation();
            Edit(animation, so => { so.FindProperty("duration").floatValue = .5f; so.FindProperty("blendIn").floatValue = .8f; });
            _doc.Notify(new ChangeSet(animation, true)); using var validation = new ValidationService(_doc); validation.ValidateNow(true);
            var issue = validation.Issues.Single(i => i.Id == "GAS014");
            Assert.AreEqual(IssueSeverity.Error, issue.Severity);
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = animation.Definition);
            issue.FixAction(); validation.ValidateNow(true);
            Assert.IsFalse(validation.Issues.Any(i => i.Id == "GAS014")); Assert.AreEqual(.5f, animation.BlendIn, 1e-6f);
            animation.InvalidateDefinition(); Assert.AreEqual(.5f, animation.Definition.BlendIn, 1e-6f);
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
