using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Samples.BasicCombat
{
    [Serializable] [System.ComponentModel.DisplayName("显示目标连线")]
    [MovedFrom(true, "ZFramework.GAS.Unity", "ZFramework.GAS.Unity.Realtime", "ShowTargetLinkAction")]
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
                var line=go.GetComponentInChildren<LineRenderer>(true);line.useWorldSpace=true;
                var side=Vector3.Cross(end-start,Vector3.up).normalized*Mathf.Min(.12f,(end-start).magnitude*.09f);
                line.positionCount=5;line.SetPosition(0,start);line.SetPosition(1,Vector3.Lerp(start,end,.27f)+side);
                line.SetPosition(2,Vector3.Lerp(start,end,.55f)-side);line.SetPosition(3,Vector3.Lerp(start,end,.78f)+side*.5f);line.SetPosition(4,end);
                c.Start(new LinkLifetimeTask(c, id, go, life.Resolve(c.Values)),SkillTaskOwnership.Scene);
            });
        }
    }
    internal sealed class LinkLifetimeTask : SkillActionTask
    {
        private readonly GameObject _visual;
        private float _remaining;
        public override string State => "连线表现";
        public LinkLifetimeTask(SkillExecutionContext context, string id, GameObject visual, float duration) : base(context, id)
        { _visual = visual; _remaining = duration; }
        protected override void Tick(float dt) { if ((_remaining -= dt) <= 0) Complete(); }
        protected override void EndAction(bool cancelled) => Execution.Services.Objects.Release(_visual);
    }
}
