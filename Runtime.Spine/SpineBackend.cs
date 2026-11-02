using System;
using Spine;
using Spine.Unity;
using UnityEngine;

namespace ZFramework.GAS.Unity.Spine
{
    [Serializable]
    public sealed class SpineBackend : AnimationBackend
    {
        [Min(0)] public int trackIndex;
        [Min(0)] public float mixDuration = .1f;
        public override string BackendId => "spine";
        public override bool Validate(GameObject host, out string error)
        {
            var skeleton = host != null ? host.GetComponentInChildren<SkeletonAnimation>(true) : null;
            error = skeleton == null ? "缺少 SkeletonAnimation" : skeleton.skeletonDataAsset == null ? "缺少 SkeletonDataAsset" :
                trackIndex < 0 || mixDuration < 0 ? "Spine 轨道与混合时长不能为负" : null;
            return error == null;
        }
        public override IAnimationDriver CreateDriver(GameObject host)
            => new SpineAnimationDriver(host.GetComponentInChildren<SkeletonAnimation>(true), trackIndex, mixDuration);
    }
    public sealed class SpineAnimationDriver : IBlendingAnimationDriver
    {
        private readonly SkeletonAnimation _skeleton;
        private readonly int _track;
        private readonly float _mix;
        private TrackEntry _entry;
        // 施放前这条轨道上的动画：结束时混回它（通常是待机），而不是回到绑定姿势。
        private global::Spine.Animation _previous;
        private bool _previousLoop;
        public SpineAnimationDriver(SkeletonAnimation skeleton, int track = 0, float mix = .1f)
        {
            _skeleton = skeleton != null ? skeleton : throw new ArgumentNullException(nameof(skeleton));
            _track = track; _mix = mix; skeleton.Initialize(false);
        }
        /// <summary>不指定混入时沿用 SkeletonData 里配置的默认混合。</summary>
        public float Play(string clipName, float duration, bool loop) => Play(clipName, duration, loop, -1);

        public float Play(string clipName, float duration, bool loop, float blendIn)
        {
            if (_skeleton == null || _skeleton.Skeleton == null) return 0;
            var animation = _skeleton.Skeleton.Data.FindAnimation(clipName); if (animation == null) return 0;
            if (_entry == null)
            {
                var current = _skeleton.AnimationState.GetCurrent(_track);
                bool idle = current != null && current.Animation != null && current.Animation.Name != "<empty>";
                _previous = idle ? current.Animation : null; _previousLoop = idle && current.Loop;
            }
            _entry = _skeleton.AnimationState.SetAnimation(_track, animation, loop);
            bool stretch = duration > 0 && animation.Duration > 0;
            _entry.TimeScale = stretch ? animation.Duration / duration : 1;
            // Spine 的混合按实际时间推进、不受 TimeScale 影响，换算后才与标记点同比缩放。
            if (blendIn >= 0) _entry.MixDuration = blendIn / _entry.TimeScale;
            return stretch ? duration : animation.Duration;
        }
        public void Advance(float normalizedTime) { }
        public void Stop() => Stop(_mix);

        public void Stop(float blendOut)
        {
            if (_entry == null) return;
            if (_skeleton != null && _skeleton.AnimationState.GetCurrent(_track) == _entry)
            {
                if (_previous != null) _skeleton.AnimationState.SetAnimation(_track, _previous, _previousLoop).MixDuration = blendOut;
                else _skeleton.AnimationState.SetEmptyAnimation(_track, blendOut);
            }
            _entry = null; _previous = null;
        }
    }
}
