using System.Collections;
using System.Linq;
using NUnit.Framework;
using GameplayTags;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using ZFramework.GAS.Unity;
using ZFramework.GAS.Samples.BasicCombat;
#if UNITY_EDITOR
using UnityEditor;
#endif
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Tests
{
    public sealed class ConfigurableSkillPlaybackTests
    {
        [UnityTest] public IEnumerator GeneratedSkillsUseAssetActionsAndDetachedJobsSurviveAnimation()
        {
#if UNITY_EDITOR
            const string folder = "Assets/GAS Samples/BasicCombat";
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(folder + "/BasicCombat.unity", new LoadSceneParameters(LoadSceneMode.Additive));
            var scene = SceneManager.GetSceneByPath(folder + "/BasicCombat.unity");
            var arena = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<TrainingArena>()).Single();
            using var data = new SerializedObject(arena);
            var hero = (CombatUnit)data.FindProperty("hero").objectReferenceValue; var target = (CombatUnit)data.FindProperty("dummy").objectReferenceValue;
            GameplayAbilityAsset Skill(string name) => AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(folder + "/Skills/" + name + ".asset");
            try
            {
                var health = target.Get(CombatUnit.Health);
                arena.Cast(0); yield return new WaitForSeconds(.6f);
                Assert.Less(target.Get(CombatUnit.Health), health, "Generated sector damage should hit the dummy without business callbacks");
                arena.Cast(2); yield return new WaitForSeconds(1.3f);
                Assert.IsTrue(hero.AbilitySystem.OwnedTags.HasTag(GameplayTags.GameplayTagManager.RequestTag(SampleTags.Empowered)));
                float before = target.Get(CombatUnit.Health); arena.Cast(0); yield return new WaitForSeconds(.6f);
                Assert.Greater(before - target.Get(CombatUnit.Health), health - before, "Empowered branch should do greater damage");
                arena.Cast(1); yield return new WaitForSeconds(Skill("能量弹").Animation.Duration + .1f);
                var host = SceneSkillTaskHost.For(scene);
                Assert.IsFalse(hero.SkillRunner.IsActive(Skill("能量弹")));
                Assert.Greater(host.Tasks.Count, 0, "Slow projectile must still be in flight after animation ends");
                float beforeImpact = target.Get(CombatUnit.Health); yield return new WaitForSeconds(2.5f);
                Assert.Less(target.Get(CombatUnit.Health), beforeImpact);
                arena.Cast(3); yield return new WaitForSeconds(.7f);
                Assert.IsTrue(hero.SkillRunner.Cancel(Skill("圣域引导")));
                Assert.IsFalse(hero.AbilitySystem.OwnedTags.HasTag(GameplayTags.GameplayTagManager.RequestTag(SampleTags.Channeling)));
                arena.Cast(4); yield return new WaitForSeconds(Skill("召唤").Animation.Duration + .2f);
                Assert.IsFalse(hero.SkillRunner.IsActive(Skill("召唤"))); Assert.GreaterOrEqual(host.Tasks.Count, 2, "Summon and its area are scene owned");
                Assert.IsTrue(scene.GetRootGameObjects().Any(go => go.name.StartsWith("Guardian")));
            }
            finally { arena.enabled = false; }
            var lingering = scene.GetRootGameObjects().Where(go => go.name.StartsWith("Guardian") || go.name == "GAS Scene Tasks").ToArray();
            yield return SceneManager.UnloadSceneAsync(scene);
            Assert.IsTrue(lingering.All(go => go == null), "Scene unload must clean independent objects and host");
#else
            yield break;
#endif
        }
    }
}
