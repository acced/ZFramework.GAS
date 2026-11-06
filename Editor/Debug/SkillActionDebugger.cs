using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    [FilePath("UserSettings/GasBreakpoints.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class SkillBreakpoints : ScriptableSingleton<SkillBreakpoints>
    {
        [SerializeField] private List<string> actions=new();
        public bool Any=>actions.Count!=0;
        public bool Contains(string id) => !string.IsNullOrEmpty(id) && actions.Contains(id);
        public void Toggle(string id) { if (string.IsNullOrEmpty(id)) return; if (!actions.Remove(id)) actions.Add(id); Save(true); }
    }
    [InitializeOnLoad]
    public static class SkillActionDebugger
    {
        public sealed class PausedFrame
        {
            public readonly string RawId, NavigationId, Summary;
            public readonly GameplayAbilityAsset Asset;
            public readonly AbilitySystemComponent Owner;
            internal PausedFrame(SkillExecutionContext c, string id, string label)
            {
                Asset=c.Asset; Owner=c.Source; RawId=id; NavigationId=c.EditorCallSiteId ?? id;
                var parameters=new List<KeyValuePair<string,SkillParameterValue>>(); c.Values.Parameters.CopyTo(parameters);
                Summary=$"{(Asset!=null?Asset.name:"技能")} › {label}  ·  Lv.{c.Values.Level}\n目标：{(c.Services.Target != null ? c.Services.Target.name : "无")}  点：{(c.Services.HasTargetPosition?c.Services.TargetPosition.ToString():"无")}\n参数："+string.Join("，",parameters.Select(p=>p.Key+"="+p.Value))+
                    "\n最近选敌："+(LastTargets.TryGetValue(c,out var targets)?string.Join("，",targets.Names):"无")+"\n通道："+string.Join("，",c.Channels.Select(p=>p.Key+"="+p.Value))+(c.Event.HasValue?"\n事件："+c.Event.Value.Tag+"  "+c.Event.Value.Payload:"");
            }
        }
        public static PausedFrame Frame { get; private set; }
        public static bool Has(string id)=>SkillBreakpoints.instance.Contains(id);

        public static string PausedActionId { get; private set; }
        private sealed class QueryResult {public readonly List<string> Names=new();}
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SkillExecutionContext,QueryResult> LastTargets=new();
        private static bool _next,_subscribed;
        public static bool PauseNext {get=>_next;set{_next=value;Subscribe();}}
        private static void Query(SkillExecutionContext c,TargetQueryBuffer b)
        {
            if(c==null)return;var result=LastTargets.GetValue(c,_=>new QueryResult());result.Names.Clear();
            for(int i=0;i<b.Count;i++)result.Names.Add(b.Transforms[i]!=null?b.Transforms[i].name:"单位");
        }
        private static void Subscribe()
        {
            bool need=EditorApplication.isPlaying && (_next||SkillBreakpoints.instance.Any);if(need==_subscribed)return; _subscribed=need;
            if(need){SkillActionDebug.Boundary+=Boundary;SkillActionDebug.PointReached+=Point;SkillActionDebug.TargetsSelected+=Query;}
            else {SkillActionDebug.Boundary-=Boundary;SkillActionDebug.PointReached-=Point;SkillActionDebug.TargetsSelected-=Query;}
        }
        private static void Point(SkillExecutionContext c,string id,string label){if(c!=null && Has(id)) Pause(c,id,label);}
        static SkillActionDebugger()
        {
            EditorApplication.delayCall+=Subscribe;
            EditorApplication.playModeStateChanged += _ => { PausedActionId = null; PauseNext = false; Frame=null; };
        }
        public static void Toggle(string actionId)
        {SkillBreakpoints.instance.Toggle(actionId);Subscribe();}
        private static void Boundary(SkillExecutionContext context, SkillActionDefinition action)
        =>Pause(context,action.Id,action.Label);
        private static void Pause(SkillExecutionContext context,string id,string label)
        {
            if (!EditorApplication.isPlaying || EditorApplication.isPaused) return;
            if (!PauseNext && !Has(id) && (context.EditorCallSiteId == null || !Has(context.EditorCallSiteId))) return;
            Frame=new PausedFrame(context,id,label);
            PauseNext = false; PausedActionId = id;
            EditorApplication.isPaused = true;
        }
        public static void StepScene()
        {
            if (!EditorApplication.isPlaying) return;
            EditorApplication.isPaused = true; EditorApplication.Step();
        }
    }
}
