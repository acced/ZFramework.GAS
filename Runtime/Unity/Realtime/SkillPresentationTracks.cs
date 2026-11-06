using System;
using UnityEngine;

namespace ZFramework.GAS.Unity
{
    // A lease is per track and per cast. Project services can safely combine overlapping requests.
    public interface ISkillPresentationLease : IDisposable { void Sample(float value); }
    public interface ISkillCameraService { ISkillPresentationLease Shake(SkillExecutionContext c, Vector3 direction, float frequency); }
    public interface ISkillTimeService
    {
        ISkillPresentationLease GlobalScale(SkillExecutionContext c);
        // The service owns the wall-clock timeout, so a zero local speed cannot deadlock its own track.
        ISkillPresentationLease HitStop(SkillExecutionContext c, float realSeconds);
        float LocalScale(AbilitySystemComponent unit);
    }
    public interface ISkillRigService { ISkillPresentationLease Aim(SkillExecutionContext c, string chain); }
    [Serializable] public abstract class PresentationTrack : SkillRuntimeTrack
    {
        public AnimationCurve amount=AnimationCurve.Linear(0,1,1,1);
        protected abstract Func<SkillExecutionContext,ISkillPresentationLease> Factory();
        public override RuntimeTrackDefinition Build()
        {
            if(amount==null || amount.length==0) throw new InvalidOperationException("表现曲线不能为空。");
            return new Definition(Id,start,end,new AnimationCurve(amount.keys),Factory());
        }
        private sealed class Definition : RuntimeTrackDefinition
        {
            private readonly AnimationCurve _curve; private readonly Func<SkillExecutionContext,ISkillPresentationLease> _open;
            public Definition(string id,float start,float end,AnimationCurve curve,Func<SkillExecutionContext,ISkillPresentationLease> open):base(id,start,end){_curve=curve;_open=open;}
            public override IRuntimeTrackHandler Create(SkillExecutionContext context)=>new Handler(this,context);
            private sealed class Handler : IRuntimeTrackHandler
            {
                private readonly Definition _d; private readonly SkillExecutionContext _c; private ISkillPresentationLease _lease;
                public Handler(Definition d,SkillExecutionContext c){_d=d;_c=c;}
                public void Enter()=>_lease=_d._open(_c);
                public void Sample(float time,float u)=>_lease?.Sample(_d._curve.Evaluate(u));
                public void Exit(){var lease=_lease;_lease=null;lease?.Dispose();}
                public void Cancel()=>Exit();
            }
        }
    }
    [Serializable] public sealed class CameraShakeTrack : PresentationTrack
    {
        public Vector3 direction=Vector3.up; [Min(0)] public float frequency=16;
        protected override Func<SkillExecutionContext,ISkillPresentationLease> Factory()
        {var dir=direction;float hz=frequency;return c=>c.Services.Get<ISkillCameraService>()?.Shake(c,dir,hz);}
    }
    [Serializable] public sealed class TimeScaleTrack : PresentationTrack
    {
        protected override Func<SkillExecutionContext,ISkillPresentationLease> Factory()=>c=>c.Services.Get<ISkillTimeService>()?.GlobalScale(c);
    }
    // A hit can affect an already-running target task, whose own cast used another services object.
    internal sealed class HitStopLease : ISkillPresentationLease
    {
        private static readonly System.Collections.Generic.List<HitStopLease> Active=new();
        private ISkillPresentationLease _inner;
        private readonly AbilitySystemComponent _source,_target;
        private readonly int _sourceGeneration,_targetGeneration;
        private readonly ISkillTimeService _service;
        private HitStopLease(SkillExecutionContext c,ISkillPresentationLease inner)
        { _inner=inner;_source=c.Source;_target=c.Target;_sourceGeneration=_source?.Generation??-1;
          _targetGeneration=_target?.Generation??-1;_service=c.Services.Get<ISkillTimeService>();Active.Add(this); }
        public static ISkillPresentationLease Open(SkillExecutionContext c,float seconds)
        {var inner=c.Services.Get<ISkillTimeService>()?.HitStop(c,seconds);return inner==null?null:new HitStopLease(c,inner);}
        internal static float Scale(AbilitySystemComponent unit,ISkillTimeService fallback)
        {
            float scale=fallback?.LocalScale(unit)??1;
            // A service combines its own overlapping leases; never multiply it twice.
            for(int i=0;i<Active.Count;i++){var l=Active[i];
                if(l._service!=fallback && ((l._source==unit && l._sourceGeneration==unit.Generation) ||
                    (l._target==unit && l._targetGeneration==unit.Generation)))scale=Math.Min(scale,l._service.LocalScale(unit));}
            return scale;
        }
        public void Sample(float value)=>_inner?.Sample(value);
        public void Dispose(){var inner=_inner;if(inner==null)return;_inner=null;Active.Remove(this);inner.Dispose();}
    }
    [Serializable] public sealed class HitStopTrack : PresentationTrack
    {
        [Min(.001f)] public float realSeconds=.06f;
        public HitStopTrack(){amount=AnimationCurve.Constant(0,1,0);}
        protected override Func<SkillExecutionContext,ISkillPresentationLease> Factory()
        {var seconds=realSeconds;if(!(seconds>0) || !float.IsFinite(seconds)) throw new InvalidOperationException("顿帧须有有限的恢复时间。"); return c=>HitStopLease.Open(c,seconds);}
    }
    [Serializable] public sealed class LookAtTrack : PresentationTrack
    {
        public string boneChain="Spine/Head";
        protected override Func<SkillExecutionContext,ISkillPresentationLease> Factory(){var chain=boneChain;return c=>c.Services.Get<ISkillRigService>()?.Aim(c,chain);}
    }
}
