using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Animations;
#endif
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Tests
{
    public sealed class AnimationBackendTests
    {
        [UnityTest]
        public IEnumerator LegacyPlaybackStretchesAndRestoresSettings()
        {
            var go = new GameObject("Legacy backend test");
            var clip = new AnimationClip { legacy = true, frameRate = 30 };
            clip.SetCurve("", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Linear(0, 0, .8f, 1));
            var component = go.AddComponent<Animation>(); component.AddClip(clip, "Attack");
            component["Attack"].speed = .7f; component["Attack"].wrapMode = WrapMode.Loop;
            var animator = go.AddComponent<SkillAnimator>(); animator.SetBackend(new LegacyAnimationBackend());
            try
            {
                var driver = animator.Driver;
                Assert.AreEqual(.4f, driver.Play("Attack", .4f, false), .001f);
                Assert.AreEqual(2, component["Attack"].speed, .001f);
                component["Attack"].time = .4f; component.Sample(); Assert.AreEqual(.5f, go.transform.localPosition.x, .02f);
                yield return null;
                driver.Stop(); Assert.AreEqual(.7f, component["Attack"].speed, .001f); Assert.AreEqual(WrapMode.Loop, component["Attack"].wrapMode);
                Assert.AreEqual(0, driver.Play("Missing", 1, false)); driver.Stop(); Assert.AreEqual(.7f, component["Attack"].speed, .001f);
            }
            finally { Object.Destroy(go); Object.Destroy(clip); }
        }
        [UnityTest]
        public IEnumerator AnimatorPlaybackStretchesAndRestoresSpeed()
        {
#if UNITY_EDITOR
            const string directory = "Assets/GASPlayModeFixture";
            AssetDatabase.CreateFolder("Assets", "GASPlayModeFixture");
            var clip = new AnimationClip { frameRate = 30 };
            AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Linear(0, 0, .8f, 1));
            AssetDatabase.CreateAsset(clip, directory + "/Attack.anim");
            var controller = AnimatorController.CreateAnimatorControllerAtPath(directory + "/Character.controller");
            controller.AddMotion(clip); AssetDatabase.SaveAssets();
            var go = new GameObject("Animator backend test"); var animator = go.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller; animator.speed = .7f;
            var skillAnimator = go.AddComponent<SkillAnimator>();
            try
            {
                yield return null;
                var driver = skillAnimator.Driver; Assert.AreEqual(.4f, driver.Play("Base Layer.Attack", .4f, false), .01f);
                Assert.AreEqual(2, animator.speed, .01f); animator.Update(.1f);
                Assert.Greater(animator.GetCurrentAnimatorStateInfo(0).normalizedTime, .1f);
                driver.Stop(); Assert.AreEqual(.7f, animator.speed, .001f);
                Assert.AreEqual(0, driver.Play("Missing", .4f, false)); driver.Stop(); Assert.AreEqual(.7f, animator.speed, .001f);
            }
            finally { Object.DestroyImmediate(go); AssetDatabase.DeleteAsset(directory); }
#else
            yield break;
#endif
        }
        [UnityTest]
        public IEnumerator AnimatorCrossFadesInAndReturnsToPreviousState()
        {
#if UNITY_EDITOR
            const string directory = "Assets/GASPlayModeBlendFixture";
            AssetDatabase.CreateFolder("Assets", "GASPlayModeBlendFixture");
            AnimationClip Clip(string name, float value)
            {
                var clip = new AnimationClip { frameRate = 30 };
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Transform), "m_LocalPosition.x"), AnimationCurve.Constant(0, .8f, value));
                AssetDatabase.CreateAsset(clip, directory + "/" + name + ".anim"); return clip;
            }
            var idle = Clip("Idle", 0); var attack = Clip("Attack", 1);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(directory + "/Character.controller");
            var machine = controller.layers[0].stateMachine;
            machine.defaultState = machine.AddState("Idle"); machine.defaultState.motion = idle;
            machine.AddState("Attack").motion = attack; AssetDatabase.SaveAssets();
            var go = new GameObject("Animator blend test"); var animator = go.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller; animator.speed = .7f;
            var skillAnimator = go.AddComponent<SkillAnimator>();
            try
            {
                yield return null;
                var driver = (IBlendingAnimationDriver)skillAnimator.Driver;
                Assert.AreEqual(.4f, driver.Play("Base Layer.Attack", .4f, false, .2f), .01f);
                // 交叉淡入：仍在过渡中，姿势介于待机与攻击之间，而不是直接跳到攻击。
                Assert.IsTrue(animator.IsInTransition(0));
                Assert.AreEqual(Animator.StringToHash("Base Layer.Attack"), animator.GetNextAnimatorStateInfo(0).fullPathHash);
                animator.Update(.05f);
                Assert.That(go.transform.localPosition.x, Is.InRange(.05f, .95f));
                animator.Update(.2f);
                Assert.IsFalse(animator.IsInTransition(0)); Assert.AreEqual(1, go.transform.localPosition.x, .01f);
                driver.Stop(.2f);
                Assert.AreEqual(.7f, animator.speed, .001f);
                animator.Update(.05f);
                Assert.IsTrue(animator.IsInTransition(0));
                Assert.AreEqual(Animator.StringToHash("Base Layer.Idle"), animator.GetNextAnimatorStateInfo(0).fullPathHash);
                animator.Update(.5f);
                Assert.AreEqual(0, go.transform.localPosition.x, .01f);
                // Restart/seek the pending destination before Mecanim settles, not only the current state.
                driver.Play("Base Layer.Attack",.8f,false,.3f);animator.Update(.08f);Assert.IsTrue(animator.IsInTransition(0));
                driver.Play("Base Layer.Attack",.8f,false,.3f);
                Assert.IsFalse(animator.IsInTransition(0));Assert.That(animator.GetCurrentAnimatorStateInfo(0).normalizedTime,Is.InRange(0f,.03f));
                driver.Stop(.1f);animator.Update(.3f);
                driver.Play("Base Layer.Attack",.8f,false,.3f);animator.Update(.05f);Assert.IsTrue(animator.IsInTransition(0));
                Assert.IsTrue(((IAdvancedBlendingDriver)driver).SeekBlend(.65f,new SkillBlend(.2f)));
                Assert.AreEqual(.65f,animator.GetCurrentAnimatorStateInfo(0).normalizedTime,.02f);
                foreach(float entry in new[]{0f,.3f}){
                    driver.Stop(0);animator.Play("Base Layer.Idle",0,0);animator.Update(0);
                    var asc=new AbilitySystemComponent();var spec=asc.GrantAbility(new GameplayAbilityDefinition());asc.TryActivateAbility(spec);
                    var def=new SkillAnimationDefinition("Base Layer.Attack",.8f,blendInSpec:new SkillBlend(.2f),
                        sections:new[]{new AnimationSectionDefinition("s","s",entry,.8f,null,entry,.8f)});
                    var task=new AnimationTask(def,driver);asc.StartTask(spec,task);animator.Update(.005f);
                    Assert.IsTrue(animator.IsInTransition(0),"Entering the first Section must preserve the pending cross-fade.");
                    Assert.AreEqual(entry/.8f,animator.GetNextAnimatorStateInfo(0).normalizedTime,.03f);
                    animator.Update(.08f);Assert.That(go.transform.localPosition.x,Is.InRange(.05f,.95f));asc.Clear();
                }
                // 混出为 0：驱动只恢复速度，不切换状态（交给状态机自己的转换）。
                driver.Play("Base Layer.Attack", .4f, false, 0); driver.Stop(0); animator.Update(.05f);
                Assert.IsFalse(animator.IsInTransition(0));
                Assert.AreEqual(Animator.StringToHash("Base Layer.Attack"), animator.GetCurrentAnimatorStateInfo(0).fullPathHash);
            }
            finally { Object.DestroyImmediate(go); AssetDatabase.DeleteAsset(directory); }
