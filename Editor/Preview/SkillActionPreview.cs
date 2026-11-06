using System;
using System.Collections.Generic;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    /// <summary>Editor-only geometric projections. Register once per domain from InitializeOnLoadMethod.</summary>
    public static class SkillActionPreview
    {
        private static readonly Dictionary<Type, Action<SkillAction, PreviewFrame>> Handlers = new();
        static SkillActionPreview()
        {
            Register<ApplyEffectAction>((action, frame) => { if (frame.AtImpact) frame.Mark(action.targets); });
            Register<WaitSkillAction>((action, frame) => {
                if (action.kind == SkillWaitKind.Delay) frame.Visit(action.onSuccess, action.timeout.Resolve(frame.Values));
            });
        }
        public static void Register<T>(Action<T, PreviewFrame> sample) where T : SkillAction =>
            Handlers.Add(typeof(T), (action, frame) => sample((T)action, frame));
        internal static bool TryGet(Type type, out Action<SkillAction, PreviewFrame> sample) =>
            Handlers.TryGetValue(type, out sample);
    }

    /// <summary>A synchronous view of one action in caster-local space. Do not retain this frame.</summary>
    public readonly struct PreviewFrame
    {
        public readonly EffectEvaluationContext Values;
        public readonly float Age;
        public bool AtImpact => Age >= 0 && Age < SkillPreviewSimulation.ImpactPulse;
        private readonly Func<SkillSpawnPosition, Vector3?> _origin;
        private readonly Action<TargetQuery> _mark;
        private readonly Action<SkillAction[], float, Vector3?> _visit;
        internal PreviewFrame(EffectEvaluationContext values, float age, Func<SkillSpawnPosition, Vector3?> origin,
            Action<TargetQuery> mark, Action<SkillAction[], float, Vector3?> visit)
        { Values=values; Age=age; _origin=origin; _mark=mark; _visit=visit; }
        public Vector3? Origin(SkillSpawnPosition origin) => _origin(origin);
        public void Mark(TargetQuery query) => _mark(query);
        /// <summary>Project a branch after a relative delay. A ground point clears the selected unit for this branch only.</summary>
        public void Visit(SkillAction[] branch, float delay = 0, Vector3? groundPoint = null) => _visit(branch,delay,groundPoint);
    }
}
