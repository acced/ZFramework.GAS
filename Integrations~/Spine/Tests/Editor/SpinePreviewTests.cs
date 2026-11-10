using System;
using NUnit.Framework;
using Spine.Unity;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class SpinePreviewTests
    {
        private const string SkeletonJson = "{\"skeleton\":{\"hash\":\"gas-test\",\"spine\":\"4.3.00\"},"
            + "\"bones\":[{\"name\":\"root\"},{\"name\":\"hand\",\"parent\":\"root\",\"x\":3}],"
            + "\"events\":{\"hit\":{}},\"animations\":{\"move\":{\"bones\":{\"hand\":{\"translate\":["
            + "{\"x\":0},{\"time\":1,\"x\":10}]}},\"events\":[{\"time\":0.5,\"name\":\"hit\"}]}}}";

        private TextAsset _json;
        private SkeletonDataAsset _data;
        private AnimationReferenceAsset _clip;
        private GameObject _instance;
        private SkeletonAnimation _animation;
        private IAnimationClipEditor _editor;

        [SetUp]
        public void SetUp()
        {
            _json = new TextAsset(SkeletonJson);
            _data = SkeletonDataAsset.CreateRuntimeInstance(_json, Array.Empty<AtlasAssetBase>(), true, 1);
            _clip = ScriptableObject.CreateInstance<AnimationReferenceAsset>();
            _clip.SkeletonDataAsset = _data;
            _clip.AnimationName = "move";
            _instance = new GameObject("Spine preview test");
            _instance.SetActive(false);
            var renderer = _instance.AddComponent<SkeletonRenderer>();
            renderer.skeletonDataAsset = _data;
            _animation = _instance.AddComponent<SkeletonAnimation>();
            _animation.Initialize(false);
            Assert.That(AnimationClipEditors.TryGet(_clip, out _editor), Is.True);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_instance);
            Object.DestroyImmediate(_clip);
            Object.DestroyImmediate(_data);
            Object.DestroyImmediate(_json);
        }

        [Test]
        public void BackwardSeekingRestoresPoseWithoutFiringAnimationStateEvents()
        {
            int events = 0;
            _animation.AnimationState.Event += (_, __) => events++;
            using var sampler = _editor.CreatePreview(_instance, _clip);
            var skeleton = _animation.Skeleton;
            var hand = skeleton.FindBone("hand");
            Assert.That(_editor.Duration(_clip), Is.EqualTo(1));

            sampler.Sample(1);
            Assert.That(hand.Pose.X, Is.EqualTo(13).Within(.0001f));
            skeleton.FindBone("root").Pose.Y = 17;
            sampler.Sample(.25f);
            Assert.That(hand.Pose.X, Is.EqualTo(5.5f).Within(.0001f));
            Assert.That(skeleton.FindBone("root").Pose.Y, Is.Zero);
            sampler.Sample(1);
            sampler.Sample(.25f);
            Assert.That(hand.AppliedPose.WorldX, Is.EqualTo(5.5f).Within(.0001f));
            Assert.That(events, Is.Zero);
        }

        [Test]
        public void DisposalRestoresSetupPose()
        {
            var sampler = _editor.CreatePreview(_instance, _clip);
            sampler.Sample(1);
            sampler.Dispose();
            Assert.That(_animation.Skeleton.FindBone("hand").AppliedPose.WorldX, Is.EqualTo(3).Within(.0001f));
        }

        [Test]
        public void MismatchedSkeletonDataDoesNotReplacePreviewData()
        {
            var other = SkeletonDataAsset.CreateRuntimeInstance(_json, Array.Empty<AtlasAssetBase>(), true, 1);
            try
            {
                _clip.SkeletonDataAsset = other;
                _clip.Clear();
                Assert.Throws<InvalidOperationException>(() => _editor.CreatePreview(_instance, _clip));
                Assert.That(_animation.SkeletonDataAsset, Is.SameAs(_data));
            }
            finally { Object.DestroyImmediate(other); }
        }
    }
}
