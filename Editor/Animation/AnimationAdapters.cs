using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor
{
    public readonly struct ClipInfo
    {
        public readonly string Name, Warning;
        public readonly float Length, FrameRate, SampleSpeed;
        public readonly bool Loop;
        public readonly Object Source;
        public readonly GameObject SampleRoot;
        public ClipInfo(string name, float length, float frameRate, bool loop, Object source, GameObject sampleRoot = null, float speed = 1, string warning = null)
        { Name = name; Length = length; FrameRate = frameRate; Loop = loop; Source = source; SampleRoot = sampleRoot; SampleSpeed = speed; Warning = warning; }
    }
    public interface IPoseSampler : IDisposable { void Sample(string clip, float time); }
    public interface IAnimationEditorAdapter
    {
        string BackendId { get; }
        string DisplayName { get; }
        int Priority { get; }
        bool CanHandle(GameObject preview);
        IReadOnlyList<ClipInfo> EnumerateClips(GameObject preview);
        IPoseSampler BeginPreview(GameObject instance);
        AnimationBackend CreateDefaultBackend(GameObject preview);
    }
    public static class AnimationAdapterRegistry
    {
        public static readonly IReadOnlyList<IAnimationEditorAdapter> Adapters = TypeCache.GetTypesDerivedFrom<IAnimationEditorAdapter>()
            .Where(t => !t.IsAbstract && !t.IsInterface && t.GetConstructor(Type.EmptyTypes) != null)
            .Select(t => (IAnimationEditorAdapter)Activator.CreateInstance(t)).OrderByDescending(a => a.Priority).ToArray();
        public static IAnimationEditorAdapter Resolve(GameObject preview)
        {
            if (preview == null) return NullAnimationAdapter.Instance;
            var animator = preview.GetComponentInChildren<SkillAnimator>(true);
            if (animator?.Backend != null)
                return Adapters.FirstOrDefault(a => a.BackendId == animator.Backend.BackendId) ?? NullAnimationAdapter.Instance;
            return Adapters.FirstOrDefault(a => a.CanHandle(preview)) ?? NullAnimationAdapter.Instance;
        }
    }
    public sealed class NullAnimationAdapter : IAnimationEditorAdapter
    {
        public static readonly NullAnimationAdapter Instance = new();
        public string BackendId => "none";
        public string DisplayName => "未选择后端";
        public int Priority => -1;
        public bool CanHandle(GameObject p) => false;
        public IReadOnlyList<ClipInfo> EnumerateClips(GameObject p) => Array.Empty<ClipInfo>();
        public IPoseSampler BeginPreview(GameObject p) => new NullSampler();
        public AnimationBackend CreateDefaultBackend(GameObject p) => null;
        private sealed class NullSampler : IPoseSampler { public void Sample(string clip, float time) { } public void Dispose() { } }
    }
    public sealed class AnimatorEditorAdapter : IAnimationEditorAdapter
    {
        public string BackendId => "animator";
        public string DisplayName => "Animator";
        public int Priority => 50;
        public bool CanHandle(GameObject p) => p.GetComponentInChildren<Animator>(true) != null;
        public IReadOnlyList<ClipInfo> EnumerateClips(GameObject p)
        {
            var result = new List<ClipInfo>();
            var animator = p.GetComponentInChildren<Animator>(true);
            if (animator == null) return result;
            var controller = animator.runtimeAnimatorController;
            var overrides = new Dictionary<AnimationClip, AnimationClip>();
            if (controller is AnimatorOverrideController replacement)
            {
                var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(); replacement.GetOverrides(pairs);
                foreach (var pair in pairs) if (pair.Value != null) overrides[pair.Key] = pair.Value;
                controller = replacement.runtimeAnimatorController;
            }
            if (controller is AnimatorController ac)
            {
                int layer = (p.GetComponentInChildren<SkillAnimator>(true)?.Backend as AnimatorBackend)?.layer ?? 0;
                if (layer >= 0 && layer < ac.layers.Length)
                    Visit(ac.layers[layer].stateMachine, ac.layers[layer].name, animator.gameObject, result, overrides);
            }
            return result;
        }
        private static AnimationClip FirstClip(Motion motion)
        {
            if (motion is AnimationClip clip) return clip;
            if (motion is BlendTree tree) foreach (var child in tree.children) { var found = FirstClip(child.motion); if (found != null) return found; }
            return null;
        }
        private static void Visit(AnimatorStateMachine machine, string path, GameObject root, List<ClipInfo> clips, Dictionary<AnimationClip, AnimationClip> overrides)
        {
            foreach (var item in machine.states)
            {
                var state = item.state; var clip = FirstClip(state.motion); if (clip == null) continue;
                if (overrides.TryGetValue(clip, out var replacement)) clip = replacement;
                float speed = Mathf.Abs(state.speed) > .0001f ? Mathf.Abs(state.speed) : 1;
                string warning = state.motion is BlendTree ? "BlendTree 预览使用第一个子 Motion；运行期以 Controller 为准。" :
                    state.speedParameterActive || state.speed <= 0 ? "动态或反向状态速度仅作近似预览。" : null;
                clips.Add(new ClipInfo(path + "." + state.name, clip.length / speed, clip.frameRate, clip.isLooping, clip, root, speed, warning));
            }
            foreach (var child in machine.stateMachines) Visit(child.stateMachine, path + "." + child.stateMachine.name, root, clips, overrides);
        }
        public IPoseSampler BeginPreview(GameObject instance) => new ClipPoseSampler(EnumerateClips(instance), instance);
        public AnimationBackend CreateDefaultBackend(GameObject p) => new AnimatorBackend();
    }
    public sealed class LegacyAnimationEditorAdapter : IAnimationEditorAdapter
    {
        public string BackendId => "legacy";
        public string DisplayName => "Legacy Animation";
        public int Priority => 40;
        public bool CanHandle(GameObject p) => p.GetComponentInChildren<Animation>(true) != null;
        public IReadOnlyList<ClipInfo> EnumerateClips(GameObject p)
        {
            var result = new List<ClipInfo>(); var animation = p.GetComponentInChildren<Animation>(true);
            if (animation != null)
                foreach (AnimationState state in animation)
                    if (state.clip != null) result.Add(new ClipInfo(state.name, state.clip.length, state.clip.frameRate, state.clip.isLooping, state.clip, animation.gameObject));
            return result;
        }
        public IPoseSampler BeginPreview(GameObject p) => new ClipPoseSampler(EnumerateClips(p), p);
        public AnimationBackend CreateDefaultBackend(GameObject p) => new LegacyAnimationBackend();
    }
    public sealed class SpriteSequenceEditorAdapter : IAnimationEditorAdapter
    {
        public string BackendId => "sprite";
        public string DisplayName => "序列帧";
        public int Priority => 10;
        public bool CanHandle(GameObject p) => p.GetComponentInChildren<SpriteRenderer>(true) != null;
        public IReadOnlyList<ClipInfo> EnumerateClips(GameObject p)
        {
            var backend = p.GetComponentInChildren<SkillAnimator>(true)?.Backend as SpriteSequenceBackend;
            return backend?.sequences?.Where(s => s != null && s.frames?.Length > 0 && s.frameRate > 0)
                .Select(s => new ClipInfo(s.name, s.frames.Length / s.frameRate, s.frameRate, false, s.frames[0])).ToArray() ?? Array.Empty<ClipInfo>();
        }
        public IPoseSampler BeginPreview(GameObject p) => new SpriteSampler(p);
        public AnimationBackend CreateDefaultBackend(GameObject p) => new SpriteSequenceBackend();
        private sealed class SpriteSampler : IPoseSampler
        {
            private readonly SpriteRenderer _renderer;
            private readonly Sprite _original;
            private readonly SpriteSequenceBackend _backend;
            public SpriteSampler(GameObject p)
            { _renderer = p.GetComponentInChildren<SpriteRenderer>(true); _original = _renderer != null ? _renderer.sprite : null; _backend = p.GetComponentInChildren<SkillAnimator>(true)?.Backend as SpriteSequenceBackend; }
            public void Sample(string clip, float time)
            {
                var sequence = _backend?.sequences?.FirstOrDefault(s => s?.name == clip);
                if (_renderer != null && sequence?.frames?.Length > 0)
                    _renderer.sprite = sequence.frames[Mathf.Clamp((int)(time * sequence.frameRate), 0, sequence.frames.Length - 1)];
            }
            public void Dispose() { if (_renderer != null) _renderer.sprite = _original; }
        }
    }
    internal sealed class ClipPoseSampler : IPoseSampler
    {
        private readonly IReadOnlyList<ClipInfo> _clips;
        private readonly Transform[] _transforms;
        private readonly Vector3[] _positions, _scales;
        private readonly Quaternion[] _rotations;
        public ClipPoseSampler(IReadOnlyList<ClipInfo> clips, GameObject root)
        {
            _clips = clips; _transforms = root.GetComponentsInChildren<Transform>(true);
            _positions = _transforms.Select(t => t.localPosition).ToArray(); _scales = _transforms.Select(t => t.localScale).ToArray();
            _rotations = _transforms.Select(t => t.localRotation).ToArray();
        }
        public void Sample(string name, float time)
        {
            Restore();
            foreach (var info in _clips)
                if (info.Name == name && info.Source is AnimationClip clip)
                { clip.SampleAnimation(info.SampleRoot, Mathf.Clamp(time * info.SampleSpeed, 0, clip.length)); break; }
        }
        private void Restore()
        {
            for (int i = 0; i < _transforms.Length; i++) if (_transforms[i] != null)
            { _transforms[i].localPosition = _positions[i]; _transforms[i].localRotation = _rotations[i]; _transforms[i].localScale = _scales[i]; }
        }
        public void Dispose() => Restore();
    }
}
