using System;
using System.Collections.Generic;
using GameplayTags;
using NUnit.Framework;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class AnimationTimelineRuntimeTests
    {
        private GameplayTag _window, _other, _cue;
        private sealed class Driver : IAnimationDriver
        {
            public int Stops;
            public float Play(string name, float duration, bool loop) => duration > 0 ? duration : 1;
            public void Advance(float time) { }
            public void Stop() => Stops++;
        }
        [Serializable]
        private sealed class CountingSpawner : TaskSpawner
        {
            public int Count;
            public bool Cancel;
            public override void Spawn(GameplayAbilitySpec ability, TimelineTaskContext context)
            { Count++; if (Cancel) ability.Owner.EndAbility(ability); }
        }
        [SetUp]
        public void SetUp()
        {
            TestTags.Initialize();
            _window = GameplayTagManager.RequestTag("Test.Window"); _other = GameplayTagManager.RequestTag("Test.Other"); _cue = GameplayTagManager.RequestTag("Test.Cue");
        }
        private static GameplayAbilitySpec Activate(AbilitySystemComponent owner)
        {
            var ability = owner.GrantAbility(new GameplayAbilityDefinition());
            Assert.AreEqual(AbilityActivationResult.Activated, owner.TryActivateAbility(ability)); return ability;
        }
        [Test]
        public void LargeTickObservesWindowStateAtEachMarker()
        {
            var owner = new AbilitySystemComponent(); var ability = Activate(owner); var observed = new List<bool>();
            var definition = new SkillAnimationDefinition("Attack", 1, markers: new[] { new AnimationMarker(.1f), new AnimationMarker(.3f), new AnimationMarker(.7f) },
                tagWindows: new[] { new AnimationTagWindow(.2f, .5f, new GameplayTagSet(_window)) });
            var task = new AnimationTask(definition, new Driver(), onMarker: (_, __) => observed.Add(owner.OwnedTags.HasTag(_window)));
            owner.StartTask(ability, task); owner.Tick(1);
            CollectionAssert.AreEqual(new[] { false, true, false }, observed); Assert.IsFalse(owner.OwnedTags.HasTag(_window));
        }
        [Test]
        public void CancellationDuringFirstTagAdditionDoesNotLeakRemainingTags()
        {
            var owner = new AbilitySystemComponent(); var ability = Activate(owner); var driver = new Driver();
            owner.OwnedTags.RegisterTagEventCallback(_window, GameplayTagEventType.NewOrRemoved, (_, count) => { if (count > 0) owner.EndAbility(ability); });
            var definition = new SkillAnimationDefinition("Attack", 1, tagWindows: new[] { new AnimationTagWindow(0, -1, new GameplayTagSet(_window, _other)) });
            var task = new AnimationTask(definition, driver); owner.StartTask(ability, task); owner.Tick(.1f);
            Assert.IsFalse(task.IsActive); Assert.AreEqual(1, driver.Stops);
            Assert.IsFalse(owner.OwnedTags.HasTag(_window)); Assert.IsFalse(owner.OwnedTags.HasTag(_other));
        }
        [Test]
        public void OverlappingWindowsAndExternalSourcesKeepCountsIndependent()
        {
            var owner = new AbilitySystemComponent(); var ability = Activate(owner); owner.OwnedTags.AddTag(_window);
            var definition = new SkillAnimationDefinition("Attack", 1, tagWindows: new[] { new AnimationTagWindow(0, .2f, new GameplayTagSet(_window)), new AnimationTagWindow(.1f, .5f, new GameplayTagSet(_window)) });
            owner.StartTask(ability, new AnimationTask(definition, new Driver())); owner.Tick(.3f); Assert.IsTrue(owner.OwnedTags.HasTag(_window));
            owner.EndAbility(ability); Assert.IsTrue(owner.OwnedTags.HasTag(_window));
            owner.OwnedTags.RemoveTag(_window); Assert.IsFalse(owner.OwnedTags.HasTag(_window));
        }
        [Test]
        public void ZeroLengthWindowOpensBeforeSameTimeEventAndClosesAfter()
        {
            var owner = new AbilitySystemComponent(); var ability = Activate(owner); bool during = false;
            var definition = new SkillAnimationDefinition("Attack", 1, markers: new[] { new AnimationMarker(.2f) },
                tagWindows: new[] { new AnimationTagWindow(.2f, .2f, new GameplayTagSet(_window)) });
            owner.StartTask(ability, new AnimationTask(definition, new Driver(), onMarker: (_, __) => during = owner.OwnedTags.HasTag(_window)));
            owner.Tick(.3f); Assert.IsTrue(during); Assert.IsFalse(owner.OwnedTags.HasTag(_window));
        }
        [Test]
        public void ScaledDurationMovesAllTrackBoundariesTogether()
        {
            var owner = new AbilitySystemComponent(); var ability = Activate(owner); int fired = 0;
            var definition = new SkillAnimationDefinition("Attack", 1, markers: new[] { new AnimationMarker(.4f) },
                tagWindows: new[] { new AnimationTagWindow(.2f, .6f, new GameplayTagSet(_window)) });
            owner.StartTask(ability, new AnimationTask(definition, new Driver(), durationOverride: .5f, onMarker: (_, __) => fired++));
            owner.Tick(.11f); Assert.IsTrue(owner.OwnedTags.HasTag(_window)); Assert.AreEqual(0, fired);
            owner.Tick(.1f); Assert.AreEqual(1, fired); owner.Tick(.1f); Assert.IsFalse(owner.OwnedTags.HasTag(_window));
        }
        [Test]
        public void LoopingTaskEventsFireOncePerCrossedCycle()
        {
            var owner = new AbilitySystemComponent(); var ability = Activate(owner); var spawner = new CountingSpawner();
            var definition = new SkillAnimationDefinition("Attack", 1, true,
                tagWindows: new[] { new AnimationTagWindow(.2f, .5f, new GameplayTagSet(_window)) },
                taskEvents: new[] { new AnimationTaskEvent(.3f, spawner) });
            owner.StartTask(ability, new AnimationTask(definition, new Driver(), taskContext: new TimelineTaskContext()));
            owner.Tick(3.6f); Assert.AreEqual(4, spawner.Count); Assert.IsFalse(owner.OwnedTags.HasTag(_window)); owner.EndAbility(ability);
        }
        // authored × scale 在这些组合下比实际时长多一个 ulp；圈末必须仍能收掉「随动画结束」的区间与窗口，
        // 落在末尾的标记点也必须每圈触发。授权 0.8s 是模板默认时长。
        [TestCase(.8f, .44f)]
        [TestCase(.6f, .19f)]
        [TestCase(1.2f, .77f)]
        public void ScaledLoopClosesEndWithAnimationSpansEveryCycle(float authored, float played)
        {
            var router = new GameplayCueRouter(); int opened = 0, closed = 0, ends = 0;
            router.Register(_cue, p => { if (p.Event == GameplayCueEvent.OnActive) opened++; else if (p.Event == GameplayCueEvent.Removed) closed++; });
            var owner = new AbilitySystemComponent(cueRouter: router); var ability = Activate(owner);
            var definition = new SkillAnimationDefinition("Attack", authored, true,
                markers: new[] { new AnimationMarker(authored, "End") },
                cueRegions: new[] { new AnimationCueRegion(authored * .25f, -1, _cue) },
                tagWindows: new[] { new AnimationTagWindow(authored * .25f, -1, new GameplayTagSet(_window)) });
            owner.StartTask(ability, new AnimationTask(definition, new Driver(), durationOverride: played, onMarker: (_, __) => ends++));
            for (int cycle = 1; cycle <= 5; cycle++)
            {
                owner.Tick(played);
                Assert.AreEqual(cycle, ends, "末尾标记点没有逐圈触发");
                Assert.AreEqual(opened, closed, "圈末未收掉随动画结束的 Cue 区间");
                Assert.IsFalse(owner.OwnedTags.HasTag(_window), "圈末未收掉随动画结束的标签窗口");
            }
            owner.Tick(played * .5f); Assert.IsTrue(owner.OwnedTags.HasTag(_window));
            owner.EndAbility(ability);
            Assert.IsFalse(owner.OwnedTags.HasTag(_window), "技能结束后窗口标签仍残留"); Assert.AreEqual(opened, closed);
        }
        [Test]
        public void TaskSpawnerCancellationStopsLaterEvents()
        {
            var owner = new AbilitySystemComponent(); var ability = Activate(owner); var first = new CountingSpawner { Cancel = true }; var second = new CountingSpawner();
            var definition = new SkillAnimationDefinition("Attack", 1, taskEvents: new[] { new AnimationTaskEvent(.2f, first), new AnimationTaskEvent(.3f, second) });
            owner.StartTask(ability, new AnimationTask(definition, new Driver(), taskContext: new TimelineTaskContext())); owner.Tick(1);
            Assert.AreEqual(1, first.Count); Assert.AreEqual(0, second.Count);
        }
        [Test]
        public void ThrowingRemovalListenerStillCleansEveryWindowAndStopsDriver()
        {
            var owner = new AbilitySystemComponent(); var ability = Activate(owner); var driver = new Driver();
            var definition = new SkillAnimationDefinition("Attack", 1, tagWindows: new[] { new AnimationTagWindow(0, -1, new GameplayTagSet(_window)), new AnimationTagWindow(0, -1, new GameplayTagSet(_other)) });
            owner.StartTask(ability, new AnimationTask(definition, driver)); owner.Tick(.1f);
            owner.OwnedTags.RegisterTagEventCallback(_window, GameplayTagEventType.NewOrRemoved, (_, count) => { if (count == 0) throw new InvalidOperationException("test"); });
            Assert.Throws<InvalidOperationException>(() => owner.EndAbility(ability));
            Assert.IsFalse(owner.OwnedTags.HasTag(_window)); Assert.IsFalse(owner.OwnedTags.HasTag(_other)); Assert.AreEqual(1, driver.Stops);
        }
        [Test]
        public void WarmTimelineTraversalWithWindowsAndTaskDispatchAllocatesZero()
        {
            var owner = new AbilitySystemComponent(); var ability = Activate(owner); var spawner = new CountingSpawner();
            var definition = new SkillAnimationDefinition("Attack", 1, true,
                markers: new[] { new AnimationMarker(.4f) },
                tagWindows: new[] { new AnimationTagWindow(.2f, .6f, new GameplayTagSet(_window)) },
                taskEvents: new[] { new AnimationTaskEvent(.3f, spawner) });
            owner.StartTask(ability, new AnimationTask(definition, new Driver(), taskContext: new TimelineTaskContext()));
            for (int i = 0; i < 100; i++) owner.Tick(1);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) owner.Tick(1);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.AreEqual(0, bytes); Assert.AreEqual(1100, spawner.Count); owner.EndAbility(ability);
        }
    }
}
