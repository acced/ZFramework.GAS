using System;
using UnityEngine;
namespace ZFramework.GAS.Unity
{
    [CreateAssetMenu(menuName="ZFramework GAS/骨骼权重遮罩")]
    public sealed class BoneWeightMask : ScriptableObject
    {
        [Serializable] public struct Bone { public string path; [Range(0,1)] public float weight; }
        public Bone[] bones=Array.Empty<Bone>();
        public sealed class Definition
        {
            internal readonly Bone[] Bones;
            internal Definition(Bone[] source)
            {
                Bones=(Bone[])source.Clone();
                var used=new System.Collections.Generic.HashSet<string>();
                foreach(var b in Bones) if(string.IsNullOrWhiteSpace(b.path) || !used.Add(b.path) || !(b.weight>=0 && b.weight<=1))
                    throw new ArgumentException("骨骼权重需要唯一的相对路径，权重范围为 0–1。");
            }
        }
        private void OnValidate()=>DefinitionSource.InvalidateDependents(this);
        public Definition Build()=>new Definition(bones??Array.Empty<Bone>());
    }
    public interface IBoneWeightDriver { void SetNextBoneWeights(BoneWeightMask.Definition mask); }
}
