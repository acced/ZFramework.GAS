using System;
using Spine;
using Spine.Unity;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor
{
    internal sealed class SpineAnimationClipEditor : IAnimationClipEditor
    {
        [InitializeOnLoadMethod]
        private static void Register() => AnimationClipEditors.Register(new SpineAnimationClipEditor());

        public Type AssetType => typeof(AnimationReferenceAsset);
        public float Duration(Object clip) => Resolve((AnimationReferenceAsset)clip).Duration;
        public IAnimationPreviewSampler CreatePreview(GameObject instance, Object clip) =>
            new SpinePoseSampler(instance, (AnimationReferenceAsset)clip);

        internal static Spine.Animation Resolve(AnimationReferenceAsset reference) =>
            reference.Animation ?? throw new InvalidOperationException("Spine 动画引用无效，请检查 SkeletonData 和动画名称。");
    }

    /// <summary>Absolute pose sampling; does not advance AnimationState or simulate physics history.</summary>
    internal sealed class SpinePoseSampler : IAnimationPreviewSampler
    {
        private readonly SkeletonRenderer _renderer;
        private readonly Skeleton _skeleton;
        private readonly Spine.Animation _animation;

        public SpinePoseSampler(GameObject instance, AnimationReferenceAsset reference)
        {
            _animation = SpineAnimationClipEditor.Resolve(reference);
            var animation = instance.GetComponentInChildren<SkeletonAnimation>(true);
            _renderer = animation != null ? animation.GetComponent<SkeletonRenderer>() : null;
            if (_renderer == null)
                throw new InvalidOperationException("Spine 预览角色需要 SkeletonAnimation 和 SkeletonRenderer。");
            if (_renderer.SkeletonDataAsset != reference.SkeletonDataAsset)
                throw new InvalidOperationException("Spine 动画引用与预览角色的 SkeletonData 不一致。");

            animation.enabled = false;
            _renderer.enabled = false;
            animation.Initialize(false, true);
            _skeleton = _renderer.Skeleton;
        }

        public void Sample(float normalizedTime)
        {
            _skeleton.Time = 0;
            _skeleton.SetupPose();
            _animation.Apply(_skeleton, -1, Mathf.Clamp01(normalizedTime) * _animation.Duration,
                false, null, 1, MixFrom.Setup, false, false, false);
            RefreshMesh();
        }

        private void RefreshMesh()
        {
            _skeleton.UpdateWorldTransform(Spine.Physics.Reset);
            _renderer.UpdateMesh();
        }

        public void Dispose()
        {
            _skeleton.Time = 0;
            _skeleton.SetupPose();
            RefreshMesh();
        }
    }
}
