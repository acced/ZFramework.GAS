using System;
using GameplayTags;

namespace ZFramework.GAS.Unity
{
    internal static class TagAssetUtility
    {
        /// <summary>在加载边界解析并跳过空行、未注册名称；保留序列化字段原样。</summary>
        internal static GameplayTagSet ToSet(GameplayTag[] tags)
        {
            if (tags == null || tags.Length == 0)
                return GameplayTagSet.Empty;

            var resolved = new GameplayTag[tags.Length];
            int count = 0;
            for (int i = 0; i < tags.Length; i++)
            {
                GameplayTag tag = tags[i].Resolve();
                if (tag.IsValid)
                    resolved[count++] = tag;
            }
            return new GameplayTagSet(resolved.AsSpan(0, count));
        }
    }
}
