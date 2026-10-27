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
