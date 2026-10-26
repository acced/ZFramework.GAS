using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>投射物的瞄准方式。</summary>
    public enum ProjectileAim
    {
        /// <summary>飞向一个固定坐标。</summary>
        Position,
        /// <summary>追踪一个目标 Transform；目标消失时沿最后已知位置继续。</summary>
        Unit,
    }

    /// <summary>
    /// 投射物参数。视觉体（<see cref="Visual"/>）的创建与回收由调用方负责——
    /// 库不持有对象池，任务只负责移动它、做命中判定、在结束时通过回调通知调用方回收。
    /// </summary>
    public struct ProjectileConfig
    {
        /// <summary>被移动的物体。</summary>
        public Transform Visual;

        public ProjectileAim Aim;
        /// <summary><see cref="ProjectileAim.Position"/> 时的终点。</summary>
        public Vector3 TargetPosition;
        /// <summary><see cref="ProjectileAim.Unit"/> 时追踪的目标。</summary>
        public Transform TargetUnit;

        public float Speed;
        /// <summary>大于 0 时走抛物线，数值为弧顶相对直线的最大抬升。</summary>
        public float CurveHeight;
        /// <summary>飞行距离上限；小于等于 0 表示不限。</summary>
        public float MaxDistance;

        public float CollisionRadius;
        /// <summary>参与碰撞的层。分辨敌我用层，命中后是否真正生效由效果自身的标签门禁决定。</summary>
        public LayerMask CollisionMask;

        public bool Piercing;
        /// <summary>穿透模式下的最大命中数；小于等于 0 视为不限。</summary>
        public int MaxPierceCount;

        /// <summary>命中时施加给目标的效果。</summary>
        public GameplayEffectDefinition ImpactEffect;
    }

    /// <summary>
    /// 投射物：跨帧飞行、命中时对目标施加效果。
    ///
    /// 作为 <see cref="GameplayAbilityTask"/> 的派生类，它随所属能力一同取消——
    /// 因此发射方应让能力保持激活直到投射物落点，而不是发射后立刻结束能力。
    ///
    /// 命中检测复用 <see cref="TargetSearcher"/>（由调用方传入并可共享），
    /// 因此飞行过程除穿透记录外不产生分配。
    /// </summary>
    public sealed class ProjectileTask : GameplayAbilityTask
    {
        private readonly ProjectileConfig _config;
        private readonly TargetSearcher _searcher;
        private readonly AttributeSet _sourceAttributes;
        private readonly object _source;
        private readonly AbilitySystemComponent _caster;
        private readonly Action<ProjectileTask> _onFinished;
        private readonly AbilitySystemComponent[] _hitBuffer;
        private readonly List<AbilitySystemComponent> _alreadyHit;

        /// <summary>沿路径推进的权威位置，不含弧线抬升。</summary>
        private Vector3 _flatPosition;
        /// <summary>实际渲染与碰撞用的位置 = <see cref="_flatPosition"/> + 弧线抬升。</summary>
        private Vector3 _position;
        private Vector3 _endPosition;
        private float _traveled;

        /// <summary>已命中的目标数。</summary>
        public int HitCount { get; private set; }

        public ProjectileTask(
            in ProjectileConfig config,
            TargetSearcher searcher,
            AttributeSet sourceAttributes,
            object source,
            Action<ProjectileTask> onFinished = null)
        {
            if (config.Visual == null)
            {
                throw new ArgumentException("ProjectileConfig.Visual must be set.", nameof(config));
            }

            if (config.Speed <= 0f)
            {
                throw new ArgumentException("ProjectileConfig.Speed must be positive.", nameof(config));
            }

            // 追踪模式却没给目标，只能退回 TargetPosition（通常是未填的原点）——
            // 这是配置错误，静默飞向原点比抛异常更难查。飞行途中目标消失是另一回事，那由 OnTick 处理。
            if (config.Aim == ProjectileAim.Unit && config.TargetUnit == null)
            {
                throw new ArgumentException(
                    "ProjectileConfig.TargetUnit must be set when Aim is ProjectileAim.Unit.", nameof(config));
            }

            _config = config;
            _searcher = searcher ?? throw new ArgumentNullException(nameof(searcher));
            _sourceAttributes = sourceAttributes;
            _source = source;
            // 发射点通常与发射者重合，不排除自己就会在第一次碰撞检测时打中自己。
            _caster = source as AbilitySystemComponent;
            _onFinished = onFinished;

            _hitBuffer = new AbilitySystemComponent[config.Piercing ? 16 : 1];
            // 只有穿透才需要记录命中过谁；非穿透首次命中即结束。
            _alreadyHit = config.Piercing ? new List<AbilitySystemComponent>() : null;

            _flatPosition = config.Visual.position;
            _position = _flatPosition;
            _endPosition = ResolveEndPosition();
        }

        /// <summary>视觉体，供调用方在结束回调里回收。</summary>
        public Transform Visual => _config.Visual;

        protected override void OnTick(float deltaTime)
        {
            float step = _config.Speed * deltaTime;

            if (_config.Aim == ProjectileAim.Unit && _config.TargetUnit != null)
            {
                // 追踪：终点每帧刷新。目标消失后沿最后已知位置继续。
                _endPosition = _config.TargetUnit.position;
            }

            // 以当前位置为权威向终点推进——不能用「起点到终点的比例」，
            // 否则目标后退时分母变大会让投射物向后跳。
            float remaining = Vector3.Distance(_flatPosition, _endPosition);
            bool arrived = step >= remaining;
            _flatPosition = arrived ? _endPosition : Vector3.MoveTowards(_flatPosition, _endPosition, step);
            _traveled += arrived ? remaining : step;

            _position = _flatPosition;
            if (_config.CurveHeight > 0f)
            {
                // 进度用「已飞 / (已飞 + 剩余)」，单调递增且不受目标移动影响。
                float total = _traveled + (arrived ? 0f : remaining - step);
                float progress = total > 1e-4f ? Mathf.Clamp01(_traveled / total) : 1f;
                _position.y += _config.CurveHeight * 4f * progress * (1f - progress);
            }

            Transform visual = _config.Visual;
            if (visual == null)
            {
                // 视觉体被外部销毁（场景卸载等），静默结束而不是抛异常。
                Complete();
                return;
            }

            Vector3 previous = visual.position;
            visual.position = _position;
            Vector3 delta = _position - previous;
            if (delta.sqrMagnitude > 1e-8f)
            {
                visual.rotation = Quaternion.LookRotation(delta, Vector3.up);
            }

            if (DetectHits())
            {
                return;
            }

            if (_config.MaxDistance > 0f && _traveled >= _config.MaxDistance)
            {
                Complete();
                return;
            }

            if (arrived)
            {
                Complete();
            }
        }

        protected override void OnEnd(bool cancelled) => _onFinished?.Invoke(this);

        private Vector3 ResolveEndPosition()
            => _config.Aim == ProjectileAim.Unit && _config.TargetUnit != null
                ? _config.TargetUnit.position
                : _config.TargetPosition;

        /// <summary>返回 true 表示本次命中已让任务结束。</summary>
        private bool DetectHits()
        {
            if (_config.CollisionRadius <= 0f || _config.ImpactEffect == null)
            {
                return false;
            }

            int found = _searcher.Circle(_position, _config.CollisionRadius, _config.CollisionMask, _hitBuffer, _caster);
            for (int i = 0; i < found; i++)
            {
                AbilitySystemComponent target = _hitBuffer[i];
                if (_alreadyHit != null && _alreadyHit.Contains(target))
                {
                    continue;
                }

                target.Effects.Apply(new GameplayEffectSpec(_config.ImpactEffect, _sourceAttributes, _source));
                HitCount++;

                // 施加效果可能连锁取消发射者的能力（击杀触发的打断等），本任务随之结束。
                // 任务生命周期从属于能力，结束后一发都不能再打。
                if (!IsActive)
                {
                    return true;
                }

                if (!_config.Piercing)
                {
                    Complete();
                    return true;
                }

                _alreadyHit.Add(target);
                if (_config.MaxPierceCount > 0 && HitCount >= _config.MaxPierceCount)
                {
                    Complete();
                    return true;
                }
            }

            return false;
        }
    }
}
