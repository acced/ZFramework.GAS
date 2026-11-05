using System.IO;
using System.Collections.Generic;
using System.Linq;
using GameplayTags;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using ZFramework.GAS.Samples.BasicCombat;
using ZFramework.GAS.Samples.BasicCombat.Editor;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class CueVfxSampleTests
    {
        [Test]
        public void EveryGeneratedSkillMarkerOpensItsOwnAuthoredActionTree()
        {
            const string skills = BasicCombatSample.Folder + "/Skills/";
            if (!AssetDatabase.IsValidFolder(skills.TrimEnd('/'))) BasicCombatSample.Create();
            var window = ScriptableObject.CreateInstance<SkillEditorWindow>();
            try
            {
                window.ShowUtility();
                int markers = 0;
                foreach (var name in new[] { "斩击", "战吼", "分叉闪电", "圣域引导", "召唤" })
                {
                    var skill = AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(skills + name + ".asset");
                    Assert.IsNotNull(skill, name);
                    window.Show(skill);
                    var animation = skill.Animation;
                    if (animation == null) continue;
                    var items = window.Document.Serialized(animation).FindProperty("markers");
                    for (int i = 0; i < items.arraySize; i++)
                    {
                        var marker = items.GetArrayElementAtIndex(i);
                        var actions = marker.FindPropertyRelative("actions");
                        var expected = new List<string>();
                        var cursor = actions.Copy();
                        var end = cursor.GetEndProperty();
                        while (cursor.Next(true) && !SerializedProperty.EqualContents(cursor,end))
                            if (cursor.propertyType == SerializedPropertyType.ManagedReference && cursor.managedReferenceValue is SkillAction a)
                                expected.Add(a.Id);
                        window.Document.Selection.Select(animation, "markers", marker.FindPropertyRelative("id").stringValue);
                        window.Document.OpenFlow();
                        var nodes = window.rootVisualElement.Q<SkillFlowView>().Q<GraphView>().nodes.ToList();
                        StringAssert.Contains(marker.FindPropertyRelative("key").stringValue, nodes.Single(n => Equals(n.userData,"$entry")).title);
                        CollectionAssert.AreEquivalent(expected, nodes.Where(n => !Equals(n.userData,"$entry")).Select(n => n.userData), name);
                        markers++;
                    }
                }
                Assert.Greater(markers, 0);
            }
            finally { window.Close(); }
        }
        [Test]
        public void GeneratedDemoUsesSharedLibraryAndRendersGroundParticlesAndSwordTrail()
        {
            const string folder = BasicCombatSample.Folder;
            TestTags.Initialize();
            BasicCombatSample.Create();
            EditorSceneManager.OpenScene(folder + "/BasicCombat.unity", OpenSceneMode.Single);
            // 使用同一个入口重新生成正在打开的示例，确保场景和引用也完成更新。
            BasicCombatSample.Create();
            Assert.AreEqual(folder + "/BasicCombat.unity", SceneManager.GetActiveScene().path);
            Assert.AreEqual(1, SceneManager.sceneCount);
            var knight = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Prefabs/Knight.prefab");
            var library = AssetDatabase.LoadAssetAtPath<CueVfxLibrary>(folder + "/CueVfxLibrary.asset");
            var module = AssetDatabase.LoadAssetAtPath<SkillActionModuleAsset>(folder + "/Skills/Shared/扇形斩击动作.asset");
            Assert.IsNotNull(module); Assert.IsNotNull(MonoScript.FromScriptableObject(module), "Shared module must survive a domain reload");
            Assert.IsNotNull(module.parameters); Assert.IsNotNull(MonoScript.FromScriptableObject(module.parameters));
            // 共享参数集只放模块用到的伤害与范围；冷却留在斩击自身，模块只绑定共享集里的参数。
            var slashSkill = AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(folder + "/Skills/斩击.asset");
            CollectionAssert.AreEquivalent(new[] { "普通伤害系数", "强化伤害系数", "范围", "基础范围" }, module.parameters.parameters.Select(p => p.displayName));
            Assert.AreSame(module.parameters, slashSkill.parameterSet);
            CollectionAssert.AreEqual(new[] { "冷却" }, slashSkill.parameters.Select(p => p.displayName));
            var call = (CallModuleAction)new SerializedObject(slashSkill.Animation).FindProperty("markers.Array.data[0].actions.Array.data[0]").managedReferenceValue;
            CollectionAssert.AreEquivalent(module.parameters.parameters.Select(p => p.Id), call.bindings.Select(b => b.moduleParameterId));
            var variant = AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(folder + "/Skills/Shared/重斩变体.asset");
            Assert.AreSame(slashSkill.Animation,variant.Animation);
            Assert.IsTrue(variant.clipOverrides.TryGet(slashSkill.Animation.ClipName,out var axe));
            CollectionAssert.Contains(knight.GetComponent<Animator>().runtimeAnimatorController.animationClips.Select(c=>c.name),"AxeSlash");
            Assert.AreEqual("Base Layer.AxeSlash",axe.replacement);
            Assert.AreEqual(2, variant.parameterOverrides.Length, "重斩只覆盖普通与强化两个伤害系数");
            Assert.AreEqual(1.8f, variant.ResolveParameters(1).Get(module.parameters.parameters.Single(p => p.displayName == "普通伤害系数").Id).Number, 1e-4f);
            Assert.AreEqual(9, library.bindings.Length);
            Assert.AreSame(library, knight.GetComponent<CueVfxPlayer>().Library);
            Assert.IsEmpty(knight.GetComponentsInChildren<CueVisual>(true), "角色只引用共享配置，不预挂特效。");
            foreach (var binding in library.bindings)
            {
                Assert.IsNotNull(binding.prefab); Assert.IsNotNull(binding.FindAnchor(knight.transform));
                Assert.IsTrue(PrefabUtility.IsPartOfPrefabAsset(binding.prefab));
            }
            var ground = library.bindings.Single(b => b.cueTag.Name == SampleTags.CueGroundTest);
            Assert.IsFalse(ground.follow); Assert.AreEqual(CueVfxStopMode.StopEmitting, ground.stopMode);
            Assert.IsEmpty(ground.prefab.GetComponentsInChildren<CueVfxBehaviour>());
            foreach (string dependency in AssetDatabase.GetDependencies(folder + "/BasicCombat.unity", true))
                if (dependency.StartsWith("Assets/")) StringAssert.StartsWith(folder + "/", dependency, "示例不能引用目录外的生成资产");
            foreach (var guid in AssetDatabase.FindAssets("t:GameplayAbilityAsset", new[] { folder + "/Skills" }))
            {
                var skill = AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(AssetDatabase.GUIDToAssetPath(guid));
                Assert.AreSame(knight, new LayoutStore(skill).PreviewPrefab);
            }

            var render = new PreviewRenderUtility();
            try
            {
                PreviewStage.Configure(render);
                var owner = render.InstantiatePrefabInScene(knight);
                foreach (var animator in owner.GetComponentsInChildren<Animator>()) animator.enabled = false;
                foreach (var behaviour in owner.GetComponentsInChildren<MonoBehaviour>()) behaviour.enabled = false;
                PreviewStage.AddGround(render, new Bounds(new Vector3(0, 1, 0), new Vector3(2.8f, 2, 2.8f)));
                using var sampler = AnimationAdapterRegistry.Resolve(knight).BeginPreview(owner);
                using var preview = new CueVfxPreview(owner);
                var warcry = AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(folder + "/Skills/战吼.asset");
                preview.Sample(warcry.Animation, .55f, t => sampler.Sample(warcry.Animation.ClipName, t));
                var particles = Resources.FindObjectsOfTypeAll<ParticleSystem>().Where(p => p.gameObject.scene == owner.scene).ToArray();
                Assert.IsTrue(particles.Any(p => p.particleCount > 0));
                Capture(render, "cue-ground-preview.png");
                preview.Sample(warcry.Animation, .1f, t => sampler.Sample(warcry.Animation.ClipName, t));
                Assert.IsFalse(Resources.FindObjectsOfTypeAll<ParticleSystem>().Any(p => p.gameObject.scene == owner.scene));
                const float swordTime = .37f;
                preview.Sample(slashSkill.Animation, swordTime, t => sampler.Sample(slashSkill.Animation.ClipName, t));
                var trail = owner.GetComponentInChildren<SwordTrailVisual>();
                Assert.IsNotNull(trail); Assert.Greater(trail.GetComponent<MeshFilter>().sharedMesh.vertexCount, 4);
                var vertices = trail.GetComponent<MeshFilter>().sharedMesh.vertices;
                var tip = owner.GetComponentsInChildren<Transform>().Single(t => t.name == "SwordTip");
                Assert.Less(Vector3.Distance(tip.position, trail.transform.TransformPoint(vertices[vertices.Length - 1])), .0001f);
                Capture(render, "cue-sword-preview.png");
            }
            finally { render.Cleanup(); }
        }

        private static void Capture(PreviewRenderUtility render, string name)
        {
            string directory = Path.Combine(Directory.GetCurrentDirectory(), "GASValidation~/CueVfx");
            Directory.CreateDirectory(directory);
            var rotation = Quaternion.Euler(18, 155, 0);
            render.camera.transform.position = new Vector3(-.35f, .85f, 0) + rotation * new Vector3(0, 0, -7.5f);
            render.camera.transform.rotation = rotation;
            render.camera.nearClipPlane = .01f; render.camera.farClipPlane = 100;
            render.BeginStaticPreview(new Rect(0, 0, 900, 680)); render.Render(true);
            var image = render.EndStaticPreview();
            try { File.WriteAllBytes(Path.Combine(directory, name), image.EncodeToPNG()); }
            finally { Object.DestroyImmediate(image); }
        }
    }
}
