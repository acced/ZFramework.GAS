using System;
using System.Collections.Generic;
using GameplayTags;
using NUnit.Framework;
using UnityEngine;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class AnimationTimelineRuntimeTests
    {
        private GameplayTag _window, _other, _cue;
        private AnimationClip _clip;
        private sealed class Driver : IAnimationDriver
        {
            public int Plays, Stops;
            public float BlendIn, BlendOut;
            public void Play(AnimationClip clip, float blendIn) { Plays++; BlendIn = blendIn; }
            public void Advance(float time) { }
            public void Stop(float blendOut) { Stops++; BlendOut = blendOut; }
            public void Dispose() { }
        }
        [Serializable]
        private sealed class CountingAction : SkillAction
        {
            public int Count;
            public bool Cancel;
            public override SkillActionDefinition Build(SkillActionBuildContext builder) => Define("Count", context => { Count++; if (Cancel) context.Source.EndAbility(context.Ability); });
        }
        private sealed class CancellingAudio : ISkillAudioService, IDisposable
        {
            public Action Cancel;
            public int Disposals;
            public IDisposable Play(AudioClip clip, Vector3 position, float volume, bool spatial, bool loop, UnityEngine.SceneManagement.Scene scene)
            { Cancel(); return this; }
            public void Dispose() => Disposals++;
        }
        [SetUp]
        public void SetUp()
        {
            TestTags.Initialize();
            _window = GameplayTagManager.RequestTag("Test.Window"); _other = GameplayTagManager.RequestTag("Test.Other"); _cue = GameplayTagManager.RequestTag("Test.Cue");
            _clip = new AnimationClip();
        }
        [TearDown] public void TearDown() => Object.DestroyImmediate(_clip);
        private static SkillExecutionContext Activate(AbilitySystemComponent owner)
        {
            var ability = owner.GrantAbility(new GameplayAbilityDefinition());
            Assert.IsTrue(owner.TryActivateAbility(ability).Succeeded);
            return new SkillExecutionContext(null, ability, default, new EffectEvaluationContext(1), null);
        }
        [Test]
        public void LargeTickObservesWindowStateAtEachMarker()
        {
            var owner = new AbilitySystemComponent(); var execution = Activate(owner); var observed = new List<bool>();
            var definition = new SkillAnimationDefinition(null, 1, markers: new[] { new AnimationMarker(.1f), new AnimationMarker(.3f), new AnimationMarker(.7f) },
                tagWindows: new[] { new AnimationTagWindow(.2f, .5f, new GameplayTagSet(_window)) });
            var task = new AnimationTask(definition, null, execution, (_, __) => observed.Add(owner.OwnedTags.HasTag(_window)));
            owner.StartTask(execution.Ability, task); owner.Tick(1);
            CollectionAssert.AreEqual(new[] { false, true, false }, observed); Assert.IsFalse(owner.OwnedTags.HasTag(_window));
        }
        [Test]
        public void CancellationDuringFirstTagAdditionDoesNotLeakRemainingTags()
        {
            var owner = new AbilitySystemComponent(); var execution = Activate(owner);
            owner.OwnedTags.RegisterTagEventCallback(_window, GameplayTagEventType.NewOrRemoved, (_, count) => { if (count > 0) owner.EndAbility(execution.Ability); });
            var definition = new SkillAnimationDefinition(null, 1, tagWindows: new[] { new AnimationTagWindow(0, -1, new GameplayTagSet(_window, _other)) });
            var task = new AnimationTask(definition, null, execution); owner.StartTask(execution.Ability, task); owner.Tick(.1f);
            Assert.IsFalse(task.IsActive);
            Assert.IsFalse(owner.OwnedTags.HasTag(_window)); Assert.IsFalse(owner.OwnedTags.HasTag(_other));
        }
        [Test]
        public void OverlappingWindowsAndExternalSourcesKeepCountsIndependent()
        {
            var owner = new AbilitySystemComponent(); var execution = Activate(owner); owner.OwnedTags.AddTag(_window);
            var definition = new SkillAnimationDefinition(null, 1, tagWindows: new[] { new AnimationTagWindow(0, .2f, new GameplayTagSet(_window)), new AnimationTagWindow(.1f, .5f, new GameplayTagSet(_window)) });
            owner.StartTask(execution.Ability, new AnimationTask(definition, null, execution)); owner.Tick(.3f); Assert.IsTrue(owner.OwnedTags.HasTag(_window));
            owner.EndAbility(execution.Ability); Assert.IsTrue(owner.OwnedTags.HasTag(_window));
            owner.OwnedTags.RemoveTag(_window); Assert.IsFalse(owner.OwnedTags.HasTag(_window));
        }
        [Test]
        public void ZeroLengthWindowOpensBeforeSameTimeEventAndClosesAfter()
        {
            var owner = new AbilitySystemComponent(); var execution = Activate(owner); bool during = false;
            var definition = new SkillAnimationDefinition(null, 1, markers: new[] { new AnimationMarker(.2f) },
                tagWindows: new[] { new AnimationTagWindow(.2f, .2f, new GameplayTagSet(_window)) });
            owner.StartTask(execution.Ability, new AnimationTask(definition, null, execution, (_, __) => during = owner.OwnedTags.HasTag(_window)));
            owner.Tick(.3f); Assert.IsTrue(during); Assert.IsFalse(owner.OwnedTags.HasTag(_window));
        }
        [Test]
        public void LoopingTaskEventsFireOncePerCrossedCycle()
        {
            var owner = new AbilitySystemComponent(); var execution = Activate(owner); var action = new CountingAction();
            var definition = new SkillAnimationDefinition(null, 1, true,
                tagWindows: new[] { new AnimationTagWindow(.2f, .5f, new GameplayTagSet(_window)) },
                markers: new[] { new AnimationMarker(.3f, actions: new[] { action.Build(new()) }) });
            owner.StartTask(execution.Ability, new AnimationTask(definition, null, execution));
            owner.Tick(3.6f); Assert.AreEqual(4, action.Count); Assert.IsFalse(owner.OwnedTags.HasTag(_window)); owner.EndAbility(execution.Ability);
        }
        [TestCase(.8f)]
        [TestCase(.6f)]
        public void LoopClosesEndSpansAndFiresEndMarkerEveryCycle(float duration)
        {
            var router = new GameplayCueRouter(); int opened = 0, closed = 0, ends = 0;
            router.Register(_cue, p => { if (p.Event == GameplayCueEvent.OnActive) opened++; else if (p.Event == GameplayCueEvent.Removed) closed++; });
            var owner = new AbilitySystemComponent(cueRouter: router); var execution = Activate(owner);
            var definition = new SkillAnimationDefinition(null, duration, true,
                markers: new[] { new AnimationMarker(duration, "End") },
                cueRegions: new[] { new AnimationCueRegion(duration * .25f, -1, _cue) },
                tagWindows: new[] { new AnimationTagWindow(duration * .25f, -1, new GameplayTagSet(_window)) });
            owner.StartTask(execution.Ability, new AnimationTask(definition, null, execution, (_, __) => ends++));
            for (int cycle = 1; cycle <= 5; cycle++)
            {
                owner.Tick(duration);
                Assert.AreEqual(cycle, ends); Assert.AreEqual(opened, closed); Assert.IsFalse(owner.OwnedTags.HasTag(_window));
            }
            owner.Tick(duration * .5f); Assert.IsTrue(owner.OwnedTags.HasTag(_window));
            owner.EndAbility(execution.Ability);
            Assert.IsFalse(owner.OwnedTags.HasTag(_window)); Assert.AreEqual(opened, closed);
        }
        [Test]
        public void ActionCancellationStopsLaterEvents()
        {
            var owner = new AbilitySystemComponent(); var execution = Activate(owner); var first = new CountingAction { Cancel = true }; var second = new CountingAction();
            var definition = new SkillAnimationDefinition(null, 1, markers: new[] { new AnimationMarker(.2f, actions: new[] { first.Build(new()) }), new AnimationMarker(.3f, actions: new[] { second.Build(new()) }) });
            owner.StartTask(execution.Ability, new AnimationTask(definition, null, execution)); owner.Tick(1);
            Assert.AreEqual(1, first.Count); Assert.AreEqual(0, second.Count);
        }
        [Test]
        public void WarmTimelineTraversalWithWindowsAndTaskDispatchAllocatesZero()
        {
            var owner = new AbilitySystemComponent(); var execution = Activate(owner); var action = new CountingAction();
            var definition = new SkillAnimationDefinition(null, 1, true,
                tagWindows: new[] { new AnimationTagWindow(.2f, .6f, new GameplayTagSet(_window)) },
                markers: new[] { new AnimationMarker(.3f, actions: new[] { action.Build(new()) }) });
            owner.StartTask(execution.Ability, new AnimationTask(definition, null, execution));
            for (int i = 0; i < 100; i++) owner.Tick(1);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) owner.Tick(1);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, bytes); Assert.AreEqual(1100, action.Count); owner.EndAbility(execution.Ability);
        }
        [TestCase(false)]
        [TestCase(true)]
        public void FinishAndCancelUseTheSameDriverFadePath(bool cancel)
        {
            var owner = new AbilitySystemComponent(); var execution = Activate(owner); var driver = new Driver();
            var definition = new SkillAnimationDefinition(_clip, 1, blendIn: .1f, blendOut: .25f);
            owner.StartTask(execution.Ability, new AnimationTask(definition, driver, execution));
            Assert.AreEqual(.1f, driver.BlendIn);
            if (cancel) owner.EndAbility(execution.Ability); else owner.Tick(1.1f);
            Assert.AreEqual(1, driver.Stops); Assert.AreEqual(.25f, driver.BlendOut);
        }
        [Test]
        public void AudioServiceCancellationDisposesTheHandleReturnedAfterCancellation()
        {
            var owner = new AbilitySystemComponent(); var execution = Activate(owner);
            var service = new CancellingAudio { Cancel = () => owner.EndAbility(execution.Ability) };
            execution.Services.Set<ISkillAudioService>(service);
            var clip = AudioClip.Create("Cancellation", 100, 1, 1000, false);
            try
            {
                var track = new AudioSkillTrack { clip = clip, end = 1 }.Build();
                var definition = new SkillAnimationDefinition(null, 1, runtimeTracks: new[] { track });
                owner.StartTask(execution.Ability, new AnimationTask(definition, null, execution));
                owner.Tick(.1f);
                Assert.IsFalse(execution.Ability.IsActive); Assert.AreEqual(1, service.Disposals);
            }
            finally { Object.DestroyImmediate(clip); }
        }
        [TestCase(false)]
        [TestCase(true)]
        public void EarlierCastStartedHandlerCannotAttachAStaleTimeline(bool recast)
        {
            var animation = ScriptableObject.CreateInstance<SkillAnimationAsset>();
            var asset = ScriptableObject.CreateInstance<GameplayAbilityAsset>(); asset.Presentation = animation;
            SkillTimelineAuthoring.Edit(animation, t => t.Clip(_clip, 1));
            var owner = new AbilitySystemComponent(); var runner = new SkillRunner(owner); var driver = new Driver();
            var spec = runner.Grant(asset); bool entered = false;
            runner.CastStarted += execution =>
            {
                if (entered) return;
                entered = true; owner.EndAbility(execution.Ability);
                if (recast) Assert.IsTrue(runner.TryCast(asset).Succeeded);
            };
            var player = new SkillTimelinePlayer(runner, driver);
            try
            {
                Assert.IsTrue(runner.TryCast(asset).Succeeded);
                Assert.AreEqual(recast ? 1 : 0, driver.Plays);
                Assert.AreEqual(recast, spec.IsActive);
                owner.Tick(.5f);
                Assert.AreEqual(recast, spec.IsActive);
            }
            finally { player.Dispose(); Object.DestroyImmediate(asset); Object.DestroyImmediate(animation); }
        }
    }
}