#else
            yield break;
#endif
        }
        [UnityTest]
        public IEnumerator LegacyCrossFadesInAndBackToPreviousClip()
        {
            var go = new GameObject("Legacy blend test");
            AnimationClip Clip(float value)
            {
                var clip = new AnimationClip { legacy = true, frameRate = 30, wrapMode = WrapMode.Loop };
                clip.SetCurve("", typeof(Transform), "m_LocalPosition.x", AnimationCurve.Constant(0, .8f, value)); return clip;
            }
            var idle = Clip(0); var attack = Clip(1);
            var component = go.AddComponent<Animation>(); component.AddClip(idle, "Idle"); component.AddClip(attack, "Attack");
            component.Play("Idle");
            var animator = go.AddComponent<SkillAnimator>(); animator.SetBackend(new LegacyAnimationBackend());
            try
            {
                yield return null;
                var driver = (IBlendingAnimationDriver)animator.Driver;
                driver.Play("Attack", .4f, true, .3f);
                Assert.IsTrue(component.IsPlaying("Idle"), "交叉淡入期间待机仍在播放");
                yield return new WaitForSeconds(.07f);
                Assert.That(component["Attack"].weight, Is.InRange(.01f, .99f));
                yield return new WaitForSeconds(.3f);
                driver.Stop(.25f);
                Assert.IsTrue(component.IsPlaying("Attack"), "淡出期间攻击仍在播放");
                yield return new WaitForSeconds(.45f);
                Assert.IsFalse(component.IsPlaying("Attack")); Assert.IsTrue(component.IsPlaying("Idle"));
            }
            finally { Object.Destroy(go); Object.Destroy(idle); Object.Destroy(attack); }
        }
        [UnityTest]
        public IEnumerator SpriteBackendSelectsSequenceRestoresSpriteAndAllocatesZeroWhenWarm()
        {
            var texture = new Texture2D(2, 2);
            var original = Sprite.Create(texture, new Rect(0, 0, 1, 1), Vector2.zero);
            var first = Sprite.Create(texture, new Rect(0, 0, 1, 2), Vector2.zero);
            var second = Sprite.Create(texture, new Rect(1, 0, 1, 2), Vector2.zero);
            var go = new GameObject("Sprite backend test"); var renderer = go.AddComponent<SpriteRenderer>(); renderer.sprite = original;
            var animator = go.AddComponent<SkillAnimator>();
            animator.SetBackend(new SpriteSequenceBackend { sequences = new[] { new SpriteSequence { name = "Attack", frameRate = 10, frames = new[] { first, second } } } });
            try
            {
                var driver = animator.Driver; Assert.AreSame(driver, animator.Driver);
                Assert.AreEqual(.2f, driver.Play("Attack", 0, false), .001f); Assert.AreEqual(first, renderer.sprite);
                driver.Advance(1); Assert.AreEqual(second, renderer.sprite); driver.Stop(); Assert.AreEqual(original, renderer.sprite);
                for (int i = 0; i < 100; i++) { driver.Play("Attack", .4f, false); driver.Advance(.5f); driver.Stop(); }
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1000; i++) { driver.Play("Attack", .4f, false); driver.Advance(.5f); driver.Stop(); }
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.AreEqual(0, bytes); Assert.AreEqual(original, renderer.sprite);
                yield return null;
            }
            finally { Object.Destroy(go); Object.Destroy(first); Object.Destroy(second); Object.Destroy(original); Object.Destroy(texture); }
        }
    }
}
