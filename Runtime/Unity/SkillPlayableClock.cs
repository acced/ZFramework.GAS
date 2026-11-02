using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ZFramework.GAS.Unity
{
    [AddComponentMenu("")]
    public sealed class SkillPlayableClock : MonoBehaviour
    {
        internal PlayableAnimationDriver Driver;
        private void Update() => Driver?.Update(Time.deltaTime);
        private void OnDestroy() { var driver = Driver; Driver = null; driver?.Dispose(); }
    }
}

