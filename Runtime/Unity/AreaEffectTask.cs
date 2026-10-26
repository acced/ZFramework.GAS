using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>
    /// 区域效果参数。区域本身只是坐标 + 半径的逻辑存在，
    /// 视觉表现由使用方经 Cue 自行处理——与库其他部分一致，规则与表现分离。
    /// </summary>
    public struct AreaEffectConfig
    {
        public Vector3 Position;
        public float Radius;
        /// <summary>参与检测的层。命中后是否真正生效仍由效果自身的标签门禁决定。</summary>
        public LayerMask CollisionMask;

        /// <summary>存在时长；小于等于 0 表示持续到所属能力结束。</summary>
        public float Duration;

        /// <summary>单位进入区域时施加。</summary>
        public GameplayEffectDefinition EnterEffect;

        /// <summary>
        /// 离开区域（或区域消失）时是否移除 <see cref="EnterEffect"/> 施加的效果。
        /// 光环类区域置 true；踩中即触发的陷阱类置 false。
        /// </summary>
        public bool RemoveOnExit;
    }

    /// <summary>
    /// 驻留区域：持续检测范围内的单位，进入时施加效果，离开时按配置移除。
    ///
    /// 每帧对当前占用者做一次差集，因此「进入」与「离开」各自只触发一次。
    /// 区域结束（时长到期或所属能力结束）时，所有仍在区域内的单位都按离开处理，
    /// 不会留下无人移除的光环。
    /// </summary>
    public sealed class AreaEffectTask : GameplayAbilityTask
    {
        private readonly AreaEffectConfig _config;
        private readonly TargetSearcher _searcher;
        private readonly AttributeSet _sourceAttributes;
        private readonly object _source;
        private readonly AbilitySystemComponent _caster;
        private readonly AbilitySystemComponent[] _buffer;
        private readonly List<AbilitySystemComponent> _occupants = new();
        private readonly List<AbilitySystemComponent> _current = new();

        private float _elapsed;

        /// <summary>当前在区域内的单位数。</summary>
        public int OccupantCount => _occupants.Count;

        public AreaEffectTask(
            in AreaEffectConfig config,
            TargetSearcher searcher,
            AttributeSet sourceAttributes,
            object source,
            int maxOccupants = 32)
        {
            if (config.Radius <= 0f)
            {
                throw new ArgumentException("AreaEffectConfig.Radius must be positive.", nameof(config));
            }

            if (config.EnterEffect == null)
            {
                throw new ArgumentException("AreaEffectConfig.EnterEffect must be set.", nameof(config));
            }

            _config = config;
            _searcher = searcher ?? throw new ArgumentNullException(nameof(searcher));
            _sourceAttributes = sourceAttributes;
            _source = source;
            _caster = source as AbilitySystemComponent;
            _buffer = new AbilitySystemComponent[maxOccupants];
        }

        protected override void OnTick(float deltaTime)
        {
            _elapsed += deltaTime;

            int found = _searcher.Circle(_config.Position, _config.Radius, _config.CollisionMask, _buffer, _caster);

            _current.Clear();
            for (int i = 0; i < found; i++)
            {
                _current.Add(_buffer[i]);
            }

            // 缓冲区被填满意味着结果可能被截断——此时「不在 _current 里」不足以断定某人离开了区域，
            // 只是没被这次查询取到。据不完整的结果移除光环会造成每帧反复施加 / 移除。
            // 宁可少移除也不误移除；真正的解法是把 maxOccupants 配够。
            bool truncated = found >= _buffer.Length;

            if (!truncated)
            {
                // 离开：曾在区域内、本帧不在了。倒序移除以便就地删元素。
                for (int i = _occupants.Count - 1; i >= 0; i--)
                {
                    AbilitySystemComponent occupant = _occupants[i];
                    if (_current.Contains(occupant))
                    {
                        continue;
                    }

                    _occupants.RemoveAt(i);
                    HandleExit(occupant);

                    // 移除效果可能触发 Removed 子效果并连锁取消所属能力；
                    // 那时 OnEnd 已把剩余占用者处理干净，这里必须立刻停手。
                    if (!IsActive)
                    {
                        return;
                    }
                }
            }

            // 进入：本帧在区域内、之前不在。
            for (int i = 0; i < _current.Count; i++)
            {
                AbilitySystemComponent entrant = _current[i];
                if (_occupants.Contains(entrant))
                {
                    continue;
                }

                _occupants.Add(entrant);
                entrant.Effects.Apply(new GameplayEffectSpec(_config.EnterEffect, _sourceAttributes, _source));

                // 同上：区域已结束就不能再拉人进来。
                // 此刻 OnEnd 已经清空过占用者列表，之后加进去的人永远不会被移除光环。
                if (!IsActive)
                {
                    return;
                }
            }

            if (_config.Duration > 0f && _elapsed >= _config.Duration)
            {
                Complete();
            }
        }

        /// <summary>区域消失时，仍在其中的单位一律按离开处理。</summary>
        protected override void OnEnd(bool cancelled)
        {
            for (int i = 0; i < _occupants.Count; i++)
            {
                HandleExit(_occupants[i]);
            }

            _occupants.Clear();
        }

        private void HandleExit(AbilitySystemComponent unit)
        {
            if (!_config.RemoveOnExit)
            {
                return;
            }

            // 按 (定义 + 来源) 找：只摘自己挂上去的那个。
            // 两个同类光环重叠时，只按定义找会摘掉对方的效果，留下自己的永不移除。
            ActiveGameplayEffectHandle handle = unit.Effects.FindFrom(_config.EnterEffect, _source);
            if (handle.IsValid)
            {
                unit.Effects.Remove(handle);
            }
        }
    }
}
