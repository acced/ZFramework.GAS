using System;
using NUnit.Framework;
using Spine.Unity;
using UnityEngine;
using ZFramework.GAS.Unity.Spine;
using AnimationState = Spine.AnimationState;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class SpineAnimationDriverTests
    {
        private const string SkeletonJson = "{\"skeleton\":{\"hash\":\"gas-driver-test\",\"spine\":\"4.3.00\"},"
            + "\"bones\":[{\"name\":\"root\"}],\"animations\":{"
            + "\"idle\":{\"bones\":{\"root\":{\"translate\":[{\"x\":2},{\"time\":1,\"x\":2}]}}},"
            + "\"skill\":{\"bones\":{\"root\":{\"translate\":[{\"x\":0},{\"time\":1,\"x\":10}]}}},"
            + "\"other\":{\"bones\":{\"root\":{\"translate\":[{\"x\":0},{\"time\":2,\"x\":20}]}}}}}";

        private TextAsset _json;
        private SkeletonDataAsset _data;
        private AnimationReferenceAsset _skill, _other;
        private GameObject _instance;
        private SkeletonAnimation _animation;
        private AnimationState _state;
        private SpineAnimationDriver _driver;

        [SetUp]
        public void SetUp()
        {
            _json = new TextAsset(SkeletonJson);
            _data = SkeletonDataAsset.CreateRuntimeInstance(_json, Array.Empty<AtlasAssetBase>(), true, 1);
            _skill = Reference("skill");
            _other = Reference("other");
            _instance = new GameObject("Spine driver test");
            _instance.SetActive(false);
            var renderer = _instance.AddComponent<SkeletonRenderer>();
            renderer.SkeletonDataAsset = _data;
            _animation = _instance.AddComponent<SkeletonAnimation>();
            _animation.Initialize(false);
            _state = _animation.AnimationState;
            _state.SetAnimation(0, "idle", true);
            Frame(0);
            _driver = new SpineAnimationDriver(_animation);
        }

        [TearDown]
        public void TearDown()
        {
            _driver.Dispose();
            Object.DestroyImmediate(_instance);
            Object.DestroyImmediate(_skill);
            Object.DestroyImmediate(_other);
            Object.DestroyImmediate(_data);
            Object.DestroyImmediate(_json);
        }

        [Test]
        public void GasPositionsTheSkillWhileNativeUpdatesAdvanceBaseAndMix()
        {
            _driver.Play(_skill, .4f);
            _driver.Advance(.5f);
            Frame(.2f);

            var skill = _state.GetTrack(1);
            Assert.That(skill.TrackTime, Is.EqualTo(.5f));
            Assert.That(skill.MixTime, Is.EqualTo(.2f));
            Assert.That(_state.GetTrack(0).TrackTime, Is.EqualTo(.2f));
            Assert.That(_animation.Skeleton.RootBone.Pose.X, Is.EqualTo(3.5f).Within(.001f));

            _driver.Advance(1);
            _driver.Advance(1);
            _driver.Advance(.25f);
            Assert.That(skill.TrackTime, Is.EqualTo(.25f));
            Assert.That(skill.MixTime, Is.EqualTo(.2f));
            Frame(.1f);
            Assert.That(skill.TrackTime, Is.EqualTo(.25f));
            Assert.That(skill.MixTime, Is.EqualTo(.3f).Within(.0001f));
        }

        [Test]
        public void StopMixesOutAfterGasStopsSamplingAndPreservesTheBaseTrack()
        {
            _driver.Play(_skill, 0);
            _driver.Advance(1);
            Frame(0);
            Assert.That(_animation.Skeleton.RootBone.Pose.X, Is.EqualTo(10));

            _driver.Stop(.4f);
            Frame(.2f);
            Assert.That(_animation.Skeleton.RootBone.Pose.X, Is.EqualTo(6).Within(.001f));
            Frame(.2f);
            Assert.That(_animation.Skeleton.RootBone.Pose.X, Is.EqualTo(2).Within(.001f));
            // 4.3 detaches the mixed-out entry, applies the empty entry once,
            // then clears the track on the following native Update.
            Frame(.01f);
            Frame(.01f);
            Assert.That(_state.GetTrack(1), Is.Null);
            Assert.That(_state.GetTrack(0).Animation.Name, Is.EqualTo("idle"));
        }

        [Test]
        public void StartListenerCanRecastAndSampleBeforeTheNewStartIsDelivered()
        {
            _driver.Dispose();
            bool recast = false;
            _state.Start += entry =>
            {
                if (entry.Animation != _skill.Animation || recast) return;
                recast = true;
                _driver.Play(_other, .25f);
                _driver.Advance(.4f);
            };
            _driver = new SpineAnimationDriver(_animation);

            _driver.Play(_skill, .1f);
            var current = _state.GetTrack(1);
            Assert.That(current.Animation, Is.SameAs(_other.Animation));
            Assert.That(current.TimeScale, Is.Zero);
            Assert.That(current.MixDuration, Is.EqualTo(.25f));
            Assert.That(current.TrackTime, Is.EqualTo(.8f));
            Frame(.1f);
            Assert.That(current.TrackTime, Is.EqualTo(.8f));
        }

        [Test]
        public void CancellationDuringTheInitialEmptyStartDoesNotStartTheSkill()
        {
            _driver.Dispose();
            bool cancelled = false;
            _state.Start += entry =>
            {
                if (entry.TrackIndex != 1 || cancelled) return;
                cancelled = true;
                _driver.Stop(.3f);
            };
            _driver = new SpineAnimationDriver(_animation);

            _driver.Play(_skill, .1f);
            var current = _state.GetTrack(1);
            Assert.That(current.Animation.Timelines.Count, Is.Zero);
            Assert.That(current.MixDuration, Is.EqualTo(.3f));
            Assert.That(current.TrackEnd, Is.EqualTo(.3f));
            Assert.That(current.TimeScale, Is.EqualTo(1));
        }

        [Test]
        public void StopListenerCanStartANewSkillWithoutOldStopSettingsOverwritingIt()
        {
            _driver.Play(_skill, 0);
            Frame(0);
            bool recast = false;
            _state.Start += entry =>
            {
                if (entry.TrackIndex != 1 || entry.Animation.Timelines.Count != 0 || recast) return;
                recast = true;
                _driver.Play(_other, .15f);
                _driver.Advance(.75f);
            };

            _driver.Stop(.8f);
            var current = _state.GetTrack(1);
            Assert.That(current.Animation, Is.SameAs(_other.Animation));
            Assert.That(current.MixDuration, Is.EqualTo(.15f));
            Assert.That(current.TrackEnd, Is.EqualTo(float.MaxValue));
            Assert.That(current.TrackTime, Is.EqualTo(1.5f));
        }

        [Test]
        public void ReleasingOldEntriesCannotDropOrRetargetTheCurrentSamplingReference()
        {
            _driver.Play(_skill, 0);
            _driver.Play(_skill, 0);
            _driver.Advance(.3f);
            Frame(0);
            Assert.That(_state.GetTrack(1).TrackTime, Is.EqualTo(.3f));

            _driver.Play(_other, .1f);
            _driver.Advance(.6f);
            Frame(.1f);
            Frame(.1f);
            _driver.Advance(.7f);
            Assert.That(_state.GetTrack(1).TrackTime, Is.EqualTo(1.4f));

            _state.ClearTrack(1);
            var unrelated = _state.SetAnimation(2, _skill.Animation, false);
            _driver.Advance(.9f);
            Assert.That(unrelated.TrackTime, Is.Zero);
        }

        [Test]
        public void RepeatedDisposalCannotClearANewDriversTrackOrTheBaseAnimation()
        {
            _driver.Play(_skill, 0);
            _driver.Dispose();
            using var replacement = new SpineAnimationDriver(_animation);
            replacement.Play(_other, 0);
            _driver.Dispose();
            Assert.That(_state.GetTrack(1).Animation, Is.SameAs(_other.Animation));
            Assert.That(_state.GetTrack(0).Animation.Name, Is.EqualTo("idle"));
        }

        private AnimationReferenceAsset Reference(string name)
        {
            var reference = ScriptableObject.CreateInstance<AnimationReferenceAsset>();
            reference.SkeletonDataAsset = _data;
            reference.AnimationName = name;
            return reference;
        }

        private void Frame(float delta)
        {
            _state.Update(delta);
            _state.Apply(_animation.Skeleton);
        }
    }
}
