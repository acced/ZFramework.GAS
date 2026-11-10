using System;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Unity
{
    /// <summary>Presentation only. The ability task owns the gameplay clock.</summary>
    public interface IAnimationDriver : IDisposable
    {
        void Play(Object clip, float blendIn);
        void Advance(float normalizedTime);
        void Stop(float blendOut);
    }
}
