using System;
using GameplayTags;

namespace ZFramework.GAS
{
    /// <summary>Logic messages are separate from presentation Cues.</summary>
    public readonly struct GameplayEvent
    {
        public readonly GameplayTag Tag;
        public readonly object Source;
        public readonly SkillParameterValue Payload;
        public readonly EffectEvaluationContext Context;
        public GameplayEvent(GameplayTag tag, object source, SkillParameterValue payload, EffectEvaluationContext context = null)
        { Tag = tag; Source = source; Payload = payload; Context = context; }
    }

    public sealed class GameplayEventChannel
    {
        public event Action<GameplayEvent> Received;
        public void Send(in GameplayEvent message) => Received?.Invoke(message);
        internal void Clear() => Received = null;
    }
}
