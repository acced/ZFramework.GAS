using System;
using GameplayTags;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    [Serializable]
    public sealed class AnimationTagWindowAsset
    {
        [SerializeField, HideInInspector] private string id;
        public string Id => id;
        [SerializeField] private float start;
        [SerializeField] private float end = -1;
        [SerializeField] private GameplayTag[] tags = Array.Empty<GameplayTag>();
        public AnimationTagWindow Build() => new(start, end, TagAssetUtility.ToSet(tags));
    }

    public readonly struct AnimationTagWindow
    {
        public readonly float Start, End;
        public readonly GameplayTagSet Tags;
        public AnimationTagWindow(float start, float end, GameplayTagSet tags)
        {
            if (float.IsNaN(start) || float.IsInfinity(start) || start < 0) throw new ArgumentOutOfRangeException(nameof(start));
            if (float.IsNaN(end) || float.IsInfinity(end) || end >= 0 && end < start) throw new ArgumentOutOfRangeException(nameof(end));
            Start = start; End = end < 0 ? -1 : end; Tags = tags;
        }
    }

}
