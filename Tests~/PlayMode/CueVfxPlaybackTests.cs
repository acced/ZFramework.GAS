using System.Collections;
using System.Linq;
using GameplayTags;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;
using ZFramework.GAS.Samples.BasicCombat;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Tests
{
    public sealed class CueVfxPlaybackTests
    {
        private static void Tags() => GameplayTagManager.Initialize(SampleTags.All.Concat(new[] { "Test.Cue.Ground" })
            .Select(name => new GameplayTagRegistration(name)).ToArray());
        [UnityTest]
        public IEnumerator ParticleCueFollowsPolicyAndAutomaticallyReclaimsItsTail()
        {
            Tags();
            var tag = GameplayTagManager.RequestTag("Test.Cue.Ground");
            var prefab = new GameObject("Particle template"); prefab.SetActive(false);
            var particles = prefab.AddComponent<ParticleSystem>();
            var main = particles.main; main.playOnAwake = false; main.startLifetime = .2f; main.startSpeed = 0; main.loop = true;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            var emission = particles.emission; emission.rateOverTime = 80;
            var library = ScriptableObject.CreateInstance<CueVfxLibrary>();
            library.bindings = new[] { new CueVfxBinding { cueTag = tag, prefab = prefab, follow = false,
                stopMode = CueVfxStopMode.StopEmitting, tailSeconds = .5f } };
            var hero = new GameObject("VFX test hero");
            var router = new GameplayCueRouter(); var asc = new AbilitySystemComponent(cueRouter: router);
            var player = hero.AddComponent<CueVfxPlayer>(); player.SetLibrary(library); player.Bind(asc);
            GameObject[] Instances() => Object.FindObjectsByType<Transform>(FindObjectsSortMode.None)
                .Where(t => t.name == "CueVFX · " + tag.Name).Select(t => t.gameObject).ToArray();
            void Cue(GameplayCueEvent e) => router.Dispatch(new GameplayCueParameters(tag, e, asc, asc, 1));
            try
            {
                Cue(GameplayCueEvent.OnActive); yield return new WaitForSeconds(.12f);
                var root = Instances().Single(); var instance = root.GetComponentInChildren<ParticleSystem>();
                Assert.Greater(instance.particleCount, 0);
                hero.transform.position = Vector3.right * 4; yield return null;
                Assert.AreEqual(Vector3.zero, root.transform.position);
                Cue(GameplayCueEvent.Removed); Assert.IsFalse(instance.isEmitting); Assert.IsTrue(root.activeSelf);
                yield return new WaitForSeconds(.6f); Assert.IsTrue(root == null); Assert.IsEmpty(Instances());
                Cue(GameplayCueEvent.OnActive); player.enabled = false; yield return null;
                Assert.IsEmpty(Instances()); Assert.AreEqual(0, router.HandlerCount);
            }
            finally { Object.Destroy(hero); Object.Destroy(prefab); Object.Destroy(library); }
        }

        [UnityTest]
        public IEnumerator GeneratedKnightCastsAssetsAndCleansGroundOnCancellation()
        {
#if UNITY_EDITOR
            const string folder = "Assets/GAS Samples/BasicCombat";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Prefabs/Knight.prefab");
            if (prefab == null) Assert.Ignore("先运行 CueVfxSampleTests 或创建 BasicCombat 示例。");
            Tags();
            var knight = Object.Instantiate(prefab);
            var hero = knight.AddComponent<CombatUnit>(); hero.Initialize(new GameplayCueRouter());
            var dummyGo = new GameObject("Test target"); var dummy = dummyGo.AddComponent<CombatUnit>(); dummy.Initialize(new GameplayCueRouter());
            var slash = AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(folder + "/Skills/斩击.asset");
            var warcry = AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(folder + "/Skills/战吼.asset");
            try
            {
                yield return null;
                Assert.AreEqual(AbilityActivationResult.Activated, hero.SkillRunner.TryCast(slash, dummy.AbilitySystem));
                float elapsed = 0;
                while (elapsed < .4f) { hero.AbilitySystem.Tick(Time.deltaTime); elapsed += Time.deltaTime; yield return null; }
                var trail = knight.GetComponentInChildren<SwordTrailVisual>();
                Assert.IsNotNull(trail); Assert.Greater(trail.GetComponent<MeshFilter>().sharedMesh.vertexCount, 4);
                hero.SkillRunner.Cancel(slash); yield return null;
                Assert.IsNull(knight.GetComponentInChildren<SwordTrailVisual>());
                Assert.AreEqual(AbilityActivationResult.Activated, hero.SkillRunner.TryCast(warcry));
                elapsed = 0;
                while (elapsed < .4f) { hero.AbilitySystem.Tick(Time.deltaTime); elapsed += Time.deltaTime; yield return null; }
                var root = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Single(t => t.name == "CueVFX · " + SampleTags.CueGroundTest).gameObject;
                var position = root.transform.position;
                Assert.IsTrue(root.GetComponentsInChildren<ParticleSystem>().Any(ps => ps.particleCount > 0));
                knight.transform.position += Vector3.right * 3; yield return null;
                Assert.AreEqual(position, root.transform.position);
                hero.SkillRunner.Cancel(warcry);
                Assert.IsTrue(root.GetComponentsInChildren<ParticleSystem>().All(ps => !ps.isEmitting));
                yield return new WaitForSeconds(1.7f);
                Assert.IsTrue(root == null);
            }
            finally { Object.Destroy(knight); Object.Destroy(dummyGo); }
#else
            yield break;
#endif
        }
    }
}
