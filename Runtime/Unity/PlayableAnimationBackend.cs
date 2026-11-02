using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ZFramework.GAS.Unity
{
    public interface ISkillMovementAdapter
    {
        void ApplyRootMotion(Vector3 deltaPosition, Quaternion deltaRotation);
    }
    [Serializable]
    public sealed class PlayableClipBinding
    {
        public string name;
        public AnimationClip clip;
    }
    [Serializable]
    public sealed class PlayableAnimationBackend : AnimationBackend
    {
        public PlayableClipBinding[] clips = Array.Empty<PlayableClipBinding>();
        public AvatarMask mask;
        public bool additive;
        public bool rootMotion;
        public override string BackendId => "playables";
        public override bool Validate(GameObject host, out string error)
        {
            var animator = host != null ? host.GetComponentInChildren<Animator>(true) : null;
            error = animator == null ? "缺少 Animator" : clips == null || clips.Length == 0 ? "Playables 尚未绑定片段" : null;
            for (int i = 0; error == null && i < clips.Length; i++)
            {
                if (clips[i]?.clip == null || string.IsNullOrEmpty(clips[i].name)) error = "片段名称和 AnimationClip 必须填写";
                for (int j = 0; j < i; j++) if (clips[i]?.name == clips[j]?.name) error = "重复片段名";
            }
            if (error == null && rootMotion && animator.GetComponentInParent<ISkillMovementAdapter>() == null) error = "根运动需要项目提供 ISkillMovementAdapter";
            return error == null;
        }
        public override IAnimationDriver CreateDriver(GameObject host)
            => new PlayableAnimationDriver(host.GetComponentInChildren<Animator>(true), clips, mask, additive, rootMotion);
    }

    /// <summary>Base controller runs at its own speed. Only the skill playable is stretched.</summary>
    public sealed class PlayableAnimationDriver : IBlendingAnimationDriver, ISeekableAnimationDriver, IAnimationDriverLifetime, IDisposable
    {
        private readonly Animator _animator;
        private readonly string[] _names;
        private readonly AnimationClip[] _clips;
        private readonly SkillPlayableClock _clock;
        private readonly SkillRootMotionRelay _rootMotion;
        private PlayableGraph _graph;
        private AnimationLayerMixerPlayable _layers;
        private AnimationMixerPlayable _skills;
        private AnimatorControllerPlayable _controller;
        private sealed class ClipState { public AnimationClipPlayable Clip; public float Weight, StartWeight; public bool Loop; }
        private readonly List<ClipState> _playing = new();
        private AnimationClipPlayable _current;
        private float _fade, _elapsed, _layerWeight, _startWeight;
        private bool _stopping, _disposed, _rootMotionActive;
        public bool IsValid => !_disposed && _animator != null && _graph.IsValid();
        private bool _restoreRootMotion;
        public PlayableAnimationDriver(Animator animator, PlayableClipBinding[] clips, AvatarMask mask = null, bool additive = false, bool rootMotion = false)
        {
            _animator = animator != null ? animator : throw new ArgumentNullException(nameof(animator));
            _names = new string[clips.Length]; _clips = new AnimationClip[clips.Length];
            for (int i = 0; i < clips.Length; i++) { _names[i] = clips[i].name; _clips[i] = clips[i].clip; }
            // Capture the already-running controller before binding the graph output.
            var parameters = animator.parameters;
            var values = new (float f, int i, bool b)[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (p.type == AnimatorControllerParameterType.Float) values[i].f = animator.GetFloat(p.nameHash);
                else if (p.type == AnimatorControllerParameterType.Int) values[i].i = animator.GetInteger(p.nameHash);
                else if (p.type == AnimatorControllerParameterType.Bool) values[i].b = animator.GetBool(p.nameHash);
            }
            var states = new AnimatorStateInfo[animator.isInitialized ? animator.layerCount : 0];
            var weights = new float[states.Length];
            for (int i = 0; i < states.Length; i++) { states[i] = animator.GetCurrentAnimatorStateInfo(i); weights[i] = animator.GetLayerWeight(i); }
            _graph = PlayableGraph.Create("GAS Skill Animation"); _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _layers = AnimationLayerMixerPlayable.Create(_graph, 2); _skills = AnimationMixerPlayable.Create(_graph, 0);
            if (animator.runtimeAnimatorController != null)
            {
                _controller = AnimatorControllerPlayable.Create(_graph, animator.runtimeAnimatorController);
                for (int i = 0; i < parameters.Length; i++)
                {
                    var p = parameters[i];
                    if (p.type == AnimatorControllerParameterType.Float) _controller.SetFloat(p.nameHash, values[i].f);
                    else if (p.type == AnimatorControllerParameterType.Int) _controller.SetInteger(p.nameHash, values[i].i);
                    else if (p.type == AnimatorControllerParameterType.Bool) _controller.SetBool(p.nameHash, values[i].b);
                }
                for (int i = 0; i < states.Length; i++)
                { _controller.SetLayerWeight(i, weights[i]); if (states[i].fullPathHash != 0) _controller.Play(states[i].fullPathHash, i, states[i].normalizedTime); }
                _graph.Connect(_controller, 0, _layers, 0); _layers.SetInputWeight(0, 1);
            }
            _graph.Connect(_skills, 0, _layers, 1); _layers.SetInputWeight(1, 0);
            if (mask != null) _layers.SetLayerMaskFromAvatarMask(1, mask);
            _layers.SetLayerAdditive(1, additive);
            var output = AnimationPlayableOutput.Create(_graph, "Skill", animator); output.SetSourcePlayable(_layers); _graph.Play();
            _clock = animator.gameObject.AddComponent<SkillPlayableClock>(); _clock.Driver = this;
            if (rootMotion)
            {
                _rootMotion = animator.gameObject.AddComponent<SkillRootMotionRelay>();
            }
        }
        public float Play(string clipName, float duration, bool loop) => Play(clipName, duration, loop, 0);
        public float Play(string clipName, float duration, bool loop, float blendIn)
        {
            if (!IsValid) throw new ObjectDisposedException(nameof(PlayableAnimationDriver));
            int index = Array.IndexOf(_names, clipName); if (index < 0 || _clips[index] == null) return 0;
            foreach (var clip in _playing) clip.StartWeight = clip.Weight;
            _current = AnimationClipPlayable.Create(_graph, _clips[index]);
            float actual = duration > 0 ? duration : _clips[index].length;
            _current.SetSpeed(actual > 0 ? _clips[index].length / actual : 1);
            int port = _playing.Count; _skills.SetInputCount(port + 1); _graph.Connect(_current, 0, _skills, port);
            _playing.Add(new ClipState { Clip = _current, Loop = loop });
            _startWeight = _layerWeight;
            _fade = blendIn * (actual / Mathf.Max(.0001f, _clips[index].length)); _elapsed = 0; _stopping = false;
            if (_rootMotion != null)
            {
                if (!_rootMotionActive) _restoreRootMotion = _animator.applyRootMotion;
                _rootMotionActive = true; _animator.applyRootMotion = true;
                _rootMotion.Movement = _animator.GetComponentInParent<ISkillMovementAdapter>();
            }
            Update(0); return actual;
        }
        public void Advance(float normalizedTime) { }
        public void Seek(float normalizedTime)
        {
            if (!IsValid || !_current.IsValid()) return;
            if (_rootMotion != null) _rootMotion.Suppressed = true;
            try { _current.SetTime(_current.GetAnimationClip().length * normalizedTime); _graph.Evaluate(0); }
            finally { if (_rootMotion != null) _rootMotion.Suppressed = false; }
        }
        public void Stop() => Stop(0);
        public void Stop(float blendOut)
        {
            if (_disposed) return;
            RestoreRootMotion();
            if (!IsValid) return;
            _fade = Math.Max(0, blendOut); _elapsed = 0; _stopping = true; _startWeight = _layerWeight;
            Update(0);
        }
        internal void Update(float dt)
        {
            if (!IsValid) return;
            foreach (var state in _playing)
            {
                var clip = state.Clip; var animation = clip.GetAnimationClip(); if (animation == null) continue;
                float length = animation.length;
                if (length > 0 && clip.GetTime() >= length)
                {
                    if (state.Loop) clip.SetTime(clip.GetTime() % length);
                    else { clip.SetTime(length); clip.SetSpeed(0); }
                }
            }
            _elapsed += dt; float t = _fade > 0 ? Mathf.Clamp01(_elapsed / _fade) : 1;
            if (_stopping)
            {
                _layerWeight = _startWeight * (1 - t); _layers.SetInputWeight(1, _layerWeight);
                if (t >= 1) ReleaseClips(false);
            }
            else if (_current.IsValid())
            {
                for (int i = 0; i < _playing.Count; i++)
                {
                    var clip = _playing[i];
                    clip.Weight = i == _playing.Count - 1 ? (_playing.Count == 1 ? 1 : t) : clip.StartWeight * (1 - t);
                    _skills.SetInputWeight(i, clip.Weight);
                }
                _layerWeight = Mathf.Lerp(_startWeight, 1, t); _layers.SetInputWeight(1, _layerWeight);
                if (t >= 1 && _playing.Count > 1) ReleaseClips(true);
            }
        }
        private void ReleaseClips(bool keepCurrent)
        {
            var keep = keepCurrent && _playing.Count > 0 ? _playing[_playing.Count - 1] : null;
            for (int i = 0; i < _playing.Count; i++)
            { _skills.DisconnectInput(i); if (_playing[i] != keep) _graph.DestroyPlayable(_playing[i].Clip); }
            _playing.Clear(); _skills.SetInputCount(keep == null ? 0 : 1);
            if (keep != null) { _playing.Add(keep); _graph.Connect(keep.Clip, 0, _skills, 0); _skills.SetInputWeight(0, 1); }
            else _current = default;
        }
        private void RestoreRootMotion()
        {
            if (_rootMotion != null) _rootMotion.Movement = null;
            if (_rootMotionActive && _animator != null) _animator.applyRootMotion = _restoreRootMotion;
            _rootMotionActive = false;
        }
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            RestoreRootMotion();
            if (_graph.IsValid()) _graph.Destroy();
            if (_rootMotion != null)
            {
                _rootMotion.Movement = null;
                if (Application.isPlaying) UnityEngine.Object.Destroy(_rootMotion); else UnityEngine.Object.DestroyImmediate(_rootMotion);
            }
            if (_clock != null)
            {
                _clock.Driver = null;
                if (Application.isPlaying) UnityEngine.Object.Destroy(_clock); else UnityEngine.Object.DestroyImmediate(_clock);
            }
        }
    }


}
