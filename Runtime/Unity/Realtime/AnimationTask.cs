using GameplayTags;
using System;

namespace ZFramework.GAS.Unity
{
    /// <summary>
    /// 按时间轴推进一段技能动画：到点施加效果、区间起止派发 Cue、驱动底层动画。
    ///
    /// <b>时钟权威在 gameplay 侧</b>：任务在能力的 tick 上推进，把归一化进度交给
    /// <see cref="IAnimationDriver"/> 采样，驱动不持有第二套播放时钟。
    ///
    /// 与所有 <see cref="GameplayAbilityTask"/> 一样，存活期是所属能力存活期的子集：
    /// 能力被打断时动画立即 <see cref="IAnimationDriver.Stop"/>，已开的 Cue 区间收尾。
    /// </summary>
    public sealed class AnimationTask : GameplayAbilityTask
    {
        private static readonly bool[] NoRegions = Array.Empty<bool>();

        private readonly SkillAnimationDefinition _definition;
        private IAnimationDriver _driver;
        private readonly Action<AnimationTask, AnimationMarker> _onMarker;
        private readonly bool[] _regionOpen;
        private readonly int[] _windowTagsAdded;
        private SkillExecutionContext _execution;
        private AbilitySourceIdentity _sourceIdentity;
        private readonly Boundary[] _boundaries;
        private int _nextBoundary;
        private readonly struct Boundary
        {
            public readonly float Time;
            public readonly int Kind, Index;
            public Boundary(float time, int kind, int index) { Time = time; Kind = kind; Index = index; }
        }

        private float _cycleTime;
        private int _playVersion;
        private bool _ending;
        private SkillExecutionContext _handlerContext;
        internal bool CanReplay => !IsActive && !_ending;
        internal void DetachDriver() => _driver = null;
        private readonly IRuntimeTrackHandler[] _trackHandlers;
        private readonly bool[] _trackOpen;
        private readonly bool[] _trackDone;
        /// <summary>Prepare one cast; playback starts when the task is attached to its ability.</summary>
        public AnimationTask(
            SkillAnimationDefinition definition,
            IAnimationDriver driver,
            SkillExecutionContext execution,
            Action<AnimationTask, AnimationMarker> onMarker = null)
        {
            _definition = definition;
            _onMarker = onMarker;
            _regionOpen = definition.CueRegions.Length > 0 ? new bool[definition.CueRegions.Length] : NoRegions;
            _windowTagsAdded = new int[definition.TagWindows.Length];
            _trackHandlers = new IRuntimeTrackHandler[definition.RuntimeTracks.Count]; _trackOpen = new bool[_trackHandlers.Length]; _trackDone = new bool[_trackHandlers.Length];
            _boundaries = new Boundary[definition.Markers.Length + definition.CueRegions.Length * 2 + definition.TagWindows.Length * 2 + _trackHandlers.Length * 2];
            int b = 0;
            for (int i = 0; i < _trackHandlers.Length; i++)
            { _boundaries[b++] = new Boundary(definition.RuntimeTracks[i].Start, 6, i); _boundaries[b++] = new Boundary(definition.RuntimeTracks[i].End, 7, i); }
            for (int i = 0; i < definition.TagWindows.Length; i++)
            {
                var w = definition.TagWindows[i];
                _boundaries[b++] = new Boundary(w.Start, 0, i);
                _boundaries[b++] = new Boundary(w.End < 0 ? definition.Duration : w.End, 4, i);
            }
            for (int i = 0; i < definition.CueRegions.Length; i++)
            {
                var r = definition.CueRegions[i];
                _boundaries[b++] = new Boundary(r.Start, 1, i);
                _boundaries[b++] = new Boundary(r.End < 0 ? definition.Duration : r.End, 5, i);
            }
            for (int i = 0; i < definition.Markers.Length; i++) _boundaries[b++] = new Boundary(definition.Markers[i].Time, 2, i);
            Array.Sort(_boundaries, static (a, c) =>
            { int order = a.Time.CompareTo(c.Time); if (order == 0) order = a.Kind.CompareTo(c.Kind); return order == 0 ? a.Index.CompareTo(c.Index) : order; });

            Replay(driver, execution);
        }

        // SkillRunner leases one timeline per idle cast context. Detached work keeps its own context.
        internal void Replay(IAnimationDriver driver, SkillExecutionContext execution)
        {
            _driver = _definition.Clip != null ? driver : null;
            _execution = execution; _cycleTime = 0; _nextBoundary = 0; _playVersion++;
            Array.Clear(_trackDone, 0, _trackDone.Length);
        }

        /// <summary>本次播放的实际时长（秒）。</summary>
        public float Duration => _definition.Duration;

        /// <summary>本次任务实际持有的定义；资产在编辑器中修改后该实例仍然有效。</summary>
        public SkillAnimationDefinition Definition => _definition;

