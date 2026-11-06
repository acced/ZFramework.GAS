using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    // Each cast/chain owns its budget, visited incarnations and immutable starting parameters.
    // An asynchronous projectile retains the frame through its task context. Nothing ticks here:
    // only an explicitly authored continuation (typically in onHit) advances the sequence.
    internal sealed class TargetSequenceFrame
    {
        private sealed class Run
        {
            internal int Limit,Used; internal bool Repeat;
            internal HashSet<object> Visited;
            internal SkillActionDefinition[] Next;
            internal string IndexId,ScaleId;
            internal float Retention;internal SkillParameterValues Parameters;
        }
        private readonly Run _run;
        private readonly int _index;
        internal bool Full=>_run.Used>=_run.Limit;
        internal bool Excludes(AbilitySystemComponent unit)=>!_run.Repeat && _run.Visited.Contains(unit.SourceIdentity);
        private TargetSequenceFrame(Run run,int index){_run=run;_index=index;}
        internal TargetSequenceFrame(int limit,bool repeat,HashSet<object> visited,SkillActionDefinition[] next,string index,string scale,float retention,SkillParameterValues parameters){
            _run=new Run{Limit=limit,Repeat=repeat,Visited=visited,Next=next,IndexId=index,ScaleId=scale,Retention=retention,Parameters=parameters};
        }
        internal SkillExecutionContext Scope(SkillExecutionContext context){
            var values = TargetSequenceAction.StepParameters(_run.Parameters, _run.IndexId, _run.ScaleId, _index, _run.Retention);
            var scope=context.WithParameters(values);scope.Sequence=this;return scope;
        }
        internal void RunStep(SkillExecutionContext context,SkillActionDefinition[] actions){
            var target=context.Target;
            if(Full || target==null || Excludes(target) || !context.CanContinue)return;
            // Claim before callbacks: sibling branches cannot select the same unit reentrantly.
            _run.Used++;_run.Visited.Add(target.SourceIdentity);Scope(context).Execute(actions);
        }
        internal void Next(SkillExecutionContext context){
            if(!Full)new TargetSequenceFrame(_run,_index+1).RunStep(context,_run.Next);
        }
    }

    [Serializable] [System.ComponentModel.DisplayName("有限目标序列")]
    public sealed class TargetSequenceAction : SkillAction
    {
        public static SkillParameterValues StepParameters(SkillParameterValues values, string indexId, string scaleId, int hop, float retention)
        {
            if (!string.IsNullOrEmpty(indexId)) values = values.With(indexId, new SkillParameterValue(hop));
            if (!string.IsNullOrEmpty(scaleId)) values = values.With(scaleId, new SkillParameterValue(Mathf.Pow(retention, hop)));
            return values;
        }
        [Tooltip("包含首目标；所有分支共用这个上限。")] [FlowField("总目标数（含首击）")] public SkillNumber maxTargets=new(3);
        public bool allowRepeat;
        [Tooltip("留空：每条链独立。指定相同记录键：本次施放的多条链共享已选单位。")] public string sharedRecord;
        [SkillParameterReference] public string indexParameterId;
        [SkillParameterReference] public string scaleParameterId;
        [FlowField("每跳保留")] public SkillNumber retention=new(1);
        [SerializeReference,FlowBranch("首目标")] public SkillAction[] first=Array.Empty<SkillAction>();
        [SerializeReference,FlowBranch("后续目标")] public SkillAction[] next=Array.Empty<SkillAction>();
        public override bool IsDetachedBranch(string field) => field == nameof(next);
        public override SkillActionDefinition Build(SkillActionBuildContext builder){
            builder.RequireKind(indexParameterId, SkillParameterKind.Int); builder.RequireKind(scaleParameterId, SkillParameterKind.Float);
            builder.Number(maxTargets,1,64,true);builder.Number(retention,0,1);
            var initial=builder.Build(first);var following=BuildBranch(builder, nameof(next), next);
            var limit=maxTargets;var ratio=retention;var record=sharedRecord;bool repeat=allowRepeat;
            string index=indexParameterId,scale=scaleParameterId;
            return Define("有限目标序列",c=>new TargetSequenceFrame((int)limit.Resolve(c.Values),repeat,c.HitRecord(record),following,index,scale,ratio.Resolve(c.Values),c.Values.Parameters).RunStep(c,initial));
        }
    }

    [Serializable] [System.ComponentModel.DisplayName("继续到所选目标")]
    public sealed class NextTargetStepAction : SkillAction
    {
        public override SkillActionDefinition Build(SkillActionBuildContext builder)=>Define("继续到所选目标",c=>c.Sequence.Next(c));
    }

    [Serializable] [System.ComponentModel.DisplayName("选择目标")]
    public sealed class SelectTargetsAction : SkillAction
    {
        [FlowField("搜索原点", nameof(TargetQuery.origin))] public TargetQuery targets=new(){kind=TargetQueryKind.Circle};
        [FlowField("最多选择")] public SkillNumber count=new(1);
        public bool excludeCurrent=true,excludeVisited=true;
        [Tooltip("可选：排除本次施放里这个记录已经选过的单位。")]public string sharedRecord;
        public bool recordSelection;
        [SerializeReference,FlowBranch("每个所选目标")] public SkillAction[] actions=Array.Empty<SkillAction>();
        public override SkillActionDefinition Build(SkillActionBuildContext builder){
            builder.Query(targets);builder.Number(count,0,256,true);
            if(recordSelection && string.IsNullOrEmpty(sharedRecord))throw new InvalidOperationException("记录所选单位需要填写记录键。");
            var query=targets.Build();var number=count;var sub=builder.Build(actions);bool skip=excludeCurrent,visited=excludeVisited,remember=recordSelection;var key=sharedRecord;
            return Define("选择目标",c=>{
                int take=Math.Min((int)number.Resolve(c.Values),query.MaxTargets);
                if(take==0 || c.Sequence?.Full==true)return;
                var record=!string.IsNullOrEmpty(key)?c.HitRecord(key):null;var buffer=c.RentBuffer();
                try{
                    c.Services.Searcher.Query(query,c,buffer,null,applyLimit:false,captureAim:true);
                    GasAssert.That(!buffer.Saturated, "选择目标缓冲区已满，请缩小范围或碰撞层。");
                    for(int i=0;i<buffer.Count && take>0 && c.CanContinue && c.Sequence?.Full!=true;i++){
                        var unit=buffer.Units[i];var t=buffer.Transforms[i];
                        if(t==null || !c.Allows(query,unit) ||
                            skip && ReferenceEquals(unit,c.Target) || visited && (c.Sequence?.Excludes(unit)==true || record?.Contains(unit.SourceIdentity)==true))continue;
                        if(remember)record.Add(unit.SourceIdentity);take--;c.Selected(unit,t,buffer.Points[i]).Execute(sub);
                    }
                }finally{c.ReturnBuffer(buffer);}
            });
        }
    }

    [Serializable] [System.ComponentModel.DisplayName("显示目标连线")]
    public sealed class ShowTargetLinkAction : SkillAction
    {
        [Tooltip("包含 LineRenderer 的预制体。")] [FlowField("连线特效")] public GameObject prefab;
        public SkillSpawnPosition from=SkillSpawnPosition.HitPoint;
        public SkillNumber duration=new(.25f);
        public override SkillActionDefinition Build(SkillActionBuildContext builder){
            builder.Number(duration,float.Epsilon);
            var visual=prefab;var origin=from;var life=duration;string id=Id;
            if(visual==null || visual.GetComponentInChildren<LineRenderer>(true)==null)throw new InvalidOperationException("连接特效需要含 LineRenderer 的预制体。");
            return Define("显示目标连线",c=>{
                var start=c.OriginAt(origin);var end=c.Services.RequireTargetPosition();
                var go=SkillSpawn.Create(c,visual,start,Quaternion.identity);if(go==null)return;
                {
                    var line=go.GetComponentInChildren<LineRenderer>(true);line.useWorldSpace=true;
                    // The prefab supplies color/width/material; bend in world space without any gameplay callback.
                    var side=Vector3.Cross(end-start,Vector3.up).normalized*Mathf.Min(.12f,(end-start).magnitude*.09f);
                    line.positionCount=5;line.SetPosition(0,start);line.SetPosition(1,Vector3.Lerp(start,end,.27f)+side);
                    line.SetPosition(2,Vector3.Lerp(start,end,.55f)-side);line.SetPosition(3,Vector3.Lerp(start,end,.78f)+side*.5f);line.SetPosition(4,end);
                    c.Start(new SpawnLifetimeTask(c,id,go,life.Resolve(c.Values),null,default,Quaternion.identity,false),SkillTaskOwnership.Scene);
                }
            });
        }
    }
}
