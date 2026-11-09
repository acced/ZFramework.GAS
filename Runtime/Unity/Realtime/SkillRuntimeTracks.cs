using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
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
}
