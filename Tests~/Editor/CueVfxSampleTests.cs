using System.IO;
using System.Linq;
using GameplayTags;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZFramework.GAS.Samples.BasicCombat;
using ZFramework.GAS.Samples.BasicCombat.Editor;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class CueVfxSampleTests
    {
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
                var slash = AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(folder + "/Skills/斩击.asset");
                const float swordTime = .37f;
                preview.Sample(slash.Animation, swordTime, t => sampler.Sample(slash.Animation.ClipName, t));
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
