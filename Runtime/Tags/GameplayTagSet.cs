using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GameplayTags;

namespace ZFramework.GAS
{
    /// <summary>不可变标签集合，构造时拷贝，运行期零分配匹配。</summary>
    public readonly struct GameplayTagSet
    {
        private readonly GameplayTag[] _tags;
        public static readonly GameplayTagSet Empty = default;

        public GameplayTagSet(params GameplayTag[] tags)
            => _tags = tags == null || tags.Length == 0 ? null : (GameplayTag[])tags.Clone();

        public GameplayTagSet(IReadOnlyList<GameplayTag> tags)
        {
            int count = tags?.Count ?? 0;
            if (count == 0) { _tags = null; return; }
            _tags = new GameplayTag[count];
            for (int i = 0; i < count; i++) _tags[i] = tags[i];
        }

        public GameplayTagSet(ReadOnlySpan<GameplayTag> tags)
            => _tags = tags.IsEmpty ? null : tags.ToArray();

        public ReadOnlySpan<GameplayTag> AsSpan() => _tags;
        public int Count => _tags?.Length ?? 0;
        public bool IsEmpty => _tags == null;
        public GameplayTag this[int index] => _tags[index];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasAny(GameplayTagCountContainer container)
        {
            GameplayTag[] tags = _tags;
            if (tags == null) return false;
            for (int i = 0; i < tags.Length; i++)
                if (container.HasTag(tags[i])) return true;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasAll(GameplayTagCountContainer container)
        {
            GameplayTag[] tags = _tags;
            if (tags == null) return true;
            for (int i = 0; i < tags.Length; i++)
                if (!container.HasTag(tags[i])) return false;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MatchesTag(GameplayTag query)
        {
            GameplayTag[] tags = _tags;
            if (tags == null || !query.IsValid) return false;
            for (int i = 0; i < tags.Length; i++)
                if (query == tags[i] || query.IsParentOf(tags[i])) return true;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool HasAnyMatching(in GameplayTagSet query)
        {
            GameplayTag[] tags = _tags, queries = query._tags;
            if (tags == null || queries == null) return false;
            for (int i = 0; i < tags.Length; i++)
                for (int j = 0; j < queries.Length; j++)
                    if (queries[j] == tags[i] || queries[j].IsParentOf(tags[i])) return true;
            return false;
        }
    }
}
