using System;
using System.Collections.Generic;
using NUnit.Framework;
using GameplayTags;
using UnityEditor;
using UnityEditor.Animations;
using System.Reflection;
using UnityEngine.Playables;
using UnityEngine.Animations;
using UnityEngine;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class SkillMovementProbe : MonoBehaviour, ISkillMovementAdapter
    { public int Calls; public void ApplyRootMotion(Vector3 position, Quaternion rotation) => Calls++; }
    public sealed class SkillSectionTests
    {
        private readonly List<Object> _assets = new();
        private sealed class Driver : ISeekableAnimationDriver
        {
            public float Time;
            public float Play(string name, float duration, bool loop) => duration > 0 ? duration : 2;
            public void Advance(float time) => Time = time;
            public void Seek(float time) => Time = time;
            public void Stop() { }
        }
        [SetUp] public void Setup() => TestTags.Initialize();
        [Test] public void RoundedJumpDestinationOpensWindowAndCueExactlyOnce()
        {
            const float start = .6854840517044067f, destination = .6854841113090515f, duration = 1.7f, actual = .31f;
            Assert.Less(start, destination); Assert.AreEqual(start * (actual / duration), destination * (actual / duration));
            int opens = 0, closes = 0; object openedBy = null, closedBy = null; var router = new GameplayCueRouter();
            router.Register("Test.Cue", p => { if (p.Event == GameplayCueEvent.OnActive) { opens++; openedBy = p.Source; } if (p.Event == GameplayCueEvent.Removed) { closes++; closedBy = p.Source; } });
            var owner = new AbilitySystemComponent(cueRouter: router); var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec);
            var definition = new SkillAnimationDefinition("a", duration,
                tagWindows: new[] { new AnimationTagWindow(start, -1, new GameplayTagSet("Test.Window")) },
                cueRegions: new[] { new AnimationCueRegion(start, -1, "Test.Cue") },
                sections: new[] { new AnimationSectionDefinition("a", "a", 0, .1f, null, 0, .1f), new AnimationSectionDefinition("b", "b", destination, duration, null, destination, duration) });
            var task = new AnimationTask(definition, new Driver(), durationOverride: actual); owner.StartTask(spec, task);
            task.JumpToSection("b"); owner.Tick(0);
            Assert.AreEqual(1, opens); Assert.AreEqual(1, owner.OwnedTags.GetTagCount("Test.Window")); owner.Clear();
            Assert.AreEqual(1, closes); Assert.AreEqual(0, owner.OwnedTags.GetTagCount("Test.Window"));
            Assert.AreSame(openedBy, closedBy); Assert.IsNull(((AbilitySourceIdentity)closedBy).Owner);
        }
        [TestCase(false, false), TestCase(true, false), TestCase(false, true), TestCase(true, true)]
        public void JumpRestorationCallbackCannotSeekOrSampleTheAbandonedDestination(bool jumpAgain, bool fromTag)
        {
            var router = new GameplayCueRouter(); var owner = new AbilitySystemComponent(cueRouter: router);
            var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec);
            AnimationTask task = null;
            void Reenter() { if (jumpAgain) task.JumpToSection("c"); else owner.EndAbility(spec); }
            if (fromTag) owner.OwnedTags.RegisterTagEventCallback("Test.Window", GameplayTagEventType.NewOrRemoved, (_, count) => { if (count > 0) Reenter(); });
            else router.Register("Test.Cue", p => { if (p.Event == GameplayCueEvent.OnActive) Reenter(); });
            var definition = new SkillAnimationDefinition("a", 1,
                tagWindows: new[] { new AnimationTagWindow(.2f, .7f, new GameplayTagSet("Test.Window")) },
                cueRegions: new[] { new AnimationCueRegion(.2f, .7f, "Test.Cue") },
                runtimeTracks: new[] { new CurveSkillTrack { start = .2f, end = .7f, channelId = "abandoned" }.Build() },
                sections: new[] { new AnimationSectionDefinition("a", "a", 0, .1f, null, 0, .1f), new AnimationSectionDefinition("b", "b", .5f, .7f, null, .5f, .7f), new AnimationSectionDefinition("c", "c", .8f, 1, null, .8f, 1) });
            var context = new SkillExecutionContext(null, spec, null, spec.EvaluationContext, null); var driver = new Driver();
            task = new AnimationTask(definition, driver, durationOverride: 1, taskContext: context.Services); owner.StartTask(spec, task);
            Assert.IsFalse(task.JumpToSection("b"));
            Assert.AreEqual(jumpAgain ? .8f : 0, driver.Time); Assert.IsFalse(context.Channels.ContainsKey("abandoned"));
            Assert.AreEqual(0, owner.OwnedTags.GetTagCount("Test.Window"));
            if (jumpAgain) Assert.AreEqual("c", task.SectionId); else Assert.IsFalse(task.IsActive);
            owner.EndAbility(spec);
        }
        private T New<T>() where T : ScriptableObject { var asset = ScriptableObject.CreateInstance<T>(); _assets.Add(asset); return asset; }
        [Test] public void SectionJumpClosesWindowsSkipsMarkersAndUsesStableIds()
        {
            var first = new AnimationSection { start = 0, end = 1, inputBufferStart = .1f, inputBufferEnd = .5f };
            var next = new AnimationSection { start = 1, end = 2, inputBufferStart = 1, inputBufferEnd = 2 };
            var asset = New<SkillAnimationAsset>(); asset.sections = new[] { first, next };
            using (var so = new SerializedObject(asset))
            {
                so.FindProperty("clipName").stringValue = "Test"; so.FindProperty("duration").floatValue = 2;
                var windows = so.FindProperty("tagWindows"); windows.arraySize = 1; var window = windows.GetArrayElementAtIndex(0);
                window.FindPropertyRelative("start").floatValue = 0; window.FindPropertyRelative("end").floatValue = .8f;
                window.FindPropertyRelative("tags").arraySize = 1; window.FindPropertyRelative("tags").GetArrayElementAtIndex(0).FindPropertyRelative("m_Name").stringValue = "Test.Window";
                var markers = so.FindProperty("markers"); markers.arraySize = 2;
                markers.GetArrayElementAtIndex(0).FindPropertyRelative("time").floatValue = .5f;
                markers.GetArrayElementAtIndex(0).FindPropertyRelative("key").stringValue = "skip";
                markers.GetArrayElementAtIndex(1).FindPropertyRelative("time").floatValue = 1.2f;
                markers.GetArrayElementAtIndex(1).FindPropertyRelative("key").stringValue = "hit";
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            var owner = new AbilitySystemComponent(); var ability = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(ability);
            var driver = new Driver(); var markersSeen = new List<string>();
            var task = new AnimationTask(asset.Definition, driver, onMarker: (_, marker) => markersSeen.Add(marker.Key)); owner.StartTask(ability, task);
            owner.Tick(.25f); Assert.IsTrue(owner.OwnedTags.HasTag("Test.Window"));
            next.displayName = "重命名下一段";
            Assert.IsTrue(task.QueueSection(next.Id)); Assert.IsTrue(task.JumpToSection(next.Id));
            Assert.IsFalse(owner.OwnedTags.HasTag("Test.Window")); Assert.AreEqual(.5f, driver.Time);
            owner.Tick(.3f); CollectionAssert.AreEqual(new[] { "hit" }, markersSeen);
            Assert.IsFalse(task.QueueSection("removed-id")); owner.EndAbility(ability);
        }
        [TestCase(.8f, .44f, .6f), TestCase(1f, 1f, .5f)]
        public void LoopingSectionDoesNotExecuteTheNextSectionsSharedBoundary(float authored, float actual, float split)
        {
            int hits = 0, opens = 0; var router = new GameplayCueRouter();
            router.Register("Test.Cue", p => { if (p.Event == GameplayCueEvent.OnActive) opens++; });
            var owner = new AbilitySystemComponent(cueRouter: router); var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec);
            var animation = new SkillAnimationDefinition("a", authored, markers: new[] { new AnimationMarker(split) },
                cueRegions: new[] { new AnimationCueRegion(split, -1, "Test.Cue") },
                runtimeTracks: new[] { new CurveSkillTrack { start = split, end = authored, channelId = "next", curve = AnimationCurve.Constant(0, 1, 2) }.Build() },
                sections: new[] { new AnimationSectionDefinition("a", "a", 0, split, "a", 0, split), new AnimationSectionDefinition("b", "b", split, authored, null, split, authored) });
            var context = new SkillExecutionContext(null, spec, null, new EffectEvaluationContext(1), null);
            var task = new AnimationTask(animation, new Driver(), durationOverride: actual, onMarker: (_, _) => hits++, taskContext: context.Services);
            owner.StartTask(spec, task); owner.Tick(split * actual / authored * 3.5f);
            Assert.AreEqual(0, hits); Assert.AreEqual(0, opens); Assert.IsFalse(context.Channels.ContainsKey("next"));
            Assert.IsTrue(task.QueueSection("b")); owner.Tick(split * (actual / authored) * .5f + .0001f);
            Assert.AreEqual(1, hits); Assert.AreEqual(1, opens); Assert.IsTrue(context.Channels.ContainsKey("next")); owner.EndAbility(spec);
        }
        [Test] public void EndingSectionDoesNotEnterATrackOrMarkerAtItsExclusiveEnd()
        {
            var owner = new AbilitySystemComponent(); var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec); int hits = 0;
            var definition = new SkillAnimationDefinition("a", 1, markers: new[] { new AnimationMarker(.5f) },
                sections: new[] { new AnimationSectionDefinition("a", "a", 0, .5f, null, 0, .5f) });
            var task = new AnimationTask(definition, new Driver(), durationOverride: 1, onMarker: (_, _) => hits++);
            owner.StartTask(spec, task); owner.Tick(.5f); Assert.AreEqual(0, hits); Assert.IsFalse(task.IsActive);
        }
        [Test] public void CurveChannelsArePerCastAndCancelledWithoutChangingAttributes()
        {
            var animation = New<SkillAnimationAsset>();
            animation.runtimeTracks = new SkillRuntimeTrack[] { new CurveSkillTrack { start = 0, end = 2, channelId = "speed-id", curve = AnimationCurve.Linear(0, 1, 1, 3) } };
            using (var so = new SerializedObject(animation)) { so.FindProperty("clipName").stringValue = "Test"; so.FindProperty("duration").floatValue = 2; so.ApplyModifiedPropertiesWithoutUndo(); }
            var asset = New<GameplayAbilityAsset>(); using (var so = new SerializedObject(asset)) { so.FindProperty("animation").objectReferenceValue = animation; so.ApplyModifiedPropertiesWithoutUndo(); }
            var a = new AbilitySystemComponent(); var b = new AbilitySystemComponent(); a.Attributes.Add(new AttributeId(1), 10);
            var one = new SkillRunner(a); var two = new SkillRunner(b);
            new SkillTimelinePlayer(one, () => new Driver()); new SkillTimelinePlayer(two, () => new Driver());
            one.Grant(asset);
            one.TryCast(asset);two.Grant(asset);  two.TryCast(asset); a.Tick(1); b.Tick(.5f);
            Assert.AreEqual(2, one.GetExecution(asset).Channels["speed-id"], .001f);
            Assert.AreEqual(1.5f, two.GetExecution(asset).Channels["speed-id"], .001f);
            one.Cancel(asset); Assert.AreEqual(0, one.GetExecution(asset).Channels.Count); Assert.AreEqual(10, a.Attributes.GetCurrent(new AttributeId(1)));
            b.Tick(2); Assert.AreEqual(0, two.GetExecution(asset).Channels.Count); Assert.IsFalse(two.IsActive(asset));
        }
        [Test] public void MissingSectionReferenceIsRejectedDuringBuild()
        {
            var definition = new AnimationSectionDefinition("a", "a", 0, 1, "removed", 0, 1);
            Assert.Throws<ArgumentException>(() => new SkillAnimationDefinition("test", 1, sections: new[] { definition }));
        }
        [Test] public void PlayableDriverOwnsGraphWithoutChangingAnimatorSpeed()
        {
            var go = new GameObject("playable"); _assets.Add(go); var animator = go.AddComponent<Animator>(); animator.speed = .6f;
            var clip = new AnimationClip(); _assets.Add(clip); clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
            using (var driver = new PlayableAnimationDriver(animator, new[] { new PlayableClipBinding { name = "hit", clip = clip } }))
            {
                Assert.AreEqual(.25f, driver.Play("hit", .25f, false, .1f)); Assert.AreEqual(.6f, animator.speed);
                driver.Seek(.5f); driver.Stop(.1f); Assert.AreEqual(.6f, animator.speed);
            }
        }
        [TestCase(.8f, .44f, .6f), TestCase(1f, 1f, .5f), TestCase(1.7f, .31f, .9f)]
        public void AdjacentSectionsProcessSharedBoundaryOnceWithoutRestartingWindows(float authored, float actual, float split)
        {
            int opened = 0, closed = 0, hits = 0; var router = new GameplayCueRouter();
            router.Register("Test.Cue", p => { if (p.Event == GameplayCueEvent.OnActive) opened++; if (p.Event == GameplayCueEvent.Removed) closed++; });
            var owner = new AbilitySystemComponent(cueRouter: router); var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec);
            var definition = new SkillAnimationDefinition("Test", authored,
                markers: new[] { new AnimationMarker(split) }, cueRegions: new[] { new AnimationCueRegion(0, -1, "Test.Cue") },
                tagWindows: new[] { new AnimationTagWindow(0, -1, new GameplayTagSet(new GameplayTag[] { "Test.Window" })) },
                sections: new[] { new AnimationSectionDefinition("a", "a", 0, split, "b", 0, split), new AnimationSectionDefinition("b", "b", split, authored, null, split, authored) });
            var task = new AnimationTask(definition, new Driver(), durationOverride: actual, onMarker: (_, _) => hits++); owner.StartTask(spec, task);
            owner.Tick(split * (actual / authored)); owner.Tick(0);
            Assert.AreEqual(1, hits); Assert.AreEqual(1, opened); Assert.AreEqual(0, closed); Assert.AreEqual(1, owner.OwnedTags.GetTagCount("Test.Window"));
            owner.Tick(actual); Assert.AreEqual(1, hits); Assert.AreEqual(1, closed); Assert.AreEqual(0, owner.OwnedTags.GetTagCount("Test.Window"));
        }
        [TestCase(.8f, .44f, .6f), TestCase(1f, 1f, .5f)]
        public void ExplicitJumpIncludesDestinationBoundaryWithoutRepeatingConsumedMarker(float authored, float actual, float split)
        {
            var owner = new AbilitySystemComponent(); var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec); int hits = 0;
            var definition = new SkillAnimationDefinition("Test", authored, markers: new[] { new AnimationMarker(split) },
                sections: new[] { new AnimationSectionDefinition("a", "a", 0, split, null, 0, split), new AnimationSectionDefinition("b", "b", split, authored, null, split, authored) });
            var task = new AnimationTask(definition, new Driver(), durationOverride: actual, onMarker: (t, _) => { hits++; t.JumpToSection("b"); });
            owner.StartTask(spec, task); task.JumpToSection("b"); owner.Tick(0); owner.Tick(.01f); Assert.AreEqual(1, hits); owner.EndAbility(spec);
        }
        private static T DriverField<T>(PlayableAnimationDriver driver, string name)
            => (T)typeof(PlayableAnimationDriver).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(driver);
        [Test] public void ReplayedOpenBoundaryIsIdempotentAndReleasesEveryOwnedTag()
        {
            int opens = 0, closes = 0; var router = new GameplayCueRouter();
            router.Register("Test.Cue", cue => { if (cue.Event == GameplayCueEvent.OnActive) opens++; if (cue.Event == GameplayCueEvent.Removed) closes++; });
            var owner = new AbilitySystemComponent(cueRouter: router); var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec);
            var definition = new SkillAnimationDefinition("a", 1,
                tagWindows: new[] { new AnimationTagWindow(0, .8f, new GameplayTagSet(new GameplayTag[] { "Test.Window", "Test.Other" })) },
                cueRegions: new[] { new AnimationCueRegion(0, .8f, "Test.Cue") });
            var task = new AnimationTask(definition, new Driver(), durationOverride: 1); owner.StartTask(spec, task); owner.Tick(.1f);
            // Force the defensive case: an already restored/open boundary is visited again.
            typeof(AnimationTask).GetField("_nextBoundary", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(task, 0);
            owner.Tick(0);
            Assert.AreEqual(1, opens); Assert.AreEqual(1, owner.OwnedTags.GetTagCount("Test.Window")); Assert.AreEqual(1, owner.OwnedTags.GetTagCount("Test.Other"));
            owner.EndAbility(spec);
            Assert.AreEqual(1, closes); Assert.AreEqual(0, owner.OwnedTags.GetTagCount("Test.Window")); Assert.AreEqual(0, owner.OwnedTags.GetTagCount("Test.Other"));
        }
        [TestCase(false), TestCase(true)] public void DisposingDisabledAnimatorRemovesOnlySkillNodes(bool inactiveObject)
        {
            var go = new GameObject("disabled-dispose"); _assets.Add(go); var animator = go.AddComponent<Animator>();
            var controller = new AnimatorController(); _assets.Add(controller); controller.AddLayer("Base");
            foreach (var layer in controller.layers) _assets.Add(layer.stateMachine);
            var clip = new AnimationClip(); _assets.Add(clip); clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Constant(0, 1, 0));
            controller.layers[0].stateMachine.AddState("Idle").motion = clip; animator.runtimeAnimatorController = controller; animator.Update(0);
            var native = animator.playableGraph; int nodes = native.GetPlayableCount(), outputs = native.GetOutputCount();
            go.AddComponent<SkillMovementProbe>();
            using var driver = new PlayableAnimationDriver(animator, new[] { new PlayableClipBinding { name = "a", clip = clip } }, rootMotion: true);
            driver.Play("a", 1, false); Assert.Greater(native.GetPlayableCount(), nodes);
            var skillOutput = DriverField<AnimationPlayableOutput>(driver, "_output");
            var skillLayer = DriverField<AnimationLayerMixerPlayable>(driver, "_layers");
            var clipNode = DriverField<AnimationClipPlayable>(driver, "_current");
            if (inactiveObject) go.SetActive(false); else animator.enabled = false;
            driver.Dispose();
            Assert.IsFalse(driver.IsValid);
            Assert.IsTrue(native.IsValid(), "Disposing the skill driver must preserve the Animator's graph.");
            Assert.IsFalse(skillOutput.IsOutputValid()); Assert.IsFalse(skillLayer.IsValid()); Assert.IsFalse(clipNode.IsValid());
            Assert.AreEqual(outputs, native.GetOutputCount());
            Assert.IsNull(go.GetComponent<SkillRootMotionRelay>());
            UnityEngine.TestTools.LogAssert.NoUnexpectedReceived();
        }
        [Test] public void JumpClosingRegionDoesNotDispatchRemovedAgainAtDestination()
        {
            int closed = 0; var router = new GameplayCueRouter(); router.Register("Test.Cue", p => { if (p.Event == GameplayCueEvent.Removed) closed++; });
            var owner = new AbilitySystemComponent(cueRouter: router); var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec);
            var definition = new SkillAnimationDefinition("a", 1, cueRegions: new[] { new AnimationCueRegion(0, .5f, "Test.Cue") },
                sections: new[] { new AnimationSectionDefinition("a", "a", 0, .5f, null, 0, .5f), new AnimationSectionDefinition("b", "b", .5f, 1, null, .5f, 1) });
            var task = new AnimationTask(definition, new Driver(), durationOverride: 1); owner.StartTask(spec, task); owner.Tick(.1f); task.JumpToSection("b"); owner.Tick(.5f);
            Assert.AreEqual(1, closed);
        }
        [Test] public void DirectAnimationTaskReceivesLevelParametersAndSourceIdentity()
        {
            var owner = new AbilitySystemComponent(); owner.Attributes.Add(new AttributeId(1), 100);
            var spec = owner.GrantAbility(new GameplayAbilityDefinition(), 3);
            var values = new EffectEvaluationContext(3, new SkillParameterValues(new Dictionary<string, SkillParameterValue> { ["damage"] = new SkillParameterValue(7f) }));
            owner.TryActivateAbility(spec, values);
            var effect = new GameplayEffectDefinition(EffectDurationPolicy.Duration, 1,
                modifiers: new[] { new EffectModifier(new AttributeId(1), ModifierOperation.Add, EffectMagnitude.FromParameter("damage", -1)) });
            var task = new AnimationTask(new SkillAnimationDefinition("a", 1, markers: new[] { new AnimationMarker(.1f, actions: new[] { ApplyEffectAction.FromDefinition("hit", effect, new TargetQuery { kind = TargetQueryKind.Self }.Build()) }) }), new Driver(), durationOverride: 1);
            owner.StartTask(spec, task); owner.Tick(.2f); Assert.AreEqual(93, owner.Attributes.GetCurrent(new AttributeId(1))); Assert.AreSame(owner.SourceIdentity, owner.Effects[0].Source); owner.Clear();
        }
        [Test] public void TaskStartedAfterUpgradeStillUsesTheActivationsLevelOne()
        {
            var owner = new AbilitySystemComponent(); owner.Attributes.Add(new AttributeId(1), 100);
            var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec); spec.Level = 3;
            var damage = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[] {
                new EffectModifier(new AttributeId(1), ModifierOperation.Add, EffectMagnitude.ByLevel(-1, -2, -3)) });
            var task = new AnimationTask(new SkillAnimationDefinition("a", 1, markers: new[] {
                new AnimationMarker(.1f, actions: new[] { ApplyEffectAction.FromDefinition("hit", damage, new TargetQuery { kind = TargetQueryKind.Self }.Build()) }) }), new Driver(), durationOverride: 1);
            owner.StartTask(spec, task); owner.Tick(.2f); Assert.AreEqual(99, owner.Attributes.GetCurrent(new AttributeId(1))); owner.EndAbility(spec);
        }
        [Test] public void DirectTasksStartedAtDifferentTimesUseTheSameActivationSourceCapture()
        {
            var owner = new AbilitySystemComponent(); var health = new AttributeId(1); var attack = new AttributeId(2);
            owner.Attributes.Add(health, 100); var stat = owner.Attributes.Add(attack, 10);
            var spec = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(spec, new EffectEvaluationContext(spec.Level, source: owner.Attributes));
            var damage = new GameplayEffectDefinition(EffectDurationPolicy.Instant, modifiers: new[] {
                new EffectModifier(health, ModifierOperation.Add, EffectMagnitude.FromAttribute(attack, -1, MagnitudeCapture.Source)) });
            var animation = new SkillAnimationDefinition("a", .2f, markers: new[] { new AnimationMarker(.1f, actions: new[] { ApplyEffectAction.FromDefinition("hit", damage, new TargetQuery { kind = TargetQueryKind.Self }.Build()) }) });
            foreach (float changed in new[] { 20f, 30f })
            { stat.BaseValue = changed; owner.StartTask(spec, new AnimationTask(animation, new Driver(), durationOverride: .2f)); owner.Tick(.2f); }
            Assert.AreEqual(80, owner.Attributes.GetCurrent(health)); owner.EndAbility(spec);
        }
        private static void UpdateDriver(PlayableAnimationDriver driver, float dt)
            => typeof(PlayableAnimationDriver).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(driver, new object[] { dt });
        [Test] public void PlayablePreservesInterruptedMixtureAndRestoresRootMotionAtStop()
        {
            var go = new GameObject("playable-hardening"); _assets.Add(go); var animator = go.AddComponent<Animator>(); animator.applyRootMotion = false;
            var movement = go.AddComponent<SkillMovementProbe>();
            AnimationClip Clip(string name, float value) { var c = new AnimationClip { name = name }; c.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Constant(0, 1, value)); _assets.Add(c); return c; }
            using var driver = new PlayableAnimationDriver(animator, new[] { new PlayableClipBinding { name = "a", clip = Clip("a", 10) },
                new PlayableClipBinding { name = "b", clip = Clip("b", 20) }, new PlayableClipBinding { name = "c", clip = Clip("c", 30) } }, rootMotion: true);
            driver.Play("a", 1, false, 0); driver.Play("b", 1, false, 1); UpdateDriver(driver, .25f);
            var skills = DriverField<AnimationMixerPlayable>(driver, "_skills"); float before = skills.GetInputWeight(0) * 10 + skills.GetInputWeight(1) * 20;
            float layer = DriverField<float>(driver, "_layerWeight"); driver.Play("c", 1, false, 1);
            Assert.AreEqual(3, skills.GetInputCount()); Assert.AreEqual(before, skills.GetInputWeight(0) * 10 + skills.GetInputWeight(1) * 20 + skills.GetInputWeight(2) * 30, .0001f);
            Assert.AreEqual(layer, DriverField<float>(driver, "_layerWeight"));
            driver.Seek(.5f); // Real callback suppression is verified by the PlayMode seek test.
            var onMove = typeof(SkillRootMotionRelay).GetMethod("OnAnimatorMove", BindingFlags.Instance | BindingFlags.NonPublic);
            int applied = movement.Calls; onMove.Invoke(go.GetComponent<SkillRootMotionRelay>(), null);
            Assert.Greater(movement.Calls, applied, "Positive control immediately before Stop."); applied = movement.Calls;
            driver.Stop(.2f); Assert.IsFalse(animator.applyRootMotion); UpdateDriver(driver, .1f);
            onMove.Invoke(go.GetComponent<SkillRootMotionRelay>(), null);
            Assert.AreEqual(applied, movement.Calls);
            float stopping = DriverField<float>(driver, "_layerWeight"); driver.Play("a", 1, false, 1); Assert.AreEqual(stopping, DriverField<float>(driver, "_layerWeight"));
            driver.Stop(); animator.applyRootMotion = true;
            driver.Play("a", 1, false); driver.Stop(); Assert.IsTrue(animator.applyRootMotion, "Each play interval restores the project's latest setting.");
            animator.applyRootMotion = false; driver.Play("a", 1, false); driver.Play("b", 1, false); driver.Stop(); Assert.IsFalse(animator.applyRootMotion);
        }
        [Test] public void DisposedOrReplacedAnimatorDriverIsRecreated()
        {
            var go = new GameObject("recreate-driver"); _assets.Add(go); var animator = go.AddComponent<Animator>();
            var clip = new AnimationClip(); _assets.Add(clip); clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0, 0, 1, 1));
            var skillAnimator = go.AddComponent<SkillAnimator>(); skillAnimator.SetBackend(new PlayableAnimationBackend { clips = new[] { new PlayableClipBinding { name = "a", clip = clip } } });
            try
            {
                var old = skillAnimator.Driver; ((IDisposable)old).Dispose(); Assert.AreNotSame(old, skillAnimator.Driver);
                Object.DestroyImmediate(animator); go.AddComponent<Animator>(); Assert.DoesNotThrow(() => skillAnimator.Driver.Play("a", 1, false));
            }
            finally { skillAnimator.SetBackend(null); } // EditMode does not run this component's Play lifecycle.
        }
        [TestCase(false), TestCase(true)] public void AnimatorParametersTriggersAndExistingStateReachPlayableController(bool rootMotion)
        {
            const string folder = "Assets/GASPlayableRegression"; AssetDatabase.CreateFolder("Assets", "GASPlayableRegression");
            try
            {
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Controller.controller");
                controller.AddParameter("Speed", AnimatorControllerParameterType.Float); controller.AddParameter("Fire", AnimatorControllerParameterType.Trigger);
                var clip = new AnimationClip(); clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Constant(0, 1, 0)); AssetDatabase.CreateAsset(clip, folder + "/Clip.anim");
                var idle = controller.layers[0].stateMachine.AddState("Idle"); idle.motion = clip;
                var moving = controller.layers[0].stateMachine.AddState("Moving"); moving.motion = clip;
                var hit = controller.layers[0].stateMachine.AddState("Hit"); hit.motion = clip;
                var transition = moving.AddTransition(hit); transition.hasExitTime = false; transition.duration = 0; transition.AddCondition(AnimatorConditionMode.If, 0, "Fire");
                var go = new GameObject("controller-probe"); _assets.Add(go); var animator = go.AddComponent<Animator>(); animator.runtimeAnimatorController = controller;
                if (rootMotion) go.AddComponent<SkillMovementProbe>();
                animator.Update(0); animator.Play("Base Layer.Moving", 0, .4f); animator.SetFloat("Speed", .7f); animator.Update(0);
                var playable = (AnimatorControllerPlayable)animator.playableGraph.GetOutput(0).GetSourcePlayable();
                using var driver = new PlayableAnimationDriver(animator, new[] { new PlayableClipBinding { name = "a", clip = clip } }, rootMotion: rootMotion);
                var graph = DriverField<PlayableGraph>(driver, "_graph"); graph.Evaluate(0);
                Assert.AreEqual(.7f, playable.GetFloat("Speed"), .001f); Assert.IsTrue(playable.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Moving"));
                Assert.AreEqual(.4f, playable.GetCurrentAnimatorStateInfo(0).normalizedTime, .01f);
                animator.SetFloat("Speed", .9f); animator.SetTrigger("Fire"); animator.Update(.01f); animator.Update(.01f);
                Assert.AreEqual(.9f, playable.GetFloat("Speed"), .001f, "Animator.SetFloat must control the actual graph controller");
                Assert.IsTrue(playable.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Hit"), "Animator.SetTrigger must drive transitions");
                driver.Play("a", 1, false, .1f); graph.Evaluate(0); driver.Stop(.1f); graph.Evaluate(0);
                Assert.IsTrue(playable.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Hit"), "Root motion ownership changes must not reset the base controller");
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
        [Test] public void PlayableHandoffPreservesPendingTriggersAndRestoresControllerState()
        {
            const string folder = "Assets/GASControllerHandoff"; AssetDatabase.CreateFolder("Assets", "GASControllerHandoff");
            try
            {
                var controller = AnimatorController.CreateAnimatorControllerAtPath(folder + "/Controller.controller");
                controller.AddParameter("Speed", AnimatorControllerParameterType.Float); controller.AddParameter("Fire", AnimatorControllerParameterType.Trigger);
                var clip = new AnimationClip(); clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Constant(0, 1, 0)); AssetDatabase.CreateAsset(clip, folder + "/Clip.anim");
                var idle = controller.layers[0].stateMachine.AddState("Idle"); idle.motion = clip;
                var hit = controller.layers[0].stateMachine.AddState("Hit"); hit.motion = clip;
                var transition = idle.AddTransition(hit); transition.hasExitTime = false; transition.duration = 0; transition.AddCondition(AnimatorConditionMode.If, 0, "Fire");
                var go = new GameObject("handoff"); _assets.Add(go); var animator = go.AddComponent<Animator>(); animator.runtimeAnimatorController = controller; animator.Update(0);
                animator.SetTrigger("Fire"); // Pending before the graph exists; it has not been consumed.
                var playable = (AnimatorControllerPlayable)animator.playableGraph.GetOutput(0).GetSourcePlayable();
                using var driver = new PlayableAnimationDriver(animator, new[] { new PlayableClipBinding { name = "a", clip = clip } });
                animator.Update(.01f); animator.Update(.01f);
                Assert.IsTrue(playable.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Hit"));
                animator.SetFloat("Speed", .8f); playable.Play("Base Layer.Hit", 0, .4f); animator.Update(0);
                driver.Dispose(); animator.Update(0);
                Assert.AreEqual(.8f, animator.GetFloat("Speed"), .001f);
                Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Hit"));
                Assert.AreEqual(.4f, animator.GetCurrentAnimatorStateInfo(0).normalizedTime, .02f);
                animator.Play("Base Layer.Idle", 0, 0); animator.Update(0);
                using var second = new PlayableAnimationDriver(animator, new[] { new PlayableClipBinding { name = "a", clip = clip } });
                animator.SetTrigger("Fire"); second.Dispose(); animator.Update(.01f); animator.Update(.01f);
                Assert.IsTrue(animator.GetCurrentAnimatorStateInfo(0).IsName("Base Layer.Hit"), "An unconsumed trigger must also survive disposing the graph.");
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
