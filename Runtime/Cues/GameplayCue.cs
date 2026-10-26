using GameplayTags;
using System;
using System.Collections.Generic;

namespace ZFramework.GAS
{
    /// <summary>Cue 的触发时机，对应 UE 的 <c>EGameplayCueEvent</c>。</summary>
    public enum GameplayCueEvent
    {
        /// <summary>瞬时结算：Instant 效果应用、或周期效果的一次跳动。</summary>
        Executed,
        /// <summary>持续效果开始生效。</summary>
        OnActive,
        /// <summary>持续效果被移除。</summary>
        Removed,
    }

    public readonly struct GameplayCueParameters
    {
        public readonly GameplayTag CueTag;
        public readonly GameplayCueEvent Event;
        /// <summary>Cue 作用的实体，由 <see cref="AbilitySystemComponent"/> 在构造时提供；可为 null。</summary>
        public readonly object Target;
        /// <summary>效果来源；可为 null。</summary>
        public readonly object Source;
        public readonly int StackCount;

        public GameplayCueParameters(GameplayTag cueTag, GameplayCueEvent cueEvent, object target, object source, int stackCount)
        {
            CueTag = cueTag;
            Event = cueEvent;
            Target = target;
            Source = source;
            StackCount = stackCount;
        }
    }

    /// <summary>
    /// 按标签把 Cue 事件路由给处理器。库只负责路由，**不做任何表现**——
    /// 播粒子、放音效、飘字都由使用方在处理器里实现。
    ///
    /// 不是单例：由宿主创建（通常一场战斗一个），构造 <see cref="AbilitySystemComponent"/> 时传入。
    /// 因此不存在跨场景悬挂的全局状态。
    ///
    /// 标签匹配是层级的：注册在 <c>"Cue.Damage"</c> 上的处理器会收到 <c>"Cue.Damage.Fire"</c> 的事件。
    /// </summary>
    public sealed class GameplayCueRouter
    {
        private readonly List<GameplayTag> _tags = new();
        private readonly List<Action<GameplayCueParameters>> _handlers = new();
        /// <summary>派发嵌套深度。用计数而非布尔：Cue 处理器施加的效果会再派发 Cue，
        /// 内层退出时若把布尔复位，外层剩下的注销就又会走真删除、跳过后面的处理器。</summary>
        private int _dispatchDepth;
        private bool _needsCompaction;

        public int HandlerCount => _handlers.Count;

        public void Register(GameplayTag cueTag, Action<GameplayCueParameters> handler)
        {
            if (!cueTag.IsValid)
            {
                throw new ArgumentException("Cue tag is not valid.", nameof(cueTag));
            }

            _tags.Add(cueTag);
            _handlers.Add(handler ?? throw new ArgumentNullException(nameof(handler)));
        }

        public bool Unregister(GameplayTag cueTag, Action<GameplayCueParameters> handler)
        {
            for (int i = 0; i < _handlers.Count; i++)
            {
                if (_tags[i] != cueTag || _handlers[i] != handler)
                {
                    continue;
                }

                if (_dispatchDepth > 0)
                {
                    // 派发途中直接删会让后面的处理器整个被跳过（索引前移）。
                    // 一次性 Cue 在自己的处理器里注销自己是常见用法，必须扛住。
                    _handlers[i] = null;
                    _needsCompaction = true;
                }
                else
                {
                    _tags.RemoveAt(i);
                    _handlers.RemoveAt(i);
                }

                return true;
            }

            return false;
        }

        /// <summary>派发给所有注册标签能匹配 <c>parameters.CueTag</c> 的处理器，返回被调用的处理器数量。</summary>
        public int Dispatch(in GameplayCueParameters parameters)
        {
            int dispatched = 0;
            _dispatchDepth++;
            try
            {
                // 只派发进入时已注册的处理器：派发途中新注册的等下一次，
                // 与效果容器「本帧只推进进入时已存在的效果」一致。
                int count = _handlers.Count;
                for (int i = 0; i < count && i < _handlers.Count; i++)
                {
                    Action<GameplayCueParameters> handler = _handlers[i];
                    if (handler == null || !(_tags[i] == parameters.CueTag || _tags[i].IsParentOf(parameters.CueTag)))
                    {
                        continue;
                    }

                    handler.Invoke(parameters);
                    dispatched++;
                }
            }
            finally
            {
                // 即使处理器抛异常也必须递减，否则此后所有注销都只置空不生效。
                // 压缩只在最外层做——中途压缩会让外层循环的索引错位。
                _dispatchDepth--;
                if (_dispatchDepth == 0)
                {
                    Compact();
                }
            }

            return dispatched;
        }

        public void Clear()
        {
            _tags.Clear();
            _handlers.Clear();
            _needsCompaction = false;
        }

        /// <summary>清掉派发期间置空的槽位。</summary>
        private void Compact()
        {
            if (!_needsCompaction)
            {
                return;
            }

            _needsCompaction = false;
            for (int i = _handlers.Count - 1; i >= 0; i--)
            {
                if (_handlers[i] == null)
                {
                    _handlers.RemoveAt(i);
                    _tags.RemoveAt(i);
                }
            }
        }
    }
}
