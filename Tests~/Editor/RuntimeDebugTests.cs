using System;
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;
using ZFramework.GAS.Samples.BasicCombat;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class RuntimeDebugTests
    {
        private const string Folder = "Assets/GASRuntimeDebugTests";
        private GameplayAbilityAsset _ability;
        private SkillAnimationAsset _animation;
        private GameplayEffectAsset _effect, _cooldown;
        private AbilitySystemComponent _owner;
        private GameplayAbilitySpec _spec;
        private SkillEditorWindow _window;
        private sealed class Driver : IAnimationDriver
        {
            public float Play(string clip, float duration, bool loop) => duration > 0 ? duration : 4;
            public void Advance(float time) { }
            public void Stop() { }
        }
        private static void Edit(ScriptableObject asset, Action<SerializedObject> apply)
        { using var so = new SerializedObject(asset); apply(so); so.ApplyModifiedPropertiesWithoutUndo(); }
        private static T Asset<T>(string name) where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>(); asset.name = name;
            AssetDatabase.CreateAsset(asset, Folder + "/" + name + ".asset"); return asset;
        }
        [SetUp]
        public void SetUp()
        {
            TestTags.Initialize(); RuntimeDebugSession.Clear();
            AssetDatabase.CreateFolder("Assets", "GASRuntimeDebugTests");
            _ability = Asset<GameplayAbilityAsset>("观察技能"); _animation = Asset<SkillAnimationAsset>("观察动画");
            _effect = Asset<GameplayEffectAsset>("观察增益"); _cooldown = Asset<GameplayEffectAsset>("观察冷却");
            Edit(_animation, so => { so.FindProperty("clipName").stringValue = "Attack"; so.FindProperty("duration").floatValue = 4; });
            Edit(_cooldown, so =>
            {
                so.FindProperty("durationPolicy").enumValueIndex = (int)EffectDurationPolicy.Duration;
                so.FindProperty("duration").floatValue = 5;
                var tags = so.FindProperty("tags.granted"); tags.arraySize = 1;
                tags.GetArrayElementAtIndex(0).FindPropertyRelative("m_Name").stringValue = "Test.Cooldown";
            });
            Edit(_effect, so =>
            {
                so.FindProperty("durationPolicy").enumValueIndex = (int)EffectDurationPolicy.Duration;
                so.FindProperty("duration").floatValue = 10;
                var modifiers = so.FindProperty("modifiers"); modifiers.arraySize = 1;
                var modifier = modifiers.GetArrayElementAtIndex(0);
                modifier.FindPropertyRelative("targetAttributeId").intValue = 1;
                modifier.FindPropertyRelative("magnitude.constant").floatValue = 25;
            });
            Edit(_animation, so =>
            {
                var marker = so.FindProperty("markers"); marker.arraySize = 1;
                var entry = marker.GetArrayElementAtIndex(0); entry.FindPropertyRelative("id").stringValue = "stable-marker";
                entry.FindPropertyRelative("time").floatValue = 3;
                var actions = entry.FindPropertyRelative("actions"); actions.arraySize = 1;
                actions.GetArrayElementAtIndex(0).managedReferenceValue = new ApplyEffectAction { effect = _effect, targets = new TargetQuery { kind = TargetQueryKind.Self } };
            });
            Edit(_ability, so =>
            {
                so.FindProperty("animation").objectReferenceValue = _animation;
                so.FindProperty("cooldown").objectReferenceValue = _cooldown;
                var blocked = so.FindProperty("activationBlockedTags"); blocked.arraySize = 1;
                blocked.GetArrayElementAtIndex(0).FindPropertyRelative("m_Name").stringValue = "Test.Other";
            });
            _owner = new AbilitySystemComponent(); _owner.Attributes.Add(new AttributeId(1), 100);
            _spec = _owner.GrantAbility(_ability.Definition, 3);
            Assert.AreEqual(AbilityActivationResult.Activated, _owner.TryActivateAbility(_spec));
            _owner.StartTask(_spec, new AnimationTask(_animation.Definition, new Driver(), durationOverride: 2));
            _owner.Effects.Apply(new GameplayEffectSpec(_effect.Definition, _owner.Attributes, _owner));
            _owner.OwnedTags.AddTag("Test.Window"); _owner.OwnedTags.AddTag("Test.Window");
            _owner.Tick(.5f);
        }
        [TearDown]
        public void TearDown()
        {
            _window?.Close(); _window = null; _owner?.Clear();
            RuntimeDebugSession.Clear(); RuntimeDebugSession.FollowSelection = true;
            Selection.activeObject = null;
            AssetDatabase.DeleteAsset(Folder);
        }
        private RuntimeSnapshot Capture(RuntimeSnapshot previous = null, double now = 0)
            => RuntimeSnapshot.Capture(_owner, source => ReferenceEquals(source, _owner) ? "骑士" : "其他单位", previous, now);

        [Test]
        public void CaptureReadsAllFiveSectionsWithoutMutatingRuntime()
        {
            var snapshot = Capture();
            var ability = snapshot.Abilities.Single();
            Assert.AreEqual("观察技能", ability.Name); Assert.IsTrue(ability.Active); Assert.IsTrue(ability.OnCooldown);
            Assert.IsTrue(ability.TagGatePassed); Assert.AreEqual(3, ability.Level);
            Assert.AreEqual(4.5f, ability.CooldownRemaining); Assert.AreEqual(5, ability.CooldownDuration);
            var task = snapshot.Tasks.Single();
            Assert.AreSame(_animation, task.Asset); Assert.AreSame(_ability, task.AbilityAsset);
            Assert.AreEqual(2, task.Duration); Assert.AreEqual(.25f, task.NormalizedTime); Assert.AreEqual(1, task.AuthoredTime); Assert.IsFalse(task.Loop);
            var tag = snapshot.Tags.Single(t => t.Value.Name == "Test.Window");
            Assert.AreEqual(2, tag.Count); Assert.IsTrue(tag.IsNew);
            var attribute = snapshot.Attributes.Single();
            Assert.AreEqual(AttributeCatalog.Name(1), attribute.Name);
            Assert.AreEqual(100, attribute.BaseValue); Assert.AreEqual(125, attribute.CurrentValue); Assert.AreEqual(25, attribute.Delta);
            var effect = snapshot.Effects.Single(e => e.Asset == _effect);
            Assert.AreEqual("观察增益", effect.Name); Assert.AreEqual(EffectDurationPolicy.Duration, effect.Policy);
            Assert.AreEqual(9.5f, effect.Remaining); Assert.AreEqual(10, effect.Duration); Assert.AreEqual(0, effect.Period);
            Assert.AreEqual(1, effect.Stacks); Assert.AreEqual("骑士", effect.SourceName);
            _owner.Tick(.25f);
            Assert.AreEqual(4.5f, ability.CooldownRemaining, "Old snapshot must retain sampled values");
            Assert.AreEqual(1, task.AuthoredTime);
            Assert.AreEqual(1, _owner.ActiveTaskCount);
        }
        [Test]
        public void TagsTrackAdditionRemovalAndOwnerChanges()
        {
            var first = Capture(now: 2); var second = Capture(first, 2.1);
            Assert.IsFalse(second.Tags.Single(t => t.Value.Name == "Test.Window").IsNew);
            Assert.AreEqual(2, second.Tags.Single(t => t.Value.Name == "Test.Window").AddedAt);
            _owner.OwnedTags.RemoveTag("Test.Window"); _owner.OwnedTags.RemoveTag("Test.Window");
            var absent = Capture(second, 2.2); Assert.IsFalse(absent.Tags.Any(t => t.Value.Name == "Test.Window"));
            _owner.OwnedTags.AddTag("Test.Window"); _owner.OwnedTags.AddTag("Test.Other");
            var added = Capture(absent, 2.3);
            Assert.IsTrue(added.Tags.Single(t => t.Value.Name == "Test.Window").IsNew);
            Assert.IsFalse(added.Abilities.Single().TagGatePassed);
            var other = new AbilitySystemComponent(); other.OwnedTags.AddTag("Test.Window");
            Assert.IsTrue(RuntimeSnapshot.Capture(other, null, added).Tags.Single().IsNew);
        }
        [Test]
        public void OldDefinitionsKeepAssetSourcesAfterInvalidation()
        {
            object a = _ability.Definition, e = _effect.Definition, animation = _animation.Definition;
            _ability.InvalidateDefinition(); _effect.InvalidateDefinition(); _animation.InvalidateDefinition();
            Assert.AreNotSame(a, _ability.Definition); Assert.AreNotSame(e, _effect.Definition); Assert.AreNotSame(animation, _animation.Definition);
            Assert.AreSame(_ability, DefinitionSource.Of(a)); Assert.AreSame(_effect, DefinitionSource.Of(e)); Assert.AreSame(_animation, DefinitionSource.Of(animation));
            Assert.AreEqual("观察技能", Capture().Abilities.Single().Name);
            Assert.IsNull(DefinitionSource.Of(new object())); Assert.IsNull(DefinitionSource.Of(null));
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference RegisterTemporary(ScriptableObject asset)
        { var key = new object(); DefinitionSource.Register(key, asset); return new WeakReference(key); }
        [Test]
        public void SourceRegistryDoesNotKeepDefinitionsAlive()
        {
            var reference = RegisterTemporary(_ability);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.IsFalse(reference.IsAlive);
        }
        [Test]
        public void EffectReadbackUsesResolvedDurationAndPeriod()
        {
            var source = new AttributeSet(); source.Add(new AttributeId(91), 7); source.Add(new AttributeId(92), .75f);
            var definition = new GameplayEffectDefinition(EffectDurationPolicy.Duration, duration: 99, period: 9,
                durationAttribute: new AttributeId(91), periodAttribute: new AttributeId(92));
            _owner.Effects.Apply(new GameplayEffectSpec(definition, source, "外部来源"), out var handle);
            _owner.Tick(.1f);
            source.Get(new AttributeId(91)).BaseValue = 2;
            var effect = Capture().Effects.Single(e => e.Handle == handle);
            Assert.AreEqual(7, effect.Duration); Assert.AreEqual(6.9f, effect.Remaining, .0001f); Assert.AreEqual(.75f, effect.Period);
        }
        [Test]
        public void EditorFollowsOldTaskAndHighlightsEffectsWithoutDirtyOrUndo()
        {
            _window = SkillEditorWindow.Open(); _window.Show(_ability);
            // Opening/focusing invalidates caches; the active spec and task retain their old definitions.
            _window.Document.Notify(new ChangeSet(_ability, true));
            AssetDatabase.SaveAssets(); Undo.ClearAll();
            string before = EditorJsonUtility.ToJson(_animation); int undo = Undo.GetCurrentGroup();
            _window.SetFollowRuntime(true); RuntimeDebugSession.Publish(Capture());
            Assert.IsTrue(_window.Document.Playback.Live); Assert.IsFalse(_window.Document.Playback.Playing);
            Assert.AreEqual(1, _window.Document.Playback.Time);
            Assert.GreaterOrEqual(_window.rootVisualElement.Query(className: "live-active").ToList().Count, 3);
            Assert.IsNotNull(_window.rootVisualElement.Q(className: "live-playhead"));
            Assert.IsFalse(EditorUtility.IsDirty(_animation)); Assert.IsFalse(EditorUtility.IsDirty(_ability)); Assert.IsFalse(EditorUtility.IsDirty(_effect));
            Assert.AreEqual(before, EditorJsonUtility.ToJson(_animation)); Assert.AreEqual(undo, Undo.GetCurrentGroup());
            _window.Document.Playback.Tick(1, 4); Assert.AreEqual(1, _window.Document.Playback.Time);
            _window.SetFollowRuntime(false);
            Assert.IsFalse(_window.Document.Playback.Live); Assert.IsEmpty(_window.rootVisualElement.Query(className: "live-active").ToList());
            _owner.Tick(.2f); RuntimeDebugSession.Publish(Capture()); Assert.AreEqual(1, _window.Document.Playback.Time);
            _window.SetFollowRuntime(true); Assert.AreEqual(1.4f, _window.Document.Playback.Time, .0001f);
            RuntimeDebugSession.Clear(); Assert.IsFalse(_window.Document.Playback.Live);
            Assert.IsEmpty(_window.rootVisualElement.Query(className: "live-active").ToList());
        }
        [Test]
        public void SelectionResolvesProviderOnParentAndMissingProviderIsSafe()
        {
            var parent = new GameObject("单位"); var child = new GameObject("挂点"); child.transform.SetParent(parent.transform);
            try
            {
                var provider = parent.AddComponent<CombatUnit>();
                Assert.AreSame(provider, RuntimeDebugSession.ProviderInParents(child));
                Object.DestroyImmediate(provider);
                Assert.IsNull(RuntimeDebugSession.ProviderInParents(child));
                Assert.IsNull(RuntimeDebugSession.ProviderInParents(null));
            }
            finally { Object.DestroyImmediate(parent); }
        }
        [TestCase(false)]
        [TestCase(true)]
        public void FollowingWithoutMatchingTaskKeepsLocalPreviewPlaying(bool anotherSkillRunning)
        {
            var localAbility = _ability;
            if (anotherSkillRunning)
            {
                localAbility = Asset<GameplayAbilityAsset>("本地预览技能");
                var animation = Asset<SkillAnimationAsset>("本地预览动画");
                Edit(animation, so => { so.FindProperty("clipName").stringValue = "Preview"; so.FindProperty("duration").floatValue = 4; });
                Edit(localAbility, so => so.FindProperty("animation").objectReferenceValue = animation);
            }
            else _owner.EndAbility(_spec);
            _window = SkillEditorWindow.Open(); _window.Show(localAbility);
            _window.SetFollowRuntime(true); RuntimeDebugSession.Publish(Capture());
            _window.rootVisualElement.Q<TimelinePanel>().TogglePlay();
            for (int i = 0; i < 5; i++)
            {
                RuntimeDebugSession.Publish(Capture());
                _window.Document.Playback.Tick(.1f, 4);
            }
            Assert.IsFalse(_window.Document.Playback.Live);
            Assert.IsTrue(_window.Document.Playback.Playing);
            Assert.AreEqual(.5f, _window.Document.Playback.Time, .0001f);

            // A matching task takes over local playback; ending it returns control to local preview.
            var observer = new AbilitySystemComponent();
            var spec = observer.GrantAbility(new GameplayAbilityDefinition()); observer.TryActivateAbility(spec);
            observer.StartTask(spec, new AnimationTask(localAbility.Animation().Definition, new Driver()));
            observer.Tick(.75f); RuntimeDebugSession.Publish(RuntimeSnapshot.Capture(observer, null));
            Assert.IsTrue(_window.Document.Playback.Live);
            Assert.IsFalse(_window.Document.Playback.Playing);
            Assert.AreEqual(.75f, _window.Document.Playback.Time, .0001f);
            observer.EndAbility(spec); RuntimeDebugSession.Publish(RuntimeSnapshot.Capture(observer, null));
            _window.rootVisualElement.Q<TimelinePanel>().TogglePlay();
            RuntimeDebugSession.Publish(RuntimeSnapshot.Capture(observer, null));
            _window.Document.Playback.Tick(.1f, 4);
            Assert.IsFalse(_window.Document.Playback.Live);
            Assert.IsTrue(_window.Document.Playback.Playing);
            Assert.AreEqual(.85f, _window.Document.Playback.Time, .0001f);
        }
        [Test]
        public void LoopingTaskSnapshotUsesCurrentCycleAuthoredTime()
        {
            _owner.EndAbility(_spec);
            var definition = new SkillAnimationDefinition("Loop", 4, loop: true);
            var spec = _owner.GrantAbility(new GameplayAbilityDefinition()); _owner.TryActivateAbility(spec);
            _owner.StartTask(spec, new AnimationTask(definition, new Driver(), durationOverride: 2));
            _owner.Tick(2.5f);
            var task = Capture().Tasks.Single(); Assert.IsTrue(task.Loop); Assert.AreEqual(1, task.AuthoredTime);
        }
    }
}
