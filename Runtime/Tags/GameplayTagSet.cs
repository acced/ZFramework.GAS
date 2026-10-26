using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GameplayTags;

namespace ZFramework.GAS
{
    /// <summary>
    /// 不可变标签集合，用于配置侧的「需要 / 阻止 / 授予」等标签清单。
    /// 入参必须是当前注册表已解析的非 None 标签；构造时复制，之后不受外部数组改动影响。
    /// </summary>
    public readonly struct GameplayTagSet
    {
        private readonly GameplayTag[] _tags;

        public static readonly GameplayTagSet Empty = default;

        public GameplayTagSet(params GameplayTag[] tags)
        {
            if (tags == null || tags.Length == 0)
            {
                _tags = null;
                return;
            }

            _tags = new GameplayTag[tags.Length];
            Array.Copy(tags, _tags, tags.Length);
        }

        public GameplayTagSet(IReadOnlyList<GameplayTag> tags)
        {
            int count = tags?.Count ?? 0;
            if (count == 0)
            {
                _tags = null;
                return;
            }

            _tags = new GameplayTag[count];
            for (int i = 0; i < count; i++)
            {
                _tags[i] = tags[i];
            }
        }

        /// <summary>复制指定范围内已解析的标签，不持有输入存储。</summary>
        public GameplayTagSet(ReadOnlySpan<GameplayTag> tags) => _tags = tags.ToArray();

        internal ReadOnlySpan<GameplayTag> AsSpan() => _tags;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void AddTo(GameplayTagCountContainer container)
        {
            for (int i = 0; i < Count; i++)
                container.AddTag(_tags[i]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void RemoveFrom(GameplayTagCountContainer container)
        {
            for (int i = 0; i < Count; i++)
                container.RemoveTag(_tags[i]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool HasAny(GameplayTagCountContainer container)
        {
            for (int i = 0; i < Count; i++)
                if (container.HasTag(_tags[i]))
                    return true;
            return false;
        }

        public int Count => _tags?.Length ?? 0;

        public bool IsEmpty => _tags == null || _tags.Length == 0;

        public GameplayTag this[int index] => _tags[index];

        /// <summary>
        /// 本集合中是否存在某个标签「拥有」<paramref name="query"/> 中的任一标签（层级匹配）。
        /// 例：本集合含 <c>"Debuff.Poison"</c>，查询 <c>"Debuff"</c> 为真。
        /// </summary>
        public bool HasAnyMatching(in GameplayTagSet query)
        {
            for (int i = 0; i < Count; i++)
            {
                for (int j = 0; j < query.Count; j++)
                {
                    if (query[j] == _tags[i] || query[j].IsParentOf(_tags[i]))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
