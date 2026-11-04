using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.Compilation;
using UnityEditor.UnityLinker;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    // Populate only the scene copy being serialized into a build. Never dirty source prefabs/scenes.
    internal sealed class ExposedClipBuildProcessor : IProcessSceneWithReport
    {
        public int callbackOrder=>0;
        public void OnProcessScene(UnityEngine.SceneManagement.Scene scene,BuildReport report){
            if(report==null && !BuildPipeline.isBuildingPlayer)return;
            foreach(var root in scene.GetRootGameObjects())Prepare(root);
        }
        internal static void Prepare(UnityEngine.GameObject root){
            foreach(var host in root.GetComponentsInChildren<SkillAnimator>(true))
                if(host.Backend is PlayableAnimationBackend backend && backend.clips!=null){
                    backend.BakeRig(host.GetComponentInChildren<UnityEngine.Animator>(true));
                    foreach(var binding in backend.clips)binding?.PreparePlayerBindings();
                }
        }
    }

    // Write to the imported prefab artifact, including Resources and content bundles.
    // Declare dependencies so edits to a clip/model also invalidate this cached artifact.
    internal sealed class SkillPrefabBindings : AssetPostprocessor{
        public override uint GetVersion()=>4;
        private void OnPostprocessPrefab(UnityEngine.GameObject root){
            foreach(var host in root.GetComponentsInChildren<SkillAnimator>(true))
                if(host.Backend is PlayableAnimationBackend backend){
                    var anim=host.GetComponentInChildren<UnityEngine.Animator>(true);
                    void Depends(UnityEngine.Object asset){if(asset==null)return;var p=AssetDatabase.GetAssetPath(asset);if(!string.IsNullOrEmpty(p) && p!=assetPath)context.DependsOnArtifact(p);}
                    void DependsOnLayers(UnityEngine.RuntimeAnimatorController controller){
                        if(controller==null)return;
                        Depends(controller);
                        foreach(var clip in controller.animationClips)Depends(clip);
                        while(controller is UnityEngine.AnimatorOverrideController over){controller=over.runtimeAnimatorController;Depends(controller);}
                        if(controller is UnityEditor.Animations.AnimatorController native)
                            foreach(var layer in native.layers)if(layer.avatarMask!=null)Depends(layer.avatarMask);
                    }
                    if(anim!=null){if(anim.avatar!=null)Depends(anim.avatar);DependsOnLayers(anim.runtimeAnimatorController);}
                    if(backend.alternativeControllers!=null)foreach(var entry in backend.alternativeControllers)DependsOnLayers(entry?.controller);
                    if(backend.clips!=null)foreach(var b in backend.clips)if(b?.clip!=null)Depends(b.clip);
                }
            ExposedClipBuildProcessor.Prepare(root);
        }
    }
    // Unity asks build processors for package descriptors, independently of Assets/link.xml discovery.
    internal sealed class SkillPlayerBuildProcessor : IUnityLinkerProcessor
    {
        public int callbackOrder=>0;
        public string GenerateAdditionalLinkXmlFile(BuildReport report,UnityLinkerBuildPipelineData data)
        {
            var player=CompilationPipeline.GetAssemblies(AssembliesType.Player).Select(a=>a.name).ToHashSet();
            var roots=new[]{typeof(SkillAction),typeof(TaskSpawner),typeof(AnimationBackend),typeof(SkillRuntimeTrack)};
            var types=roots.SelectMany(t=>TypeCache.GetTypesDerivedFrom(t)).Distinct()
                .Where(t=>!t.IsAbstract && !t.ContainsGenericParameters && player.Contains(t.Assembly.GetName().Name));
            var xml=new XElement("linker",types.GroupBy(t=>t.Assembly.GetName().Name).OrderBy(g=>g.Key)
                .Select(g=>new XElement("assembly",new XAttribute("fullname",g.Key),
                    g.OrderBy(t=>t.FullName).Select(t=>new XElement("type",new XAttribute("fullname",t.FullName.Replace('+','/')),new XAttribute("preserve","all"))))));
            string path=Path.GetFullPath("Library/ZFramework.GAS/SkillTypes.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path));xml.Save(path);return path;
        }
    }
}
