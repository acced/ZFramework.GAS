using System.Linq;
using NUnit.Framework;
using Spine.Unity;
using UnityEngine;
using ZFramework.GAS.Editor;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Tests
{
    public sealed class SpinePreviewTests
    {
        [Test]
        public void RealtimeScrubsAndBlendsTheRenderedMeshWithoutChangingTheSource()
        {
            var json = SpineBackendTests.SkeletonJson(0, textured: true);
            var page = new TextAsset("page.png\nsize: 2,2\nformat: RGBA8888\nfilter: Nearest,Nearest\nrepeat: none\n" +
                "quad\n  rotate: false\n  xy: 0,0\n  size: 2,2\n  orig: 2,2\n  offset: 0,0\n  index: -1\n");
            var texture = new Texture2D(2, 2) { name = "page" };
            var material = new Material(Shader.Find("Sprites/Default")) { mainTexture = texture };
            var atlas = SpineAtlasAsset.CreateRuntimeInstance(page, new[] { material }, true);
            var data = SkeletonDataAsset.CreateRuntimeInstance(json, new AtlasAssetBase[] { atlas }, true, 1);
            var source = new GameObject("Spine preview source");
            GameObject instance = null;
            try
            {
                var skeleton = source.AddComponent<SkeletonAnimation>(); skeleton.skeletonDataAsset = data; skeleton.Initialize(true);
                var adapter = AnimationAdapterRegistry.Resolve(source);
                Assert.AreEqual("spine", adapter.BackendId);
                var clip = adapter.EnumerateClips(source).Single(c => c.Name == "Attack");
                Assert.AreEqual(24, clip.FrameRate); Assert.AreEqual(.8f, clip.Length, .001f);
                source.AddComponent<SkillAnimator>().SetBackend(adapter.CreateDefaultBackend(source));
                Assert.AreSame(adapter, AnimationAdapterRegistry.Resolve(source));
                instance = Object.Instantiate(source);
                foreach (var behaviour in instance.GetComponentsInChildren<MonoBehaviour>()) behaviour.enabled = false;
                using var sampler = (IBlendPoseSampler)adapter.BeginPreview(instance);
                var filter = instance.GetComponent<MeshFilter>();
                sampler.Sample("Attack", 0);
                Assert.AreEqual(4, filter.sharedMesh.vertexCount);
                var start = filter.sharedMesh.vertices[0];
                sampler.Sample("Attack", .8f);
                Assert.Greater(Vector3.Distance(start, filter.sharedMesh.vertices[0]), .1f);
                sampler.Sample("Idle", 0, "Attack", .8f, .5f);
                Assert.AreEqual(15, instance.GetComponent<SkeletonAnimation>().Skeleton.RootBone.Rotation, .1f);
                sampler.Sample("Attack", 0);
                Assert.Less(Vector3.Distance(start, filter.sharedMesh.vertices[0]), .001f);
                Assert.AreEqual(0, skeleton.Skeleton.RootBone.Rotation);
            }
            finally
            {
                if (instance != null) Object.DestroyImmediate(instance);
                Object.DestroyImmediate(source); Object.DestroyImmediate(data); Object.DestroyImmediate(atlas);
                Object.DestroyImmediate(material); Object.DestroyImmediate(texture); Object.DestroyImmediate(page); Object.DestroyImmediate(json);
            }
        }
    }
}
