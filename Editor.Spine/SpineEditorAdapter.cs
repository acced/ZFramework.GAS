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
                    clips.Add(new ClipInfo(animation.Name, animation.Duration, 30, false, skeleton.skeletonDataAsset));
            return clips;
        }
        public IPoseSampler BeginPreview(GameObject instance) => new Sampler(instance.GetComponentInChildren<SkeletonAnimation>(true));
        public AnimationBackend CreateDefaultBackend(GameObject preview) => new SpineBackend();
        private sealed class Sampler : IPoseSampler
        {
            private readonly SkeletonAnimation _skeleton;
            public Sampler(SkeletonAnimation skeleton) { _skeleton = skeleton; _skeleton.Initialize(false); _skeleton.AnimationState.ClearTracks(); }
            public void Sample(string clip, float time)
            {
                if (_skeleton == null || _skeleton.Skeleton == null) return;
                var animation = _skeleton.Skeleton.Data.FindAnimation(clip); if (animation == null) return;
                _skeleton.Skeleton.SetToSetupPose();
                animation.Apply(_skeleton.Skeleton, 0, Mathf.Clamp(time, 0, animation.Duration), false, null, 1, MixBlend.Replace, MixDirection.In);
                // Spine 4.2 adds a Physics parameter; Update(0) provides the stable 4.x world-transform API.
                _skeleton.Update(0);
                _skeleton.LateUpdate();
            }
            public void Dispose() { if (_skeleton != null && _skeleton.Skeleton != null) _skeleton.Skeleton.SetToSetupPose(); }
        }
    }
}
