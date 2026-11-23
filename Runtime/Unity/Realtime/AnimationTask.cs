using GameplayTags;
using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>按时间轴推进技能动画：统一边界序同步驱动、执行动作、派发 Cue/标签与扩展轨道。</summary>
    public sealed class AnimationTask : GameplayAbilityTask
    {
        private const int KindTagOpen = 0, KindCueOpen = 1, KindTrackEnter = 2, KindMarker = 3, KindTrackExit = 4, KindTagClose = 5, KindCueClose = 6;

        private readonly struct Boundary
        {
            public readonly float Time;
            public readonly int Kind, Index;
            public Boundary(float time, int kind, int index) { Time = time; Kind = kind; Index = index; }
        }

        private readonly SkillAnimationDefinition _definition;
        private readonly Action<AnimationTask, AnimationMarker> _onMarker;
        private readonly bool[] _regionOpen, _trackOpen, _trackSampled;
        private readonly int[] _windowTagsAdded;
        private readonly IRuntimeTrackHandler[] _trackHandlers;
        private readonly Boundary[] _boundaries;

        private IAnimationDriver _driver;
        private SkillExecutionContext _execution, _handlerContext;
        private AbilitySourceIdentity _sourceIdentity;
        private int _nextBoundary, _playVersion;
        private float _cycleTime, _timeScale = 1f;
        private bool _ending;

        internal bool CanReplay => !IsActive && !_ending;
        internal void DetachDriver() => _driver = null;

        public float Duration => _definition.Duration;
        public float ElapsedTime => _cycleTime;
        public SkillAnimationDefinition Definition => _definition;
        public float NormalizedTime => _cycleTime / Duration;
        public override string DiagnosticId => _definition.Clip != null ? _definition.Clip.name : "Timeline";

        public float TimeScale { get => _timeScale; set => _timeScale = value; }

        public AnimationTask(SkillAnimationDefinition definition, IAnimationDriver driver,
            SkillExecutionContext execution, Action<AnimationTask, AnimationMarker> onMarker = null)
        {
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            _onMarker = onMarker;

            int cues = definition.CueRegions.Length, tags = definition.TagWindows.Length;
            int tracks = definition.RuntimeTracks.Count, markers = definition.Markers.Length;
            _regionOpen = cues > 0 ? new bool[cues] : Array.Empty<bool>();
            _windowTagsAdded = tags > 0 ? new int[tags] : Array.Empty<int>();
            _trackHandlers = tracks > 0 ? new IRuntimeTrackHandler[tracks] : Array.Empty<IRuntimeTrackHandler>();
            _trackOpen = tracks > 0 ? new bool[tracks] : Array.Empty<bool>();
            _trackSampled = tracks > 0 ? new bool[tracks] : Array.Empty<bool>();

            int total = markers + (cues + tags + tracks) * 2;
            _boundaries = total > 0 ? new Boundary[total] : Array.Empty<Boundary>();
            int b = 0;
            for (int i = 0; i < tags; i++)
            {
                AnimationTagWindow w = definition.TagWindows[i];
                _boundaries[b++] = new Boundary(w.Start, KindTagOpen, i);
                _boundaries[b++] = new Boundary(w.End < 0f ? definition.Duration : w.End, KindTagClose, i);
            }
            for (int i = 0; i < cues; i++)
            {
                AnimationCueRegion r = definition.CueRegions[i];
                _boundaries[b++] = new Boundary(r.Start, KindCueOpen, i);
                _boundaries[b++] = new Boundary(r.End < 0f ? definition.Duration : r.End, KindCueClose, i);
            }
            for (int i = 0; i < tracks; i++)
            {
                RuntimeTrackDefinition t = definition.RuntimeTracks[i];
                _boundaries[b++] = new Boundary(t.Start, KindTrackEnter, i);
                _boundaries[b++] = new Boundary(t.End, KindTrackExit, i);
            }
            for (int i = 0; i < markers; i++) _boundaries[b++] = new Boundary(definition.Markers[i].Time, KindMarker, i);

            Array.Sort(_boundaries, static (a, c) =>
            {
                int o = a.Time.CompareTo(c.Time);
                if (o == 0) o = a.Kind.CompareTo(c.Kind);
                return o == 0 ? a.Index.CompareTo(c.Index) : o;
            });

            Replay(driver, execution);
        }

        internal void Replay(IAnimationDriver driver, SkillExecutionContext execution)
        {
            _driver = _definition.Clip != null ? driver : null;
            _execution = execution ?? throw new ArgumentNullException(nameof(execution));
            _cycleTime = 0f; _timeScale = 1f; _nextBoundary = 0; _playVersion++;
            if (_trackSampled.Length > 0) Array.Clear(_trackSampled, 0, _trackSampled.Length);
        }

        private bool Alive(int version) => IsActive && version == _playVersion;

        protected override void OnBegin()
        {
            int version = _playVersion;
            _sourceIdentity = Ability.Owner.SourceIdentity;
            _execution.Retain();
            if (!ReferenceEquals(_handlerContext, _execution))
            {
                for (int i = 0; i < _trackHandlers.Length; i++)
                {
                    _trackHandlers[i] = _definition.RuntimeTracks[i].Create(_execution);
                    if (!Alive(version)) return;
                }
                _handlerContext = _execution;
            }
            _driver?.Play(_definition.Clip, _definition.BlendIn);
        }

        protected override void OnTick(float deltaTime)
        {
            float scale = _execution.TimeScale * _timeScale;
            if (scale <= 0f) return;
            int version = _playVersion;
            _cycleTime += deltaTime > 0f ? deltaTime * scale : 0f;

            while (true)
            {
                float head = _cycleTime < Duration ? _cycleTime : Duration;
                if (!ProcessBoundaries(head, version)) return;

                _driver?.Advance(head / Duration);
                if (!Alive(version)) return;

                for (int i = 0; i < _trackHandlers.Length; i++)
                {
                    if (!_trackOpen[i]) continue;
                    RuntimeTrackDefinition track = _definition.RuntimeTracks[i];
                    _trackSampled[i] = true;
                    _trackHandlers[i].Sample(Math.Min(track.End - track.Start, Math.Max(0f, head - track.Start)), Mathf.InverseLerp(track.Start, track.End, head));
                    if (!Alive(version)) return;
                }

                if (_cycleTime < Duration) return;
                if (!_definition.Loop) { Complete(); return; }
                _cycleTime -= Duration;
                if (_trackSampled.Length > 0) Array.Clear(_trackSampled, 0, _trackSampled.Length);
                _nextBoundary = 0;
            }
        }

        protected override void OnEnd(bool cancelled)
        {
            SkillExecutionContext execution = _execution;
            int version = _playVersion;
            _ending = true;
            try
            {
                _driver?.Stop(_definition.BlendOut);
                for (int i = 0; version == _playVersion && i < _windowTagsAdded.Length; i++)
                    while (version == _playVersion && _windowTagsAdded[i] > 0)
                        Ability.Owner.OwnedTags.RemoveTag(_definition.TagWindows[i].Tags[--_windowTagsAdded[i]]);
                for (int i = 0; version == _playVersion && i < _regionOpen.Length; i++)
                    if (_regionOpen[i]) { _regionOpen[i] = false; DispatchCue(_definition.CueRegions[i].CueTag, GameplayCueEvent.Removed); }
                for (int i = 0; version == _playVersion && i < _trackHandlers.Length; i++)
                    if (_trackOpen[i]) { _trackOpen[i] = false; if (cancelled) _trackHandlers[i].Cancel(); else _trackHandlers[i].Exit(); }
            }
            finally { execution.Release(); _ending = false; }
        }

        private bool ProcessBoundaries(float head, int version)
        {
            float lastAdvanced = -1f;
            while (_nextBoundary < _boundaries.Length && _boundaries[_nextBoundary].Time <= head)
            {
                Boundary b = _boundaries[_nextBoundary++];
                switch (b.Kind)
                {
                    case KindTagOpen:
                    {
                        GameplayTagSet tags = _definition.TagWindows[b.Index].Tags;
                        for (int i = _windowTagsAdded[b.Index]; i < tags.Count; i++)
                        {
                            _windowTagsAdded[b.Index]++;
                            Ability.Owner.OwnedTags.AddTag(tags[i]);
                            if (!Alive(version)) return false;
                        }
                        break;
                    }
                    case KindCueOpen:
                        if (!_regionOpen[b.Index]) { _regionOpen[b.Index] = true; DispatchCue(_definition.CueRegions[b.Index].CueTag, GameplayCueEvent.OnActive); }
                        break;
                    case KindTrackEnter:
                        if (!_trackOpen[b.Index])
                        {
                            AdvanceDriverAt(b.Time, ref lastAdvanced);
                            if (!Alive(version)) return false;
                            _trackOpen[b.Index] = true; _trackSampled[b.Index] = false;
                            _trackHandlers[b.Index].Enter();
                        }
                        break;
                    case KindMarker:
                    {
                        AdvanceDriverAt(b.Time, ref lastAdvanced);
                        if (!Alive(version)) return false;
                        AnimationMarker marker = _definition.Markers[b.Index];
                        SkillActionDebug.Point(_execution, marker.Id, marker.Key ?? "标记");
                        if (!Alive(version)) return false;
                        if (marker.Actions != null && marker.Actions.Count > 0)
                        {
                            _execution.Execute(marker.Actions);
                            if (!Alive(version)) return false;
                        }
                        _onMarker?.Invoke(this, marker);
                        break;
                    }
                    case KindTrackExit:
                        if (_trackOpen[b.Index])
                        {
                            RuntimeTrackDefinition track = _definition.RuntimeTracks[b.Index];
                            if (!_trackSampled[b.Index] && track.Start < track.End)
                            {
                                AdvanceDriverAt(track.Start, ref lastAdvanced);
                                if (!Alive(version)) return false;
                                _trackSampled[b.Index] = true;
                                _trackHandlers[b.Index].Sample(0f, 0f);
                                if (!Alive(version)) return false;
                            }
                            _trackOpen[b.Index] = false;
                            _trackHandlers[b.Index].Exit();
                        }
                        break;
                    case KindTagClose:
                        while (_windowTagsAdded[b.Index] > 0)
                        {
                            Ability.Owner.OwnedTags.RemoveTag(_definition.TagWindows[b.Index].Tags[--_windowTagsAdded[b.Index]]);
                            if (!Alive(version)) return false;
                        }
                        break;
                    case KindCueClose:
                        if (_regionOpen[b.Index]) { _regionOpen[b.Index] = false; DispatchCue(_definition.CueRegions[b.Index].CueTag, GameplayCueEvent.Removed); }
                        break;
                }
                if (!Alive(version)) return false;
            }
            return true;
        }

        private void AdvanceDriverAt(float time, ref float lastAdvanced)
        {
            if (_driver != null && time != lastAdvanced) { lastAdvanced = time; _driver.Advance(time / Duration); }
        }

        private void DispatchCue(GameplayTag cueTag, GameplayCueEvent cueEvent)
        {
            AbilitySystemComponent owner = Ability.Owner;
            owner.CueRouter?.Dispatch(new GameplayCueParameters(cueTag, cueEvent, owner, _sourceIdentity, 1));
        }
    }
}
