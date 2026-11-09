using System;
using System.Collections.Generic;
using UnityEngine;
using ZFramework.GAS.Unity;
namespace ZFramework.GAS.Samples.BasicCombat
{
    // Project-specific adapters. Multiple casts never restore a scale owned by another lease.
    public sealed class ArenaPresentation : MonoBehaviour, ISkillCameraService, ISkillTimeService, ISkillRigService
    {
        private enum Kind { Camera, Global, Local, Aim }
        private sealed class Lease : ISkillPresentationLease
        {
            public ArenaPresentation Host; public Kind Type; public float Value=1, Hz, Until;
            public AbilitySystemComponent Unit, Target; public Vector3 Direction;
            public Transform Bone, AimTarget; public Animator PassiveTarget;
            public void Sample(float value){ Value=value; if(Type==Kind.Global && Host!=null) Host.RefreshScale(); }
            public void Dispose(){ var host=Host; Host=null; if(host!=null) host.Remove(this); }
        }
        private readonly List<Lease> _live=new(); private bool _ownsScale; private float _saved,_written;
        private Lease Open(Kind kind){var l=new Lease{Host=this,Type=kind};_live.Add(l);return l;}
        public ISkillPresentationLease Shake(SkillExecutionContext c,Vector3 direction,float frequency)
        {var l=Open(Kind.Camera);l.Direction=direction;l.Hz=frequency;return l;}
        public Vector3 CameraOffset()
        {var v=Vector3.zero; foreach(var l in _live)if(l.Type==Kind.Camera)v+=l.Value*l.Direction*Mathf.Sin(Time.unscaledTime*l.Hz*2*Mathf.PI);return v;}
        public ISkillPresentationLease GlobalScale(SkillExecutionContext c)
        {if(!_ownsScale){_saved=_written=Time.timeScale;_ownsScale=true;}return Open(Kind.Global);}
        public ISkillPresentationLease HitStop(SkillExecutionContext c,float seconds)
        {var l=Open(Kind.Local);l.Unit=c.Source;l.Target=c.Target; l.PassiveTarget=c.Services.Target!=null?c.Services.Target.GetComponentInParent<Animator>():null;l.Until=Time.unscaledTime+seconds;return l;}
        public float LocalScale(AbilitySystemComponent unit)
        {float scale=1;foreach(var l in _live)if(l.Type==Kind.Local && Time.unscaledTime<l.Until && (l.Unit==unit || l.Target==unit))scale*=Mathf.Clamp01(l.Value);return scale;}
        public ISkillPresentationLease Aim(SkillExecutionContext c,string path)
        {
            var t=c.Services.Origin; if(t==null || c.Services.Target==null)return null;
            Transform bone=t.Find(path);if(bone==null)foreach(var b in t.GetComponentsInChildren<Transform>())if(b.name==path){bone=b;break;}
            if(bone==null)return null;var l=Open(Kind.Aim);l.Bone=bone;l.AimTarget=c.Services.Target;return l;
        }
        private readonly Dictionary<Animator,(float original,float written)> _passiveScale=new();
        private readonly List<Animator> _restore=new();
        private void Update()
        {
            _restore.Clear();foreach(var pair in _passiveScale)_restore.Add(pair.Key);
            foreach(var l in _live)
            {
                var a=l.PassiveTarget;
                if(a==null || l.Target==null || Time.unscaledTime>=l.Until)continue;
                bool driven=false;for(int i=0;i<l.Target.ActiveTaskCount;i++)if(l.Target.GetActiveTask(i) is AnimationTask){driven=true;break;}
                if(driven)continue;
                float current=a.speed;float basis=_passiveScale.TryGetValue(a,out var v) && Mathf.Approximately(current,v.written)?v.original:current;
                float speed=basis*LocalScale(l.Target);a.speed=speed;_passiveScale[a]=(basis,speed);_restore.Remove(a);
            }
            foreach(var a in _restore)Restore(a);
        }
        private void Restore(Animator a){var v=_passiveScale[a];if(a!=null && Mathf.Approximately(a.speed,v.written))a.speed=v.original;_passiveScale.Remove(a);}
        private void LateUpdate()
        {
            foreach(var l in _live)if(l.Type==Kind.Aim && l.Bone!=null && l.AimTarget!=null)
            {var dir=l.AimTarget.position-l.Bone.position;if(dir.sqrMagnitude>.0001f)l.Bone.rotation=Quaternion.Slerp(l.Bone.rotation,Quaternion.LookRotation(dir),Mathf.Clamp01(l.Value));}
        }
        private void Remove(Lease l){_live.Remove(l);if(l.Type==Kind.Global)RefreshScale();}
        private void RefreshScale()
        {
            if(!_ownsScale)return;
            if(!Mathf.Approximately(Time.timeScale,_written))_saved=Time.timeScale;
            bool any=false;float v=_saved;foreach(var l in _live)if(l.Type==Kind.Global){v*=Mathf.Clamp(l.Value,.05f,4);any=true;}
            Time.timeScale=_written=v;_ownsScale=any;
        }
        private void OnDisable(){for(int i=_live.Count-1;i>=0;i--)_live[i].Dispose();_restore.Clear();foreach(var a in _passiveScale.Keys)_restore.Add(a);foreach(var a in _restore)Restore(a);}
    }
}
