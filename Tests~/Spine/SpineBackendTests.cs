using System;
using System.Collections;
using GameplayTags;
using NUnit.Framework;
using Spine.Unity;
using UnityEngine;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;
using ZFramework.GAS.Unity.Spine;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Tests
{
    public sealed class SpineBackendTests
    {
        // Real Spine JSON: rotate timelines changed from "angle" to "value" in 4.x.
        public static TextAsset SkeletonJson(int attackStart, bool textured = false)
        {
#if GAS_SPINE_38
            const string version = "3.8.99", rotation = "angle";
#else
            const string version = "4.2.0", rotation = "value";
#endif
            string Keys(int start, int end) => "{\"bones\":{\"root\":{\"rotate\":[{\"" + rotation + "\":" +
                start + "},{\"time\":0.8,\"" + rotation + "\":" + end + "}]}}}";
            return new TextAsset("{\"skeleton\":{\"hash\":\"test\",\"spine\":\"" + version + "\",\"fps\":24}," +
                "\"bones\":[{\"name\":\"root\"}]," + (textured ?
                "\"slots\":[{\"name\":\"image\",\"bone\":\"root\",\"attachment\":\"quad\"}]," +
                "\"skins\":[{\"name\":\"default\",\"attachments\":{\"image\":{\"quad\":{\"width\":2,\"height\":2}}}}]," : "") +
                "\"animations\":{\"Idle\":" + Keys(0, 0) + ",\"Attack\":" + Keys(attackStart, 30) + "}}");
        }

        [UnityTest]
        public IEnumerator EnabledSkeletonAdvancesSkillWithoutManualSampling()
        {
            var json = SkeletonJson(0);
            var data = SkeletonDataAsset.CreateRuntimeInstance(json, Array.Empty<AtlasAssetBase>(), true, 1);
            var go = new GameObject("Spine auto playback");
            try
            {
                var skeleton = go.AddComponent<SkeletonAnimation>(); skeleton.skeletonDataAsset = data; skeleton.Initialize(true);
                var animator = go.AddComponent<SkillAnimator>(); animator.SetBackend(new SpineBackend());
                animator.Driver.Play("Attack", 0, false);
                float deadline = Time.realtimeSinceStartup + 3;
                while (skeleton.Skeleton.RootBone.Rotation < 3 && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.GreaterOrEqual(skeleton.Skeleton.RootBone.Rotation, 3);
                Assert.AreEqual("Attack", skeleton.AnimationState.GetCurrent(0).Animation.Name);
            }
            finally { Object.Destroy(go); Object.Destroy(data); Object.Destroy(json); }
        }

        [UnityTest]
        public IEnumerator PlaybackStretchMarkersAndStop()
        {
            var json = SkeletonJson(0);
            var data = SkeletonDataAsset.CreateRuntimeInstance(json, Array.Empty<AtlasAssetBase>(), true, 1);
            var go = new GameObject("Spine backend test");
            var skeleton = go.AddComponent<SkeletonAnimation>(); skeleton.skeletonDataAsset = data; skeleton.Initialize(true);
            var animator = go.AddComponent<SkillAnimator>(); animator.SetBackend(new SpineBackend { mixDuration = 0 });
            try
            {
                yield return null;
                skeleton.enabled = false; // Manually step this skeleton; no Unity or threaded update may advance it twice.
                var driver = animator.Driver;
                Assert.AreEqual(.4f, driver.Play("Attack", .4f, false), .001f);
                Assert.AreEqual(2, skeleton.AnimationState.GetCurrent(0).TimeScale, .001f);
                skeleton.Update(.2f); Assert.AreEqual(15, skeleton.Skeleton.RootBone.Rotation, .2f);
                driver.Stop(); Assert.AreEqual("<empty>", skeleton.AnimationState.GetCurrent(0).Animation.Name);
                Assert.AreEqual(0, driver.Play("Missing", 1, false));
                if (!GameplayTagManager.IsInitialized) GameplayTagManager.Initialize(Array.Empty<GameplayTagRegistration>());
                var owner = new AbilitySystemComponent(); var ability = owner.GrantAbility(new GameplayAbilityDefinition()); owner.TryActivateAbility(ability);
                float hit = -1, elapsed = 0;
                var task = new AnimationTask(new SkillAnimationDefinition("Attack", .8f, markers: new[] { new AnimationMarker(.4f) }),
                    driver, durationOverride: .4f, onMarker: (_, __) => hit = elapsed);
                owner.StartTask(ability, task);
                for (int i = 0; i < 13; i++) { elapsed += 1f / 60; owner.Tick(1f / 60); }
                Assert.AreEqual(.2f, hit, 1f / 60); owner.EndAbility(ability);
            }
            finally { Object.Destroy(go); Object.Destroy(data); Object.Destroy(json); }
        }
        [UnityTest]
        public IEnumerator BlendScalesWithStretchAndReturnsToPrevious()
        {
            var json = SkeletonJson(30);
            var data = SkeletonDataAsset.CreateRuntimeInstance(json, Array.Empty<AtlasAssetBase>(), true, 1);
            var go = new GameObject("Spine blend test");
            var skeleton = go.AddComponent<SkeletonAnimation>(); skeleton.skeletonDataAsset = data; skeleton.Initialize(true);
            skeleton.AnimationState.SetAnimation(0, "Idle", true);
            var animator = go.AddComponent<SkillAnimator>(); animator.SetBackend(new SpineBackend());
            try
            {
                yield return null;
                skeleton.enabled = false; // Manually step this skeleton; no Unity or threaded update may advance it twice.
                var driver = (IBlendingAnimationDriver)animator.Driver;
                driver.Play("Attack", .4f, false, .2f);
                var entry = skeleton.AnimationState.GetCurrent(0);
                // 拉伸 2 倍，时间轴上的 0.2 秒混入在实际时间里只有 0.1 秒。
                Assert.AreEqual(2, entry.TimeScale, .001f); Assert.AreEqual(.1f, entry.MixDuration, .001f);
                skeleton.Update(.05f); Assert.That(skeleton.Skeleton.RootBone.Rotation, Is.InRange(1f, 29f));
                skeleton.Update(.1f); Assert.AreEqual(30, skeleton.Skeleton.RootBone.Rotation, .5f);
                driver.Stop(.3f);
                var back = skeleton.AnimationState.GetCurrent(0);
                Assert.AreEqual("Idle", back.Animation.Name); Assert.IsTrue(back.Loop); Assert.AreEqual(.3f, back.MixDuration, .001f);
            }
            finally { Object.Destroy(go); Object.Destroy(data); Object.Destroy(json); }
        }
    }
}
