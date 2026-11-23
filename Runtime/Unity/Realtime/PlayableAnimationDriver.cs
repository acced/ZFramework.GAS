using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Experimental.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Unity
{
    /// <summary>Unity Playables 单层技能动画叠加驱动：支持 AvatarMask、FootIK、同步采样与空闲休眠。</summary>
    public sealed class PlayableAnimationDriver : IAnimationDriver
    {
        private readonly PlayableGraph _graph;
        private readonly AnimationMixerPlayable _mixer;
        private readonly AnimationPlayableOutput _output;
        private readonly Fade _fade;
        private readonly bool _applyFootIK, _syncPoseOnAdvance;
        private AnimationClipPlayable _current, _previous;
        private float _length;

        public PlayableAnimationDriver(Animator animator, AvatarMask avatarMask = null,
            bool applyFootIK = false, ushort sortingOrder = 100, bool syncPoseOnAdvance = false)
        {
            if (animator == null) throw new ArgumentNullException(nameof(animator));
            _applyFootIK = applyFootIK; _syncPoseOnAdvance = syncPoseOnAdvance;

            _graph = PlayableGraph.Create("GAS Timeline");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _mixer = AnimationMixerPlayable.Create(_graph, 2);

            ScriptPlayable<Fade> control = ScriptPlayable<Fade>.Create(_graph, 1);
            if (avatarMask != null)
            {
                AnimationLayerMixerPlayable layer = AnimationLayerMixerPlayable.Create(_graph, 1);
                _graph.Connect(_mixer, 0, layer, 0);
                layer.SetInputWeight(0, 1f);
                layer.SetLayerMaskFromAvatarMask(0, avatarMask);
                _graph.Connect(layer, 0, control, 0);
            }
            else _graph.Connect(_mixer, 0, control, 0);

            control.SetInputWeight(0, 1f);
            _output = AnimationPlayableOutput.Create(_graph, "Skill", animator);
            _output.SetSourcePlayable(control);
            _output.SetAnimationStreamSource(AnimationStreamSource.PreviousInputs);
            _output.SetSortingOrder(sortingOrder);
            _output.SetWeight(0f);

            _fade = control.GetBehaviour();
            _fade.Mixer = _mixer; _fade.Output = _output;
        }

        public void Play(Object clip, float blendIn)
        {
            if (!_graph.IsValid()) throw new ObjectDisposedException(nameof(PlayableAnimationDriver));
            if (clip is not AnimationClip animation)
                throw new ArgumentException($"PlayableAnimationDriver 需要 AnimationClip，实际收到: {clip?.GetType().Name ?? "null"}。", nameof(clip));
            blendIn = Mathf.Max(0f, blendIn);
            if (!_graph.IsPlaying()) _graph.Play();

            if (_previous.IsValid()) { _mixer.DisconnectInput(0); _graph.DestroyPlayable(_previous); _previous = default; }
            if (_current.IsValid()) { _mixer.DisconnectInput(1); _previous = _current; _graph.Connect(_previous, 0, _mixer, 0); }

            _current = AnimationClipPlayable.Create(_graph, animation);
            _current.SetSpeed(0);
            _current.SetApplyFootIK(_applyFootIK);
            _graph.Connect(_current, 0, _mixer, 1);
            _length = animation.length;
            _fade.Begin(1f, blendIn, _previous.IsValid() && _output.GetWeight() > 0f);
        }

        public void Advance(float normalizedTime)
        {
            if (!_graph.IsValid() || !_current.IsValid()) return;
            _current.SetTime(Mathf.Clamp01(normalizedTime) * _length);
            if (_syncPoseOnAdvance) _graph.Evaluate(0f);
        }

        public void SyncPose() { if (_graph.IsValid() && _current.IsValid()) _graph.Evaluate(0f); }

        public void Stop(float blendOut)
        {
            if (!_graph.IsValid()) return;
            blendOut = Mathf.Max(0f, blendOut);
            _fade.Begin(0f, blendOut, false);
            if (blendOut > 0f) return;

            if (_previous.IsValid()) { _mixer.DisconnectInput(0); _graph.DestroyPlayable(_previous); _previous = default; }
            if (_current.IsValid()) { _mixer.DisconnectInput(1); _graph.DestroyPlayable(_current); _current = default; }
            if (_graph.IsPlaying()) _graph.Stop();
        }

        public void Dispose()
        {
            _current = _previous = default;
            if (_graph.IsValid()) _graph.Destroy();
        }

        private sealed class Fade : PlayableBehaviour
        {
            public AnimationMixerPlayable Mixer;
            public AnimationPlayableOutput Output;
            private float _from, _to, _duration, _elapsed;
            private bool _crossFade;

            public void Begin(float target, float duration, bool crossFade)
            {
                _from = Output.GetWeight(); _to = target; _duration = duration; _elapsed = 0f; _crossFade = crossFade;
                Apply(duration <= 0f ? 1f : 0f);
            }

            public override void PrepareFrame(Playable playable, FrameData info)
            {
                if (_elapsed >= _duration) return;
                _elapsed = Mathf.Min(_duration, _elapsed + info.deltaTime);
                Apply(_elapsed / _duration);
            }

            private void Apply(float t)
            {
                Output.SetWeight(Mathf.Lerp(_from, _to, t));
                if (_to == 0f) return;
                Mixer.SetInputWeight(0, _crossFade ? 1f - t : 0f);
                Mixer.SetInputWeight(1, _crossFade ? t : 1f);
            }
        }
    }
}
