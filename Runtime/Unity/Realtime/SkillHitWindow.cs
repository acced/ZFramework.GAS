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
        [InspectorName("重复命中间隔 / s"), Min(0f), Tooltip("0 表示单次命中；>0 表示间隔该秒数可再次命中。")] public float hitInterval;

        internal RuntimeTrackDefinition Build(SkillActionBuildContext builder)
        {
            if (effect == null) throw new InvalidOperationException("命中窗口缺少命中效果。");
            if (!float.IsFinite(hitInterval) || hitInterval < 0f) throw new InvalidOperationException("重复命中间隔须为有限非负数。");
            builder.Query(targets); builder.DependsOn(effect);
            return new Window(id, start, end, targets.Build(), offset, Quaternion.Euler(0f, yaw, 0f), effect.Definition, hitInterval);
        }

        private sealed class Window : RuntimeTrackDefinition
        {
            private readonly TargetQueryDefinition _query;
            private readonly Vector3 _offset;
            private readonly Quaternion _rotation;
            private readonly GameplayEffectDefinition _effect;
            private readonly float _hitInterval;

            public Window(string id, float start, float end, TargetQueryDefinition query, Vector3 offset,
                Quaternion rotation, GameplayEffectDefinition effect, float hitInterval) : base(id, start, end)
            {
                _query = query; _offset = offset; _rotation = rotation; _effect = effect; _hitInterval = hitInterval;
            }

            public override IRuntimeTrackHandler Create(SkillExecutionContext context) => new Handler(this, context);

            private sealed class Handler : IRuntimeTrackHandler
            {
                private readonly Window _window;
                private readonly SkillExecutionContext _context;
                private readonly HashSet<AbilitySourceIdentity> _hitOnce;
                private readonly Dictionary<AbilitySourceIdentity, float> _hitTimes;
                private int _pass;

                public Handler(Window window, SkillExecutionContext context)
                {
                    _window = window; _context = context;
                    if (window._hitInterval > 0f) _hitTimes = new Dictionary<AbilitySourceIdentity, float>();
                    else _hitOnce = new HashSet<AbilitySourceIdentity>();
                }

                public void Enter() { _hitOnce?.Clear(); _hitTimes?.Clear(); _pass++; }
                public void Exit() => _pass++;
                public void Cancel() => _pass++;

                public void Sample(float localTime, float normalizedTime)
                {
                    if (localTime >= _window.End - _window.Start) return;
                    SkillExecutionContext c = _context;
                    TargetQueryDefinition query = _window._query;
                    int pass = _pass;

                    Vector3? center = null, forward = null;
                    if (TargetQuery.IsVolume(query.Kind))
                    {
                        Vector3 fwd = c.Forward;
                        Quaternion frame = c.Services.Origin != null ? c.Services.Origin.rotation
                            : fwd.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(fwd) : Quaternion.identity;
                        center = query.Center(c) + frame * _window._offset;
                        forward = frame * _window._rotation * Vector3.forward;
                    }

                    using var hits = c.QueryTargets(query, center, forward, "命中窗口的目标缓冲区已满。");
                    for (int i = 0; i < hits.Count && pass == _pass && c.CanContinue; i++)
                    {
                        AbilitySystemComponent unit = hits.Units[i];
                        if (!hits.IsCurrent(i) || !c.Allows(query, unit) || !hits.IsCurrent(i) || pass != _pass || !c.CanContinue) continue;
                        if (CanHit(unit.SourceIdentity, localTime)) unit.Effects.Apply(c.Effect(_window._effect));
                    }
                }

                private bool CanHit(AbilitySourceIdentity target, float localTime)
                {
                    if (_hitOnce != null) return _hitOnce.Add(target);
                    if (_hitTimes.TryGetValue(target, out float last) && localTime - last < _window._hitInterval) return false;
                    _hitTimes[target] = localTime;
                    return true;
                }
            }
        }
    }
}
