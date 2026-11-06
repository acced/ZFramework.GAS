using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    public sealed class AnimationSectionReferenceAttribute : PropertyAttribute { }
    [Serializable]
    public sealed class AnimationSection
    {
        [SerializeField, HideInInspector] private string id = Guid.NewGuid().ToString("N");
        public string Id => id;
        public string displayName;
        public float start;
        public float end = 1;
        [AnimationSectionReference] public string nextSectionId;
        public float inputBufferStart;
        public float inputBufferEnd = 1;
        public SkillBlend transition;
        internal AnimationSectionDefinition Build() => new AnimationSectionDefinition(id, displayName, start, end, nextSectionId, inputBufferStart, inputBufferEnd, transition);
    }
    public sealed class AnimationSectionDefinition
    {
        public readonly string Id, Name, Next;
        public readonly float Start, End, InputStart, InputEnd;
        public readonly SkillBlend Transition;
        public AnimationSectionDefinition(string id, string name, float start, float end, string next, float inputStart, float inputEnd, SkillBlend transition = default)
        {
            if (transition.duration > end - start) throw new ArgumentOutOfRangeException(nameof(transition), "跳入过渡不能长于目标段。");
            Transition = transition.Snapshot();
            Id = id; Name = name; Start = start; End = end; Next = next; InputStart = inputStart; InputEnd = inputEnd; }
    }
    public interface IRuntimeTrackHandler
    {
        void Enter();
        void Sample(float localTime, float normalizedTime);
        void Exit();
        void Cancel();
    }
    public abstract class RuntimeTrackDefinition
    {
        public string Id { get; }
        public float Start { get; }
        public float End { get; }
        protected RuntimeTrackDefinition(string id, float start, float end) { Id = id; Start = start; End = end; }
        public abstract IRuntimeTrackHandler Create(SkillExecutionContext context);
    }
    [Serializable]
    public abstract class SkillRuntimeTrack
    {
        [SerializeField, HideInInspector] private string id = Guid.NewGuid().ToString("N");
        public string Id => id;
        public float start;
        public float end = 1;
        public abstract RuntimeTrackDefinition Build();
    }
    [Serializable]
    public sealed class CurveSkillTrack : SkillRuntimeTrack
    {
        public string channelId = "MoveSpeed";
        public AnimationCurve curve = AnimationCurve.Linear(0, 1, 1, 1);
        public override RuntimeTrackDefinition Build()
        {
            if (string.IsNullOrEmpty(channelId)) throw new InvalidOperationException("曲线轨缺少通道 ID。");
            return new Definition(Id, start, end, channelId, new AnimationCurve(curve.keys) { preWrapMode = curve.preWrapMode, postWrapMode = curve.postWrapMode });
        }
        private sealed class Definition : RuntimeTrackDefinition
        {
            private readonly string _channel; private readonly AnimationCurve _curve;
            public Definition(string id, float start, float end, string channel, AnimationCurve curve) : base(id, start, end) { _channel = channel; _curve = curve; }
            public override IRuntimeTrackHandler Create(SkillExecutionContext context) => new Handler(context, _channel, _curve);
        }
        private sealed class Handler : IRuntimeTrackHandler
        {
            private readonly SkillExecutionContext _context; private readonly string _channel; private readonly AnimationCurve _curve;
            public Handler(SkillExecutionContext context, string channel, AnimationCurve curve) { _context = context; _channel = channel; _curve = curve; }
            public void Enter() { }
            public void Sample(float localTime, float normalizedTime) => _context.SetChannel(_channel, _curve.Evaluate(normalizedTime));
            public void Exit() => _context.RemoveChannel(_channel);
            public void Cancel() => Exit();
        }
    }
}
