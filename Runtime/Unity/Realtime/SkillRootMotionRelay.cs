using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ZFramework.GAS.Unity
{
    [AddComponentMenu("")]
    public sealed class SkillRootMotionRelay : MonoBehaviour
    {
        internal ISkillMovementAdapter Movement;
        internal bool Suppressed;
        private void OnAnimatorMove()
        {
            if (Suppressed) return;
            var animator = GetComponent<Animator>();
            if (Movement != null) Movement.ApplyRootMotion(animator.deltaPosition, animator.deltaRotation);
            else if (animator.applyRootMotion) animator.ApplyBuiltinRootMotion();
        }
    }
}

