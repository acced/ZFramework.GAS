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
    // Unity asks build processors for package descriptors, independently of Assets/link.xml discovery.
    internal sealed class SkillPlayerBuildProcessor : IUnityLinkerProcessor
    {
        public int callbackOrder=>0;
        public string GenerateAdditionalLinkXmlFile(BuildReport report,UnityLinkerBuildPipelineData data)
        {
            var player=CompilationPipeline.GetAssemblies(AssembliesType.Player).Select(a=>a.name).ToHashSet();
            var roots=new[]{typeof(SkillAction),typeof(AnimationBackend),typeof(SkillRuntimeTrack)};
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
