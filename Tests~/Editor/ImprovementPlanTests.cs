using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class ImprovementPlanTests
    {
        [Test] public void BlendPresetsHaveBoundedWeightsAndDefinitionsKeepAuthoredCurve()
        {
            foreach(SkillBlendCurve curve in Enum.GetValues(typeof(SkillBlendCurve)))
            {
                var blend=new SkillBlend(.2f,curve,custom:AnimationCurve.Linear(0,0,1,1));
                Assert.AreEqual(0,blend.Evaluate(0));Assert.AreEqual(1,blend.Evaluate(1));
                for(int i=0;i<=100;i++) Assert.That(blend.Evaluate(i/100f),Is.InRange(0f,1f));
            }
            var custom=AnimationCurve.Linear(0,0,1,1);var spec=new SkillAnimationDefinition("a",1,blendInSpec:new SkillBlend(.5f,SkillBlendCurve.Custom,custom:custom));
            custom.keys=AnimationCurve.Constant(0,1,0).keys;Assert.AreEqual(.5f,spec.BlendInSpec.Evaluate(.5f),.001f);
            Assert.Throws<ArgumentOutOfRangeException>(()=>new AnimationSectionDefinition("a","a",0,.25f,null,0,.25f,new SkillBlend(.3f)));
        }
        private sealed class SetChannel : SkillActionDefinition
        { public SetChannel():base("set","set"){}
          public override void Execute(SkillExecutionContext c)=>c.SetChannel("proof",2); }
        [Test] public void EasingPresetsHaveDistinctInteriorWeights()
        {
            Assert.AreEqual(.125f,new SkillBlend(1,SkillBlendCurve.QuadInOut).Evaluate(.25f),1e-5f);
            Assert.AreEqual(.875f,new SkillBlend(1,SkillBlendCurve.CubicOut).Evaluate(.5f),1e-5f);
            Assert.AreEqual(.5f,new SkillBlend(1,SkillBlendCurve.ExpoInOut).Evaluate(.5f),1e-5f);
            Assert.Less(new SkillBlend(1,SkillBlendCurve.BackIn).Evaluate(.5f),.1f);
        }
        [Test] public void MovingAcrossBranchesKeepsIdsAndUndoRestoresOriginalOrder()
        {
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            try
            {
                var first=new ConditionalAction();var second=new ApplyEffectAction();
                root.activationActions=new SkillAction[]{first,second};
                string prefix="activationActions.Array.data[0]";
                SkillFlowCommands.Move(null,root,"activationActions.Array.data[1]",prefix+".whenFalse",-1);
                Assert.AreEqual(1,root.activationActions.Length);
                Assert.AreEqual(second.Id,((ConditionalAction)root.activationActions[0]).whenFalse[0].Id);
                Undo.PerformUndo(); Assert.AreEqual(2,root.activationActions.Length);Assert.AreEqual(second.Id,root.activationActions[1].Id);
                Undo.PerformRedo(); Assert.AreEqual(1,root.activationActions.Length);
                using var so=new SerializedObject(root);SkillActionClipboard.Copy(root.activationActions[0]);
                SkillActionClipboard.Paste(so.FindProperty("activationActions"),1);so.ApplyModifiedProperties();
                var copy=(ConditionalAction)root.activationActions[1]; Assert.AreNotEqual(first.Id,copy.Id); Assert.AreNotEqual(second.Id,copy.whenFalse[0].Id);
            }
            finally{UnityEngine.Object.DestroyImmediate(root);}
        }
        [Test] public void DuplicatingAnActionPreservesThePreviousClipboard(){
            var m=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
            try{
                SkillActionClipboard.Copy(new ConditionalAction());
                using var so=new SerializedObject(m);var list=so.FindProperty("actions");
                SkillActionClipboard.Duplicate(list,0,new SendGameplayEventAction());
                SkillActionClipboard.Paste(list,1);so.ApplyModifiedProperties();
                Assert.IsInstanceOf<SendGameplayEventAction>(m.actions[0]);Assert.IsInstanceOf<ConditionalAction>(m.actions[1]);
                Assert.AreNotEqual(m.actions[0].Id,m.actions[1].Id);
            }finally{UnityEngine.Object.DestroyImmediate(m);}
        }
        [Test] public void DetachedModuleAndLegacySpawnerAreRejectedAndAliasedActionsCannotBuild()
        {
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            var module=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
            try{
                var spawner=new WaitSkillAction();
                var wait=new WaitSkillAction{onCancel=new SkillAction[]{spawner}};
                root.activationActions=new SkillAction[]{wait};using var data=new SerializedObject(root);
                Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.ValidateOwner(data,null));
                module.actions=new SkillAction[]{new WaitSkillAction()};
                wait.onCancel=new SkillAction[]{new CallModuleAction{module=module}};data.Update();
                Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.ValidateOwner(data,null));
                var shared=new WaitSkillAction();root.activationActions=new SkillAction[]{new ConditionalAction{whenTrue=new SkillAction[]{shared},whenFalse=new SkillAction[]{shared}}};
                Assert.Throws<SkillActionException>(()=>root.BuildActions());
            }finally{UnityEngine.Object.DestroyImmediate(root);UnityEngine.Object.DestroyImmediate(module);}
        }
        [Test] public void DetachedBranchRejectsWaitAndRollsBackMove()
        {
            var root=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            try{
                var wait=new WaitSkillAction();root.activationActions=new SkillAction[]{new LaunchProjectileAction(),wait};
                Assert.Throws<InvalidOperationException>(()=>SkillFlowCommands.Move(null,root,"activationActions.Array.data[1]","activationActions.Array.data[0].onHit",0));
                Assert.AreEqual(2,root.activationActions.Length);Assert.AreEqual(0,((LaunchProjectileAction)root.activationActions[0]).onHit.Length);
            }finally{UnityEngine.Object.DestroyImmediate(root);}
        }
        [Test] public void PreviewUsesSameSectorConeAndLineBounds()
        {
            var sector=new TargetQuery{kind=TargetQueryKind.Sector,radius=new SkillNumber(3),angle=new SkillNumber(90)}.Build();var values=new EffectEvaluationContext(1);
            Assert.IsTrue(SkillPreviewSimulation.Contains(sector,values,Vector3.forward*2));
            Assert.IsFalse(SkillPreviewSimulation.Contains(sector,values,Vector3.right*2));Assert.IsFalse(SkillPreviewSimulation.Contains(sector,values,Vector3.forward*4));
            var line=new TargetQuery{kind=TargetQueryKind.Line,radius=new SkillNumber(3),width=new SkillNumber(1)}.Build();
            Assert.IsFalse(SkillPreviewSimulation.Contains(line,values,new Vector3(.51f,0,2)));Assert.IsTrue(SkillPreviewSimulation.Contains(line,values,new Vector3(.49f,0,2)));
        }
        [Test] public void RingRetainsLatest4096InOrder()
        {
            var history=new SkillHistory();
            for(int i=0;i<4107;i++) history.Add(new SkillTrace(TraceKind.动作开始,"记录",cast:i));
            Assert.AreEqual(4096,history.Count);Assert.AreEqual(11,history[0].Cast); Assert.AreEqual(4106,history[4095].Cast);
        }
        private sealed class PresentationProbe:ISkillCameraService,ISkillTimeService,ISkillRigService,ISkillPresentationLease
        {
            public int Opened,Closed;public float Last;
            private ISkillPresentationLease Open(){Opened++;return this;}
            public ISkillPresentationLease Shake(SkillExecutionContext c,Vector3 dir,float hz)=>Open();
            public ISkillPresentationLease GlobalScale(SkillExecutionContext c)=>Open();
            public ISkillPresentationLease HitStop(SkillExecutionContext c,float seconds)=>Open();
            public float LocalScale(AbilitySystemComponent u)=>1;
            public ISkillPresentationLease Aim(SkillExecutionContext c,string chain)=>Open();
            public void Sample(float value)=>Last=value;
            public void Dispose()=>Closed++;
        }
        private sealed class CaptureDriver : IAnimationDriver
        {
            public string Last;public float Play(string name,float duration,bool loop){Last=name;return 1;}
            public void Advance(float t){} public void Stop(){}
        }
        [Serializable] private sealed class CaptureBackend : AnimationBackend
        {
            public override string BackendId=>"test";
            public override bool Validate(GameObject go,out string error){error=null;return true;}
            public override IAnimationDriver CreateDriver(GameObject go)=>new CaptureDriver();
        }
        [Test] public void EquipmentOverridesSkillVariantAndInvalidEntriesFallThrough()
        {
            var go=new GameObject();var host=go.AddComponent<SkillAnimator>();host.SetBackend(new CaptureBackend());
            var driver=(CaptureDriver)host.Driver;var skill=ScriptableObject.CreateInstance<GameplayAbilityAsset>();
            SkillClipOverrideSet Set(string dest){
                var result=ScriptableObject.CreateInstance<SkillClipOverrideSet>();
                result.clips=new[]{new SkillClipOverrideSet.Entry{original="base",replacement=dest}};return result;}
            var basis=skill.clipOverrides=Set("variant");var equipment=Set("equipment");var invalid=Set(null);var clip=new AnimationClip();
            invalid.clips[0].clip=clip; // This backend accepts names only.
            try{
                var asc=new AbilitySystemComponent();var spec=asc.GrantAbility(new GameplayAbilityDefinition());
                var c=new SkillExecutionContext(skill,spec,null,new EffectEvaluationContext(1),null);
                string Play(){_=new AnimationTask(new SkillAnimationDefinition("base",1),driver,taskContext:c.Services);return driver.Last;}
                Assert.AreEqual("variant",Play());host.PushOverride(equipment);host.PushOverride(invalid);
                Assert.AreEqual("equipment",Play());host.PopOverride(equipment);Assert.AreEqual("variant",Play());
                skill.clipOverrides=null;Assert.AreEqual("base",Play());
            }finally{foreach(var o in new UnityEngine.Object[]{go,skill,basis,equipment,invalid,clip})UnityEngine.Object.DestroyImmediate(o);}
        }
        [Test] public void ArenaHitStopAndTimeScaleRespectParticipantsOverlapsAndExternalScale()
        {
            var go=new GameObject();var service=go.AddComponent<ZFramework.GAS.Samples.BasicCombat.ArenaPresentation>();
            var owners=new[]{new AbilitySystemComponent(),new AbilitySystemComponent(),new AbilitySystemComponent()};
            var tasks=new AnimationTask[3];SkillExecutionContext context=null;
            float saved=Time.timeScale;
            try{
                for(int i=0;i<owners.Length;i++){
                    var spec=owners[i].GrantAbility(new GameplayAbilityDefinition());owners[i].TryActivateAbility(spec);
                    tasks[i]=new AnimationTask(new SkillAnimationDefinition("clip",1),new CaptureDriver());owners[i].StartTask(spec,tasks[i]);
                    if(i==0)context=new SkillExecutionContext(null,spec,owners[1],new EffectEvaluationContext(1),new TimelineTaskContext{Time=service});}
                var h=new HitStopTrack{realSeconds=10}.Build().Create(context);h.Enter();h.Sample(0,0);
                try{
                    foreach(var asc in owners)asc.Tick(.2f);
                    Assert.AreEqual(0,tasks[0].NormalizedTime);Assert.AreEqual(0,tasks[1].NormalizedTime);Assert.AreEqual(.2f,tasks[2].NormalizedTime,.001f);
                }finally{h.Cancel();}
                owners[1].Tick(.1f);Assert.AreEqual(.1f,tasks[1].NormalizedTime,.001f);
                Time.timeScale=.8f;using var a=service.GlobalScale(context);a.Sample(.5f);using var b=service.GlobalScale(context);b.Sample(.25f);
                Assert.AreEqual(.1f,Time.timeScale,.001f);Time.timeScale=.6f;b.Dispose();
                Assert.AreEqual(.3f,Time.timeScale,.001f);a.Dispose();Assert.AreEqual(.6f,Time.timeScale,.001f);
            }finally{foreach(var asc in owners)asc.Clear();UnityEngine.Object.DestroyImmediate(go);Time.timeScale=saved;}
        }
        [Test] public void PresentationTracksReleaseEachLeaseOnceOnExitOrCancellation()
        {
            var source=new AbilitySystemComponent();
            var spec=source.GrantAbility(new GameplayAbilityDefinition());
            var probe=new PresentationProbe();var values=new EffectEvaluationContext(1);
            var context=new SkillExecutionContext(null,spec,null,values,new TimelineTaskContext{Camera=probe,Time=probe,Rig=probe});
            var noServices=new SkillExecutionContext(null,spec,null,values,null);
            foreach(var track in new PresentationTrack[]{new CameraShakeTrack(),new HitStopTrack(),new TimeScaleTrack(),new LookAtTrack()})
            {
                track.amount=AnimationCurve.Linear(0,0,1,1);var definition=track.Build();
                var h=definition.Create(context);
                h.Enter();h.Sample(.5f,.5f);Assert.AreEqual(.5f,probe.Last,.001f);h.Exit();h.Cancel();
                h.Enter();h.Cancel();h.Exit();
                var silent=definition.Create(noServices);silent.Enter();silent.Sample(.2f,.2f);silent.Cancel();
            }
            Assert.AreEqual(8,probe.Opened);Assert.AreEqual(8,probe.Closed);
        }
        [Test] public void ThreeWayMergeKeepsIndependentFieldsAndFlagsConflictingEditsAndLayout()
        {
            SkillSemanticDiff.Snapshot S(string a,string b,string layout)=>new SkillSemanticDiff.Snapshot{layout=layout,entries=new List<SkillSemanticDiff.Entry>{
                new SkillSemanticDiff.Entry{key="actions[id=A]/damage",value=a},new SkillSemanticDiff.Entry{key="actions[id=B]/radius",value=b}}};
            var conflicts=new List<string>();var basis=S("5","1","original");var ours=S("8","1","left");
            var merged=SkillSemanticDiff.Merge(basis,ours,S("5","2","original"),conflicts);
            Assert.IsEmpty(conflicts);CollectionAssert.AreEqual(new[]{"8","2"},merged.entries.Select(e=>e.value));
            SkillSemanticDiff.Merge(basis,ours,S("6","2","right"),conflicts);
            CollectionAssert.AreEquivalent(new[]{"actions[id=A]/damage","@layout"},conflicts);
        }
    }
}
