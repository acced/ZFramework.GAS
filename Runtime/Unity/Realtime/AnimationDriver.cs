using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    /// <summary>Presentation only. The ability task owns the gameplay clock.</summary>
    public interface IAnimationDriver : IDisposable
    {
        void Play(AnimationClip clip, float blendIn);
        void Advance(float normalizedTime);
        void Stop(float blendOut);
    }
}
