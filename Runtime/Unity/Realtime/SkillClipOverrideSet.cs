using System;
using UnityEngine;
namespace ZFramework.GAS.Unity
{
    [CreateAssetMenu(menuName="ZFramework GAS/技能片段替换表")]
    public sealed class SkillClipOverrideSet : ScriptableObject
    {
        [Serializable] public struct Entry { public string original, replacement; public AnimationClip clip; }
        public Entry[] clips=Array.Empty<Entry>();
        public bool TryGet(string name,out Entry entry, bool acceptsClip=true)
        { if(clips!=null) foreach(var item in clips) if(item.original==name && (acceptsClip && item.clip!=null || !string.IsNullOrEmpty(item.replacement))) {entry=item; return true;} entry=default;return false; }
    }
    public interface IClipOverrideDriver { void SetNextClip(AnimationClip clip); }
}
