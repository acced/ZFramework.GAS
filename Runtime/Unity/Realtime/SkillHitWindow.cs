using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [Serializable]
    public sealed class SkillHitWindow
    {
        [SerializeField, HideInInspector] private string id = Guid.NewGuid().ToString("N");
        [InspectorName("开始 / s")] public float start;
        [InspectorName("结束 / s")] public float end = .25f;
        [InspectorName("命中效果")] public GameplayEffectAsset effect;
        [InspectorName("命中来源")] public TargetQuery targets = new();
        [InspectorName("局部偏移")] public Vector3 offset;
        [InspectorName("朝向偏角 / °"), Tooltip("0 为角色前方（+Z），90 为角色右侧（+X）。")] public float yaw;

        internal RuntimeTrackDefinition Build(SkillActionBuildContext builder)
        {
            if (effect == null) throw new InvalidOperationException("命中窗口缺少命中效果。");
            builder.Query(targets);
            builder.DependsOn(effect);
            return new Window(id, start, end, targets.Build(), offset, Quaternion.Euler(0, yaw, 0), effect.Definition);
        }

        private sealed class Window : RuntimeTrackDefinition
        {
            private readonly TargetQueryDefinition _query;
            private readonly Vector3 _offset;
            private readonly Quaternion _rotation;
            private readonly GameplayEffectDefinition _effect;
            public Window(string id, float start, float end, TargetQueryDefinition query, Vector3 offset, Quaternion rotation, GameplayEffectDefinition effect)
                : base(id, start, end) { _query = query; _offset = offset; _rotation = rotation; _effect = effect; }
            public override IRuntimeTrackHandler Create(SkillExecutionContext context) => new Handler(this, context);

            private sealed class Handler : IRuntimeTrackHandler
            {
                private readonly Window _window;
                private readonly SkillExecutionContext _context;
                private readonly HashSet<AbilitySourceIdentity> _hit = new();
                private int _pass;
                public Handler(Window window, SkillExecutionContext context) { _window = window; _context = context; }
                public void Enter() { _hit.Clear(); _pass++; }
                public void Exit() => _pass++;
                public void Cancel() => Exit();
                public void Sample(float localTime, float normalizedTime)
                {
                    if (localTime >= _window.End - _window.Start) return; // The right edge closes the window.
                    var c = _context;
                    var query = _window._query;
                    var buffer = c.RentBuffer();
                    int pass = _pass;
                    try
                    {
                        if (TargetQuery.IsVolume(query.Kind))
                        {
                            var frame = c.Services.Origin != null ? c.Services.Origin.rotation : Quaternion.LookRotation(c.Forward);
                            c.Services.Searcher.QueryAt(query, c, buffer, query.Center(c) + frame * _window._offset, frame * _window._rotation * Vector3.forward);
                        }
                        else c.Services.Searcher.Query(query, c, buffer);
                        GasAssert.That(!buffer.Saturated, "命中窗口的目标缓冲区已满。");
                        for (int i = 0; i < buffer.Count && pass == _pass && c.CanContinue; i++)
                        {
                            var unit = buffer.Units[i];
                            if (buffer.IsCurrent(i) && c.Allows(query, unit) && _hit.Add(unit.SourceIdentity))
                                unit.Effects.Apply(c.Effect(_window._effect));
                        }
                    }
                    finally { c.ReturnBuffer(buffer); }
                }
            }
        }
    }
}
