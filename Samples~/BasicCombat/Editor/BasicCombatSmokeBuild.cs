using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    public static class BasicCombatSmokeBuild
    {
        public static void WindowsIL2CPP()
        {
            string module=Path.Combine(EditorApplication.applicationContentsPath,"PlaybackEngines/WindowsStandaloneSupport/Variations/win64_player_nondevelopment_il2cpp");
            if(!Directory.Exists(module)) throw new InvalidOperationException("Unity Hub 未安装对应版本的 Windows Build Support (IL2CPP)。");
            Build(ScriptingImplementation.IL2CPP,"IL2CPP");
        }
        public static void WindowsMono()=>Build(ScriptingImplementation.Mono2x,"Mono");
        public static void AndroidIL2CPP()=>Build(ScriptingImplementation.IL2CPP,"Android",BuildTarget.Android);
        private static void Build(ScriptingImplementation backend,string folder, BuildTarget platform=BuildTarget.StandaloneWindows64)
        {
            string scene=BasicCombatSample.Folder+"/BasicCombat.unity";
            if(!File.Exists(scene)) throw new FileNotFoundException("请先用 BasicCombat 生成器创建验证场景。",scene);
            bool android=platform==BuildTarget.Android;var target=android?NamedBuildTarget.Android:NamedBuildTarget.Standalone;
            var previous=PlayerSettings.GetScriptingBackend(target);
            try
            {
                PlayerSettings.SetScriptingBackend(target,backend);
                var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{scene},target=platform,
                    locationPathName="GASValidation~/"+folder+(android?"/GASSmoke.apk":"/GASSmoke.exe"),extraScriptingDefines=new[]{"GAS_PLAYER_SMOKE"}});
                if(report.summary.result!=BuildResult.Succeeded) throw new InvalidOperationException(folder+" 冒烟构建失败："+report.summary.result);
            }
            finally{PlayerSettings.SetScriptingBackend(target,previous);}
        }
    }
}
