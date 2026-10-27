using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [Serializable]
    public abstract class AnimationBackend
    {
        public abstract string BackendId { get; }
        public abstract IAnimationDriver CreateDriver(GameObject host);
        public abstract bool Validate(GameObject host, out string error);
    }

    [Serializable]
    public sealed class AnimatorBackend : AnimationBackend
    {
        [Min(0)] public int layer;
        public override string BackendId => "animator";
        public override IAnimationDriver CreateDriver(GameObject host)
            => new AnimatorAnimationDriver(host.GetComponentInChildren<Animator>(true), layer);
        public override bool Validate(GameObject host, out string error)
        {
            var animator = host != null ? host.GetComponentInChildren<Animator>(true) : null;
            error = animator == null ? "缺少 Animator 组件" : animator.runtimeAnimatorController == null
                ? "Animator 未设置 Controller" : layer < 0 || (animator.isInitialized && layer >= animator.layerCount) ? "Animator 层索引越界" : null;
            return error == null;
        }
    }

    [Serializable]
    public sealed class LegacyAnimationBackend : AnimationBackend
    {
        public override string BackendId => "legacy";
        public override IAnimationDriver CreateDriver(GameObject host)
            => new LegacyAnimationDriver(host.GetComponentInChildren<Animation>(true));
        public override bool Validate(GameObject host, out string error)
        {
            error = host != null && host.GetComponentInChildren<Animation>(true) != null ? null : "缺少 Animation 组件";
            return error == null;
        }
    }

    [Serializable]
    public sealed class SpriteSequence
    {
        public string name = "Attack";
        [Min(1)] public float frameRate = 30f;
        public Sprite[] frames = Array.Empty<Sprite>();
    }

    [Serializable]
    public sealed class SpriteSequenceBackend : AnimationBackend
    {
        public SpriteSequence[] sequences = Array.Empty<SpriteSequence>();
        public override string BackendId => "sprite";
        public override bool Validate(GameObject host, out string error)
        {
            error = host == null || host.GetComponentInChildren<SpriteRenderer>(true) == null ? "缺少 SpriteRenderer" : null;
            if (error == null && (sequences == null || sequences.Length == 0)) error = "尚未配置序列帧";
            if (error == null)
                for (int i = 0; i < sequences.Length; i++)
                {
                    var s = sequences[i];
                    if (s == null || string.IsNullOrEmpty(s.name) || s.frames == null || s.frames.Length == 0 ||
                        !(s.frameRate > 0f) || float.IsInfinity(s.frameRate) || Array.Exists(s.frames, f => f == null))
                    { error = "序列帧需要唯一名称、有效帧率和非空图片"; break; }
                    for (int j = 0; j < i; j++)
                        if (sequences[j].name == s.name) { error = "序列帧名称重复：" + s.name; break; }
                }
            return error == null;
        }
        public override IAnimationDriver CreateDriver(GameObject host)
            => new SequenceSetDriver(host.GetComponentInChildren<SpriteRenderer>(true), sequences);

        private sealed class SequenceSetDriver : IAnimationDriver
        {
            private readonly string[] _names;
            private readonly SpriteSequenceDriver[] _drivers;
            private SpriteSequenceDriver _active;
            public SequenceSetDriver(SpriteRenderer renderer, SpriteSequence[] sequences)
            {
                _names = new string[sequences.Length];
                _drivers = new SpriteSequenceDriver[sequences.Length];
                for (int i = 0; i < sequences.Length; i++)
                {
                    var sequence = sequences[i];
                    _names[i] = sequence.name;
                    _drivers[i] = new SpriteSequenceDriver(renderer, sequence.name, sequence.frames, sequence.frameRate);
                }
            }
            public float Play(string clipName, float duration, bool loop)
            {
                for (int i = 0; i < _names.Length; i++)
                    if (_names[i] == clipName)
                    {
                        Stop();
                        _active = _drivers[i];
                        return _active.Play(clipName, duration, loop);
                    }
                return 0f;
            }
            public void Advance(float normalizedTime) => _active?.Advance(normalizedTime);
            public void Stop() { _active?.Stop(); _active = null; }
        }
    }
}
