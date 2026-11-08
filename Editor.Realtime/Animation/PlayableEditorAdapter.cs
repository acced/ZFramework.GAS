using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class PlayableEditorAdapter : IAnimationEditorAdapter
    {
        public string BackendId => "playables";
        public string DisplayName => "Playables · 独立速度 / AvatarMask";
        public int Priority => 45;
        public bool CanHandle(GameObject preview) => preview.GetComponentInChildren<Animator>(true) != null;
        public IReadOnlyList<ClipInfo> EnumerateClips(GameObject preview)
        {
            var result = new List<ClipInfo>();
            if (preview.GetComponentInChildren<SkillAnimator>(true)?.Backend is PlayableAnimationBackend backend)
                foreach (var binding in backend.clips)
                    if (binding?.clip != null) result.Add(new ClipInfo(binding.name, binding.clip.length, binding.clip.frameRate, binding.clip.isLooping,
                        binding.clip, preview.GetComponentInChildren<Animator>(true).gameObject));
            return result;
        }
        public IPoseSampler BeginPreview(GameObject instance) => new ClipPoseSampler(EnumerateClips(instance), instance);
        public AnimationBackend CreateDefaultBackend(GameObject preview) => new PlayableAnimationBackend
        { clips = new AnimatorEditorAdapter().EnumerateClips(preview).Where(c => c.Source is AnimationClip).Select(c => new PlayableClipBinding { name = c.Name, clip = (AnimationClip)c.Source }).ToArray() };
    }
}
