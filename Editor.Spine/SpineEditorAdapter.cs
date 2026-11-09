using System.Collections.Generic;
using Spine;
using Spine.Unity;
using UnityEngine;
using ZFramework.GAS.Unity;
using ZFramework.GAS.Unity.Spine;

namespace ZFramework.GAS.Editor.Spine
{
    public sealed class SpineEditorAdapter : IAnimationEditorAdapter
    {
        public string BackendId => "spine";
        public string DisplayName => "Spine";
        public int Priority => 80;
        public bool CanHandle(GameObject preview) => preview.GetComponentInChildren<SkeletonAnimation>(true) != null;
        public IReadOnlyList<ClipInfo> EnumerateClips(GameObject preview)
        {
            var clips = new List<ClipInfo>(); var skeleton = preview.GetComponentInChildren<SkeletonAnimation>(true);
            var data = skeleton != null && skeleton.skeletonDataAsset != null ? skeleton.skeletonDataAsset.GetSkeletonData(true) : null;
            if (data != null)
                foreach (var animation in data.Animations)
                    clips.Add(new ClipInfo(animation.Name, animation.Duration, data.Fps > 0 ? data.Fps : 30, false, skeleton.skeletonDataAsset));
            return clips;
        }
        public IPoseSampler BeginPreview(GameObject instance) => new Sampler(instance.GetComponentInChildren<SkeletonAnimation>(true));
        public AnimationBackend CreateDefaultBackend(GameObject preview) => new SpineBackend();
        private sealed class Sampler : IBlendPoseSampler
        {
            private readonly SkeletonAnimation _skeleton;
            public Sampler(SkeletonAnimation skeleton)
            {
                _skeleton = skeleton;
                if (_skeleton == null) return;
                _skeleton.Initialize(false);
                _skeleton.AnimationState?.ClearTracks();
            }
            public void Sample(string clip, float time)
            {
                if (_skeleton == null || _skeleton.Skeleton == null) return;
                _skeleton.Skeleton.SetToSetupPose();
                Apply(clip, time, 1);
                PublishPose();
            }
            public void Sample(string from, float fromTime, string to, float toTime, float weight)
            {
                if (_skeleton == null || _skeleton.Skeleton == null) return;
                _skeleton.Skeleton.SetToSetupPose();
                Apply(from, fromTime, 1);
                Apply(to, toTime, Mathf.Clamp01(weight));
                PublishPose();
            }
            private void Apply(string clip, float time, float weight)
            {
                if (string.IsNullOrEmpty(clip)) return;
                var animation = _skeleton.Skeleton.Data.FindAnimation(clip);
                animation?.Apply(_skeleton.Skeleton, 0, Mathf.Clamp(time, 0, animation.Duration), false, null, weight, MixBlend.Replace, MixDirection.In);
            }
            private void PublishPose()
            {
                // Both 3.8 and 4.x update world transforms and publish the preview mesh without advancing the clock.
                _skeleton.Update(0); _skeleton.LateUpdate();
            }
            public void Dispose() { if (_skeleton != null && _skeleton.Skeleton != null) _skeleton.Skeleton.SetToSetupPose(); }
        }
    }
}
