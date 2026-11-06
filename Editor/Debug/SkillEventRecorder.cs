using System;
using System.Collections.Generic;
using GameplayTags;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    internal enum TraceKind { 动作开始, 动作结束, 效果, 标签, 属性, Cue, 事件, 失败, 条件分支, 标记 }
    internal readonly struct SkillTrace
    {
        private static long _serial;
        public readonly long Serial;
        public readonly int Frame, Cast;
        public readonly double Time;
        public readonly TraceKind Kind;
        private readonly string _label;
        private readonly GameplayTag _tag;
        private readonly int _number;
        private readonly float _before,_after;
        public string Label => Kind==TraceKind.属性 ? AttributeCatalog.Name(_number)+$" {_before:g4} → {_after:g4}" :
            Kind==TraceKind.标签 ? _tag.Name+" ×"+_number : _label;
        public readonly string RawId, NavigationId;
        public readonly GameplayAbilityAsset Skill;
        public SkillTrace(TraceKind kind,string label,GameplayAbilityAsset skill=null,string raw=null,string nav=null,int cast=0, GameplayTag tag=default, int number=0,float before=0,float after=0)
        { Serial=++_serial; Frame=UnityEngine.Time.frameCount; Time=EditorApplication.timeSinceStartup; Kind=kind; _label=label;_tag=tag;_number=number;_before=before;_after=after; Skill=skill; RawId=raw; NavigationId=nav; Cast=cast; }
    }
    internal sealed class SkillHistory
    {
        private SkillTrace[] _entries=Array.Empty<SkillTrace>();
        public long Sequence {get;private set;}
        public int Count => (int)Math.Min(4096,Sequence);
        public void Add(SkillTrace value) { if (Count == _entries.Length && Count < 4096) Array.Resize(ref _entries, Math.Max(16,Count*2)); _entries[(int)(Sequence++ & 4095)]=value; }
        public SkillTrace this[int index] => index>=0 && index<Count ? _entries[(int)((Sequence-Count+index)&4095)] : throw new ArgumentOutOfRangeException();
    }
    internal static class SkillEventRecorder
    {
        private sealed class Unit
        {
            public readonly AbilitySystemComponent Owner;
            public readonly int Generation;
            public readonly SkillHistory History=new();
            public Unit(AbilitySystemComponent owner)
            { Owner=owner; Generation=owner.Generation; owner.OwnedTags.OnAnyTagCountChange+=Tag; owner.Attributes.OnAttributeChanged+=Attribute; owner.Events.Received+=Message; }
            private void Tag(GameplayTag tag,int count) { History.Add(Scoped(TraceKind.标签,null,tag:tag,number:count)); }
            private void Attribute(GameplayAttribute a,float before,float after) { History.Add(Scoped(TraceKind.属性,null,number:a.Id.Value,before:before,after:after)); }
            private void Message(GameplayEvent e) { History.Add(Scoped(TraceKind.事件,e.Tag.Name+" "+e.Payload)); }
            public void Dispose(){Owner.OwnedTags.OnAnyTagCountChange-=Tag; Owner.Attributes.OnAttributeChanged-=Attribute; Owner.Events.Received-=Message;}
        }
        private static Unit _unit;
        private static int _clients;
        private static readonly Stack<(SkillExecutionContext context,string id)> Actions=new();
        private static SkillTrace Scoped(TraceKind kind,string label,GameplayAbilityAsset fallback=null,GameplayTag tag=default,int number=0,float before=0,float after=0)
        {
            if(Actions.Count==0)return new SkillTrace(kind,label,fallback,tag:tag,number:number,before:before,after:after);
            var (c,id)=Actions.Peek();return new SkillTrace(kind,label,c.Asset,id,c.EditorCallSiteId ?? id,c.Version,tag,number,before,after);
        }
        internal static SkillHistory Of(AbilitySystemComponent owner) =>
            owner!=null && _unit?.Owner==owner && _unit.Generation==owner.Generation ? _unit.History : null;
        internal static void Observe(AbilitySystemComponent owner)
        {
            if (_clients==0 || (owner!=null && Of(owner)!=null)) return;
            _unit?.Dispose(); _unit=owner==null?null:new Unit(owner);
        }
        private static void Add(AbilitySystemComponent owner, SkillTrace entry) => Of(owner)?.Add(entry);
        public static void Acquire()
        {
            if(_clients++!=0) return;
            SkillActionDebug.Boundary+=Begin; SkillActionDebug.ActionFinished+=End;
            SkillActionDebug.BranchSelected+=Branch; SkillActionDebug.PointReached+=Point;
            GasDebugHooks.Applied+=Applied; GasDebugHooks.Removed+=Removed; GasDebugHooks.Refreshed+=Refreshed; GasDebugHooks.Stacked+=Stack; GasDebugHooks.PeriodExecuted+=Period;
            GasDebugHooks.Cue+=Cue;
        }
        public static void Release()
        {
            if(--_clients!=0) return;
            SkillActionDebug.Boundary-=Begin; SkillActionDebug.ActionFinished-=End;
            SkillActionDebug.BranchSelected-=Branch; SkillActionDebug.PointReached-=Point;
            GasDebugHooks.Applied-=Applied; GasDebugHooks.Removed-=Removed; GasDebugHooks.Refreshed-=Refreshed; GasDebugHooks.Stacked-=Stack; GasDebugHooks.PeriodExecuted-=Period;
            GasDebugHooks.Cue-=Cue; Clear();
        }
        internal static void Clear(){_unit?.Dispose();_unit=null;Actions.Clear();}
        private static void Begin(SkillExecutionContext c,SkillActionDefinition a)
        {Actions.Push((c,a.Id));if(Of(c.Source)!=null)Add(c.Source,new SkillTrace(TraceKind.动作开始,a.Label,c.Asset,a.Id,c.EditorCallSiteId ?? a.Id,c.Version));}
        private static void Branch(SkillExecutionContext c,string id,bool taken){if(Of(c.Source)!=null)Add(c.Source,new SkillTrace(TraceKind.条件分支,taken?"满足":"不满足",c.Asset,id,c.EditorCallSiteId ?? id,c.Version));}
        private static void Point(SkillExecutionContext c,string id,string label)
        {if(c!=null && Of(c.Source)!=null)Add(c.Source,new SkillTrace(TraceKind.标记,label,c.Asset,id,id,c.Version));}
        private static void End(SkillExecutionContext c,SkillActionDefinition a,Exception e)
        {if(Actions.Count>0)Actions.Pop();if(Of(c.Source)!=null)Add(c.Source,new SkillTrace(e==null?TraceKind.动作结束:TraceKind.失败,e?.Message??a.Label,c.Asset,a.Id,c.EditorCallSiteId ?? a.Id,c.Version));}
        private static void Cue(GameplayCueParameters e){ if(Of(e.Target as AbilitySystemComponent)!=null) _unit.History.Add(Scoped(TraceKind.Cue,e.CueTag.Name+" "+e.Event)); }
        private static void Effect(AbilitySystemComponent u,GameplayEffectDefinition d,string what,int n)
        {
            if(Of(u)==null)return;
            var asset=DefinitionSource.Of(d);
            _unit.History.Add(Scoped(TraceKind.效果,what+" · "+(asset!=null?asset.name:"效果")+" ×"+n));
        }
        private static void Refreshed(AbilitySystemComponent u,GameplayEffectDefinition d,object s,int n)=>Effect(u,d,"刷新",n);
        private static void Applied(AbilitySystemComponent u,GameplayEffectDefinition d,object s,int n)=>Effect(u,d,"施加",n);
        private static void Removed(AbilitySystemComponent u,GameplayEffectDefinition d,object s,int n)=>Effect(u,d,"移除",n);
        private static void Stack(AbilitySystemComponent u,GameplayEffectDefinition d,object s,int n)=>Effect(u,d,"叠层",n);
        private static void Period(AbilitySystemComponent u,GameplayEffectDefinition d,object s,int n)=>Effect(u,d,"周期",n);
    }
}
