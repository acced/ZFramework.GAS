using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using ZFramework.GAS.Samples.BasicCombat;
using ZFramework.GAS.Samples.BasicCombat.Editor;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    // Lives in the editor test assembly so runtime player builds never reference the debug editor assembly.
    public sealed class RuntimeDebugPlayModeTests
    {
        [UnityTearDown]
        public IEnumerator RestoreEditorDomain()
        {
            if (EditorApplication.isPlaying) yield return new ExitPlayMode();
            Assert.IsNull(RuntimeDebugSession.Current);
            // The generated registry freezes sample tags during Play. Restore a clean editor domain
            // so later EditMode tests can initialize their complete, test-only tag vocabulary.
            EditorUtility.RequestScriptReload();
            yield return new WaitForDomainReload();
            TestTags.Initialize();
            RuntimeDebugSession.FollowSelection = true;
        }
        [UnityTest]
        public IEnumerator BasicCombatSelectionShowsLiveSlashTagsCooldownAndEditorFollow()
        {
            if (!AssetDatabase.IsValidFolder(BasicCombatSample.Folder)) BasicCombatSample.Create(true);
            EditorSceneManager.OpenScene(BasicCombatSample.Folder + "/BasicCombat.unity");
            yield return new EnterPlayMode();
            SkillEditorWindow editor = null;
            RuntimeDebugWindow debugger = null;
            try
            {
                var arena = Object.FindFirstObjectByType<TrainingArena>(); Assert.IsNotNull(arena);
                using var arenaData = new SerializedObject(arena);
                var hero = (CombatUnit)arenaData.FindProperty("hero").objectReferenceValue;
                var slash = (GameplayAbilityAsset)arenaData.FindProperty("skills").GetArrayElementAtIndex(0).FindPropertyRelative("asset").objectReferenceValue;
                Assert.IsNotNull(hero.AbilitySystem);
                Selection.activeGameObject = hero.transform.GetChild(0).gameObject;
                RuntimeDebugSession.FollowSelection = true;
                debugger = RuntimeDebugWindow.Open(); debugger.CreateGUI();
                editor = SkillEditorWindow.Open(); editor.Show(slash); editor.SetFollowRuntime(true);
                arena.Cast(0);
                // Pause the scene clock after a deterministic gameplay step to inspect the short super-armor interval.
                arena.enabled = false;
                hero.AbilitySystem.Tick(.2f);
                RuntimeDebugSession.Refresh();
                var snapshot = RuntimeDebugSession.Current;
                Assert.AreSame(hero, RuntimeDebugSession.Observed);
                Assert.AreEqual(6, snapshot.Abilities.Count); // Five slots; slot 2 grants both energy variants.
                Assert.AreEqual("斩击", snapshot.Tasks.Single().AbilityName);
                Assert.IsTrue(snapshot.Tags.Any(t => t.Value.Name == SampleTags.SuperArmor));
                Assert.IsTrue(snapshot.Effects.Any(e => e.Asset != null && ReferenceEquals(e.Asset.Definition, slash.Definition.Cooldown)));
                Assert.Greater(snapshot.Abilities.Single(a => a.Asset == slash).CooldownRemaining, 0);
                Assert.IsTrue(editor.Document.Playback.Live);
                Assert.AreEqual(snapshot.Tasks.Single().AuthoredTime, editor.Document.Playback.Time, .001f);
                Assert.IsNotNull(editor.rootVisualElement.Q(className: "live-active"));
                foreach (var section in new[] { "debug-abilities", "debug-tasks", "debug-tags", "debug-attributes", "debug-effects" })
                    Assert.IsNotNull(debugger.rootVisualElement.Q<Foldout>(section));
                RuntimeDebugSession.Observe(hero);
                Selection.activeGameObject = null; RuntimeDebugSession.Refresh();
                Assert.AreSame(hero, RuntimeDebugSession.Observed, "Pinned unit is independent of selection");
                var marker=editor.Document.Serialized(slash.Animation).FindProperty("markers.Array.data[0]");
                var call=(CallModuleAction)marker.FindPropertyRelative("actions.Array.data[0]").managedReferenceValue;
                var condition=(ConditionalAction)call.module.actions[0];string normalId=condition.whenFalse[0].Id,strongId=condition.whenTrue[0].Id;
                editor.Document.Selection.Select(call.module);editor.Document.OpenFlow();
                bool existing=SkillActionDebugger.Has(normalId);
                try
                {
                    if(!existing)SkillActionDebugger.Toggle(normalId);
                    hero.AbilitySystem.Tick(.2f);RuntimeDebugSession.Refresh();
                    var frame=SkillActionDebugger.Frame; Assert.IsNotNull(frame);
                    Assert.AreEqual(normalId,frame.RawId);Assert.AreEqual(call.Id,frame.NavigationId);
                    StringAssert.Contains("Lv.1",frame.Summary);
                    var flow=editor.rootVisualElement.Q<SkillFlowView>();
                    Assert.IsTrue(flow.Query(className:"flow-live").ToList().Any(n=>Equals(n.userData,normalId)));
                    Assert.IsFalse(flow.Query(className:"flow-live").ToList().Any(n=>Equals(n.userData,strongId)));
                    var events=SkillEventRecorder.Of(hero.AbilitySystem);Assert.IsNotNull(events);
                    Assert.IsTrue(Enumerable.Range(0,events.Count).Any(i=>events[i].Kind==TraceKind.条件分支 && events[i].Label=="不满足"));
                    EditorApplication.isPaused=false;
                    hero.SkillRunner.Cancel(slash);hero.AbilitySystem.Tick(1);
                    var empowered=GameplayTags.GameplayTagManager.RequestTag(SampleTags.Empowered);
                    hero.AbilitySystem.OwnedTags.AddTag(empowered);
                    Assert.IsTrue(arena.TryCast(0));hero.AbilitySystem.Tick(.4f);RuntimeDebugSession.Refresh();
                    Assert.IsTrue(flow.Query(className:"flow-live").ToList().Any(n=>Equals(n.userData,strongId)));
                    Assert.IsFalse(flow.Query(className:"flow-live").ToList().Any(n=>Equals(n.userData,normalId)));
                }
                finally{EditorApplication.isPaused=false;if(!existing)SkillActionDebugger.Toggle(normalId);}
                hero.SkillRunner.Cancel(slash); RuntimeDebugSession.Refresh(); Assert.IsEmpty(RuntimeDebugSession.Current.Tasks);
                Object.DestroyImmediate(hero.gameObject); RuntimeDebugSession.Refresh();
                Assert.IsNull(RuntimeDebugSession.Current); Assert.IsFalse(editor.Document.Playback.Live);
                Assert.IsEmpty(editor.rootVisualElement.Query(className: "live-active").ToList());
            }
            finally { editor?.Close(); debugger?.Close(); RuntimeDebugSession.Clear(); }
            yield return new ExitPlayMode();
            Assert.IsNull(RuntimeDebugSession.Current);
        }
    }
}
