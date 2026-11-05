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
        [UnityTest] public IEnumerator EnergyBoltForksFromAOrTravelsFromAToBToC(){
#if UNITY_EDITOR
            const string path="Assets/GAS Samples/BasicCombat/BasicCombat.unity";
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(path,new LoadSceneParameters(LoadSceneMode.Additive));
            var scene=SceneManager.GetSceneByPath(path);var arena=scene.GetRootGameObjects().SelectMany(o=>o.GetComponentsInChildren<TrainingArena>()).Single();
            using var data=new SerializedObject(arena);
            var a=(CombatUnit)data.FindProperty("dummy").objectReferenceValue;
            var extras=data.FindProperty("secondary");Assert.AreEqual(2,extras.arraySize);
            var b=(CombatUnit)extras.GetArrayElementAtIndex(0).objectReferenceValue;var c=(CombatUnit)extras.GetArrayElementAtIndex(1).objectReferenceValue;
            var hits=new System.Collections.Generic.List<(CombatUnit unit,float at,float amount)>();
            void Track(CombatUnit u,float delta){if(delta<0)hits.Add((u,Time.time,-delta));}
            foreach(var u in new[]{a,b,c})u.HealthChanged+=Track;
            var key=typeof(TrainingArena).GetMethod("HandleModeKey",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            void Press(EventType type){key.Invoke(arena,new object[]{new Event{type=type,keyCode=KeyCode.Q}});}
            var oldScale=Time.timeScale;
            try{
                Time.timeScale=3;
                b.transform.position=a.transform.position+Vector3.right*3;c.transform.position=b.transform.position+Vector3.right*3;
                Physics.SyncTransforms();
                Assert.IsTrue(arena.FanMode);Assert.IsTrue(arena.TryCast(1));yield return new WaitForSeconds(.1f);
                Press(EventType.KeyDown);Press(EventType.KeyDown);Press(EventType.KeyUp);
                Assert.IsFalse(arena.FanMode,"one physical Q press changes the next cast once");
                Assert.IsFalse(arena.TryCast(1),"Switching the selected asset cannot bypass the shared cooldown.");
                yield return new WaitForSeconds(4);
                CollectionAssert.AreEqual(new[]{a,b},hits.Select(h=>h.unit).ToArray(),"C is outside A's radius; Q after casting must not change the in-flight fork");
                Assert.AreEqual(hits[0].at,hits[1].at);Assert.AreEqual(hits[0].amount*.8f,hits[1].amount,.02f);
                hits.Clear();Assert.IsTrue(arena.TryCast(1));
                // C only enters range of B after A was already hit.
                c.transform.position=b.transform.position+Vector3.right*14;Physics.SyncTransforms();
                float deadline=Time.time+6;while(hits.Count==0 && Time.time<deadline)yield return null;
                Assert.AreEqual(1,hits.Count);Assert.AreSame(a,hits[0].unit);
                c.transform.position=b.transform.position+Vector3.right*3;Physics.SyncTransforms();
                deadline=Time.time+9;while(hits.Count<3 && Time.time<deadline)yield return null;
                CollectionAssert.AreEqual(new[]{a,b,c},hits.Select(h=>h.unit).ToArray());
                Assert.Greater(hits[1].at-hits[0].at,.5f);Assert.Greater(hits[2].at-hits[1].at,.5f);
                Assert.AreEqual(hits[0].amount*.64f,hits[2].amount,.02f);
                hits.Clear();Press(EventType.KeyDown);Press(EventType.KeyUp);
                c.transform.position=a.transform.position+Vector3.back*2;Physics.SyncTransforms();
                Assert.IsTrue(arena.TryCast(1));
                deadline=Time.time+5;while(hits.Count==0 && Time.time<deadline)yield return null;
                Assert.AreEqual(3,hits.Count);Assert.AreEqual(1,hits.Select(h=>h.at).Distinct().Count());
                var links=scene.GetRootGameObjects().SelectMany(o=>o.GetComponentsInChildren<LineRenderer>()).Where(l=>l.gameObject.name.StartsWith("LightningLink")).ToArray();
                Assert.AreEqual(2,links.Length);Assert.Less(Vector3.Distance(links[0].GetPosition(0),links[1].GetPosition(0)),.01f);
                Assert.Greater(Vector3.Distance(links[0].GetPosition(4),links[1].GetPosition(4)),1);
            }finally{Time.timeScale=oldScale;foreach(var u in new[]{a,b,c})u.HealthChanged-=Track;arena.enabled=false;}
            yield return SceneManager.UnloadSceneAsync(scene);
#else
            yield break;
#endif
        }
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
                arena.ToggleWeaponMotion();arena.Cast(0); yield return new WaitForSeconds(.3f);
                Assert.IsTrue(hero.GetComponentInChildren<Animator>().GetCurrentAnimatorStateInfo(0).IsName("Base Layer.AxeSlash"));
                yield return new WaitForSeconds(.3f);arena.ToggleWeaponMotion();
                Assert.Less(target.Get(CombatUnit.Health), health, "Generated sector damage should hit the dummy without business callbacks");
                arena.Cast(2); yield return new WaitForSeconds(1.3f);
                Assert.IsTrue(hero.AbilitySystem.OwnedTags.HasTag(GameplayTags.GameplayTagManager.RequestTag(SampleTags.Empowered)));
                float before = target.Get(CombatUnit.Health); arena.Cast(0); yield return new WaitForSeconds(.6f);
                Assert.Greater(before - target.Get(CombatUnit.Health), health - before, "Empowered branch should do greater damage");
                arena.Cast(1); yield return new WaitForSeconds(Skill("分叉闪电").Animation.Duration + .1f);
                var host = SceneSkillTaskHost.For(scene);
                Assert.IsFalse(hero.SkillRunner.IsActive(Skill("分叉闪电")));
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
