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
                Assert.AreEqual(5, snapshot.Abilities.Count);
                Assert.AreEqual("斩击", snapshot.Tasks.Single().AbilityName);
                Assert.IsTrue(snapshot.Tags.Any(t => t.Value.Name == SampleTags.SuperArmor));
                Assert.IsTrue(snapshot.Effects.Any(e => e.Asset != null && e.Asset == AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(slash))
                    .OfType<GameplayEffectAsset>().Single(a => a.name.StartsWith("冷却"))));
                Assert.Greater(snapshot.Abilities.Single(a => a.Asset == slash).CooldownRemaining, 0);
                Assert.IsTrue(editor.Document.Playback.Live);
                Assert.AreEqual(snapshot.Tasks.Single().AuthoredTime, editor.Document.Playback.Time, .001f);
                Assert.IsNotNull(editor.rootVisualElement.Q(className: "live-active"));
                foreach (var section in new[] { "debug-abilities", "debug-tasks", "debug-tags", "debug-attributes", "debug-effects" })
                    Assert.IsNotNull(debugger.rootVisualElement.Q<Foldout>(section));
                RuntimeDebugSession.Observe(hero);
                Selection.activeGameObject = null; RuntimeDebugSession.Refresh();
                Assert.AreSame(hero, RuntimeDebugSession.Observed, "Pinned unit is independent of selection");
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
