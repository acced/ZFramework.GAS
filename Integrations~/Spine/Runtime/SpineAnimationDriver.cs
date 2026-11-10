using System;
using Spine;
using Spine.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Unity.Spine
{
    /// <summary>Owns one Spine track. The host updates mixing; GAS supplies the skill's position.</summary>
    public sealed class SpineAnimationDriver : IAnimationDriver
    {
        private static readonly Animation Empty = CreateEmpty();
        private readonly AnimationState _state;
        private readonly int _trackIndex;
        private TrackEntry _entry;
        private Animation _animation;
        private float _blend, _normalizedTime;
        private int _version;
        private bool _disposed;

        public SpineAnimationDriver(SkeletonAnimation skeletonAnimation, int trackIndex = 1)
        {
            _state = skeletonAnimation.AnimationState;
            _trackIndex = trackIndex;
            _state.Start += Started;
            _state.Dispose += Released;
        }

        public void Play(Object clip, float blendIn)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SpineAnimationDriver));
            int version = ++_version;
            _entry = null;
            _normalizedTime = 0;
            var animation = ((AnimationReferenceAsset)clip).Animation;
            if (_state.GetTrack(_trackIndex) == null && blendIn > 0)
            {
                _animation = Empty;
                _blend = 0;
                _state.SetAnimation(_trackIndex, Empty, false);
                if (version != _version) return;
            }
            _animation = animation;
            _blend = blendIn;
            _state.SetAnimation(_trackIndex, animation, false);
        }

        public void Advance(float normalizedTime)
        {
            _normalizedTime = normalizedTime;
            var entry = _entry;
            if (entry != null && entry.Animation != Empty)
                entry.TrackTime = normalizedTime * entry.Animation.Duration;
        }

        public void Stop(float blendOut)
        {
            if (_disposed) return;
            ++_version;
            _entry = null;
            _animation = Empty;
            _blend = blendOut;
            _state.SetAnimation(_trackIndex, Empty, false);
        }

        private void Started(TrackEntry entry)
        {
            if (entry.TrackIndex != _trackIndex || entry.Animation != _animation || _state.GetTrack(_trackIndex) != entry) return;
            // Configure in Start: SetAnimation can synchronously cancel/recast before returning.
            _entry = entry;
            bool empty = entry.Animation == Empty;
            entry.TimeScale = empty ? 1 : 0;
            entry.MixDuration = _blend;
            if (empty) entry.TrackEnd = _blend;
            else entry.TrackTime = _normalizedTime * entry.Animation.Duration;
        }

        private void Released(TrackEntry entry)
        {
            if (_entry == entry) _entry = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ++_version;
            _state.Start -= Started;
            _state.Dispose -= Released;
            _entry = null;
            _state.ClearTrack(_trackIndex);
        }

        private static Animation CreateEmpty()
        {
            var animation = new Animation("GAS empty");
            animation.SetTimelines(new ExposedList<Timeline>(0), new ExposedList<int>(0));
            return animation;
        }
    }
}
