#if UNITY_EDITOR
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Tests
{
    public sealed class PlayableAnimationTests
    {
        [UnityTest]
        public IEnumerator SkillClipSamplesGameplayTimeAndReturnsToAnimatorController()
        {
            var go = new GameObject("Skill animation test");
            var bone = new GameObject("Bone").transform; bone.SetParent(go.transform, false);
            var animator = go.AddComponent<Animator>(); animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            var idle = new AnimationClip { name = "Idle" };
            var skill = new AnimationClip { name = "Skill" };
            var binding = EditorCurveBinding.FloatCurve("Bone", typeof(Transform), "m_LocalPosition.x");
            AnimationUtility.SetEditorCurve(idle, binding, AnimationCurve.Linear(0, 0, 1, 0));
            AnimationUtility.SetEditorCurve(skill, binding, AnimationCurve.Linear(0, 0, 1, 2));
            var controller = new AnimatorController(); controller.AddLayer("Base");
            var machine = controller.layers[0].stateMachine;
            var state = machine.AddState("Idle"); state.motion = idle; machine.defaultState = state;
            animator.runtimeAnimatorController = controller;
            var driver = new PlayableAnimationDriver(animator);
            try
            {
                yield return null;
                driver.Play(skill, 0); driver.Advance(.5f);
                yield return null; yield return null;
                Assert.AreEqual(1, bone.localPosition.x, .02f);
                driver.Stop(0);
                yield return null; yield return null;
                Assert.AreEqual(0, bone.localPosition.x, .02f);
                driver.Play(skill, 0); driver.Advance(.75f);
                yield return null; yield return null;
                Assert.AreEqual(1.5f, bone.localPosition.x, .02f);
            }
            finally
            {
                driver.Dispose(); Object.Destroy(go); Object.Destroy(state); Object.Destroy(machine);
                Object.Destroy(controller); Object.Destroy(idle); Object.Destroy(skill);
            }
        }
    }
}
#endif
