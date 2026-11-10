using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Experimental.Animations;
using UnityEngine.Playables;

namespace ZFramework.GAS.Unity
{
    /// <summary>A single skill overlay. The existing Animator controller remains the base pose.</summary>
    public sealed class PlayableAnimationDriver : IAnimationDriver
    {
        private readonly PlayableGraph _graph;
        private readonly AnimationMixerPlayable _mixer;
        private readonly AnimationPlayableOutput _output;
        private readonly Fade _fade;
        private AnimationClipPlayable _current, _previous;
        private float _length;

        public PlayableAnimationDriver(Animator animator)
        {
            _graph = PlayableGraph.Create("GAS Timeline");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _mixer = AnimationMixerPlayable.Create(_graph, 2);
            var control = ScriptPlayable<Fade>.Create(_graph, 1);
            _graph.Connect(_mixer, 0, control, 0);
            control.SetInputWeight(0, 1);
            _output = AnimationPlayableOutput.Create(_graph, "Skill", animator);
            _output.SetSourcePlayable(control);
            _output.SetAnimationStreamSource(AnimationStreamSource.PreviousInputs);
            _output.SetSortingOrder(100);
            _output.SetWeight(0);
            _fade = control.GetBehaviour();
            _fade.Mixer = _mixer;
            _fade.Output = _output;
            _graph.Play();
        }

        public void Play(Object clip, float blendIn)
        {
            var animation = (AnimationClip)clip;
            if (_previous.IsValid()) _graph.DestroyPlayable(_previous);
            if (_current.IsValid())
            {
                _mixer.DisconnectInput(1);
                _previous = _current;
                _graph.Connect(_previous, 0, _mixer, 0);
            }
            _current = AnimationClipPlayable.Create(_graph, animation);
            _current.SetSpeed(0);
            _current.SetApplyFootIK(false);
            _graph.Connect(_current, 0, _mixer, 1);
            _length = animation.length;
            _fade.Begin(1, blendIn, _previous.IsValid() && _output.GetWeight() > 0);
        }

        public void Advance(float normalizedTime) => _current.SetTime(normalizedTime * _length);
        public void Stop(float blendOut) => _fade.Begin(0, blendOut, false);
        public void Dispose() { if (_graph.IsValid()) _graph.Destroy(); }

        private sealed class Fade : PlayableBehaviour
        {
            public AnimationMixerPlayable Mixer;
            public AnimationPlayableOutput Output;
            private float _from, _to, _duration, _elapsed;
            private bool _crossFade;
            public void Begin(float target, float duration, bool crossFade)
            {
                _from = Output.GetWeight(); _to = target; _duration = duration; _elapsed = 0;
                _crossFade = crossFade;
                Apply(duration == 0 ? 1 : 0);
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
                if (_to == 0) return;
                Mixer.SetInputWeight(0, _crossFade ? 1 - t : 0);
                Mixer.SetInputWeight(1, _crossFade ? t : 1);
            }
        }
    }
}