        /// <summary>当前这一圈的归一化进度（0~1）。</summary>
        public float NormalizedTime => _cycleTime / Duration;
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
                    if (!IsActive || version != _playVersion) return;
                }
                _handlerContext = _execution;
            }
            _driver?.Play(_definition.Clip, _definition.BlendIn);
        }
        protected override void OnTick(float deltaTime) => Tick(deltaTime);
        private void Tick(float deltaTime)
        {
            int version = _playVersion;
            _cycleTime += deltaTime;

            while (true)
            {
                // 单帧跨过整圈时，本轮只结算到圈末，余下的时间留给下一圈——
                // 否则同一圈里靠后的标记点会被跳过。
                float head = _cycleTime < Duration ? _cycleTime : Duration;

                if (!ProcessBoundaries(head))
                {
                    // 效果连锁取消了所属能力，任务已随之结束。
                    return;
                }

                _driver?.Advance(head / Duration);
                if (!IsActive || version != _playVersion) return;
                SampleTracks(head);
                if (!IsActive || version != _playVersion) return;

                if (_cycleTime < Duration)
                {
                    return;
                }

                if (!_definition.Loop)
                {
                    Complete();
                    return;
                }

                // 循环动画不自行结束，由能力结束它。每圈重新触发标记点与区间。
                _cycleTime -= Duration;
                Array.Clear(_trackDone, 0, _trackDone.Length);
                _nextBoundary = 0;
            }
        }

        private void SampleTracks(float time)
        {
            int version = _playVersion;
            for (int i = 0; i < _trackHandlers.Length && IsActive; i++)
            {
                var definition = _definition.RuntimeTracks[i];
                if (!_trackOpen[i] && !_trackDone[i] && time >= definition.Start)
                { _trackOpen[i] = true; _trackHandlers[i].Enter(); }
                if (!IsActive || version != _playVersion) return;
                if (!_trackOpen[i] || !IsActive) continue;
                _trackHandlers[i].Sample(Math.Min(definition.End - definition.Start, Math.Max(0, time - definition.Start)), UnityEngine.Mathf.InverseLerp(definition.Start, definition.End, time));
                if (!IsActive || version != _playVersion) return;
                if (time >= definition.End) { _trackOpen[i] = false; _trackDone[i] = true; _trackHandlers[i].Exit(); }
                if (!IsActive || version != _playVersion) return;
            }
        }
        private void CloseTimelineState(bool cancelled)
        {
            int version = _playVersion;
            for (int i = 0; version == _playVersion && i < _windowTagsAdded.Length; i++)
                while (version == _playVersion && _windowTagsAdded[i] > 0)
                    Ability.Owner.OwnedTags.RemoveTag(_definition.TagWindows[i].Tags[--_windowTagsAdded[i]]);
            for (int i = 0; version == _playVersion && i < _regionOpen.Length; i++) if (_regionOpen[i])
            { _regionOpen[i] = false; DispatchCue(_definition.CueRegions[i].CueTag, GameplayCueEvent.Removed); }
            for (int i = 0; version == _playVersion && i < _trackHandlers.Length; i++) if (_trackOpen[i])
            {
                _trackOpen[i] = false;
                if (cancelled) _trackHandlers[i].Cancel(); else _trackHandlers[i].Exit();
            }
        }

        protected override void OnEnd(bool cancelled)
        {
            var execution = _execution;
            _ending = true;
            try
            {
                _driver?.Stop(_definition.BlendOut);
                CloseTimelineState(cancelled);
            }
            finally { execution.Release(); _ending = false; }
        }

        // Merge boundaries once at construction. Large ticks still observe the correct window state
        // at every marker; same-time opens precede events and closes follow events.
        private bool ProcessBoundaries(float head)
        {
            int version = _playVersion;
            while (_nextBoundary < _boundaries.Length && _boundaries[_nextBoundary].Time <= head)
            {
                Boundary b = _boundaries[_nextBoundary++];
                SampleTracks(b.Time);
                if (!IsActive || version != _playVersion) return false;
                switch (b.Kind)
                {
                    case 0:
                        var tags = _definition.TagWindows[b.Index].Tags;
                        for (int i = _windowTagsAdded[b.Index]; i < tags.Count; i++)
                        {
                            // Commit ownership before notification: cancellation can reenter OnEnd.
                            _windowTagsAdded[b.Index]++;
                            Ability.Owner.OwnedTags.AddTag(tags[i]);
                            if (!IsActive || version != _playVersion) return false;
                        }
                        break;
                    case 1:
                        if (!_regionOpen[b.Index])
                        {
                            _regionOpen[b.Index] = true;
                            DispatchCue(_definition.CueRegions[b.Index].CueTag, GameplayCueEvent.OnActive);
                        }
                        break;
                    case 2:
                        var marker = _definition.Markers[b.Index];
                        SkillActionDebug.Point(_execution, marker.Id, marker.Key ?? "标记");
                        if (!IsActive || version != _playVersion) return false;
                        if (marker.Actions != null && marker.Actions.Count > 0)
                        {
                            _execution.Execute(marker.Actions);
                            if (!IsActive || version != _playVersion) return false;
                        }
                        _onMarker?.Invoke(this, marker);
                        break;
                    case 4:
                        while (_windowTagsAdded[b.Index] > 0)
                        {
                            int index = --_windowTagsAdded[b.Index];
                            Ability.Owner.OwnedTags.RemoveTag(_definition.TagWindows[b.Index].Tags[index]);
                            if (!IsActive || version != _playVersion) return false;
                        }
                        break;
                    case 5:
                        if (_regionOpen[b.Index])
                        {
                            _regionOpen[b.Index] = false;
                            DispatchCue(_definition.CueRegions[b.Index].CueTag, GameplayCueEvent.Removed);
                        }
                        break;
                }
                if (!IsActive || version != _playVersion) return false;
            }
            return true;
        }

        private void DispatchCue(GameplayTag cueTag, GameplayCueEvent cueEvent)
        {
            AbilitySystemComponent owner = Ability.Owner;
            owner.CueRouter?.Dispatch(new GameplayCueParameters(cueTag, cueEvent, owner, _sourceIdentity, 1));
        }
    }
}
