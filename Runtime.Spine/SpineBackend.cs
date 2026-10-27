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
    public sealed class SpineAnimationDriver : IAnimationDriver
    {
        private readonly SkeletonAnimation _skeleton;
        private readonly int _track;
        private readonly float _mix;
        private TrackEntry _entry;
        public SpineAnimationDriver(SkeletonAnimation skeleton, int track = 0, float mix = .1f)
        {
            _skeleton = skeleton != null ? skeleton : throw new ArgumentNullException(nameof(skeleton));
            _track = track; _mix = mix; skeleton.Initialize(false);
        }
        public float Play(string clipName, float duration, bool loop)
        {
            if (_skeleton == null || _skeleton.Skeleton == null) return 0;
            var animation = _skeleton.Skeleton.Data.FindAnimation(clipName); if (animation == null) return 0;
            _entry = _skeleton.AnimationState.SetAnimation(_track, animation, loop);
            bool stretch = duration > 0 && animation.Duration > 0;
            _entry.TimeScale = stretch ? animation.Duration / duration : 1;
            return stretch ? duration : animation.Duration;
        }
        public void Advance(float normalizedTime) { }
        public void Stop()
        {
            if (_entry == null) return;
            if (_skeleton != null && _skeleton.AnimationState.GetCurrent(_track) == _entry)
                _skeleton.AnimationState.SetEmptyAnimation(_track, _mix);
            _entry = null;
        }
    }
}
