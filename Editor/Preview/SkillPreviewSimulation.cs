using System;
using System.Collections.Generic;
using UnityEditor;
using System.Linq;
using UnityEngine;
using ZFramework.GAS.Unity;
namespace ZFramework.GAS.Editor
{
    internal enum DummyLayout { 直线, 扇形, 随机 }
    // Geometric authoring preview: never executes actions, physics, AI or project callbacks.
    internal sealed class SkillPreviewSimulation : IDisposable
    {
        private readonly PreviewRenderUtility _preview;
        private readonly List<GameObject> _units=new();
        private GameObject _prefab;
        private readonly List<Vector3> _points=new();
        private readonly List<int> _ordered=new();
        private readonly HashSet<int> _hit=new();
        private readonly MaterialPropertyBlock _tint=new();
        internal const float DummyRadius=.18f;
        public string SourceTags="",TargetTags="";
        public int AimIndex=1; // 1-based; 0 aims at a ground point.
        public float SourceAttribute,TargetAttribute;
        private static bool TagsMatch(string tags,GameplayTags.GameplayTag[] required,GameplayTags.GameplayTag[] blocked)
        {
            var names=(tags??"").Split(new[]{',','，',' '},StringSplitOptions.RemoveEmptyEntries);
            bool Has(GameplayTags.GameplayTag tag)=>!string.IsNullOrEmpty(tag.m_Name) && names.Any(n=>n==tag.m_Name || n.StartsWith(tag.m_Name+".",StringComparison.Ordinal));
            return !(required?.Any(t=>!string.IsNullOrEmpty(t.m_Name) && !Has(t))??false) && !(blocked?.Any(Has)??false);
        }
        public int HitCount=>_hit.Count;
        internal bool WasHit(int dummy)=>_hit.Contains(dummy);
        public float End {get;private set;}
        public AnimationClip Reaction { get;set; }
        private readonly List<Transform[]> _bones=new();
        private readonly List<(Vector3 p,Quaternion r,Vector3 s)[]> _rest=new();
        public SkillPreviewSimulation(PreviewRenderUtility preview)=>_preview=preview;
        public static Vector3 Position(DummyLayout layout,int i,int count,float distance)
        {
            float u=count<2?.5f:i/(count-1f);
            if(layout==DummyLayout.直线)return new Vector3((u-.5f)*(count-1)*.8f,0,distance);
            if(layout==DummyLayout.扇形)return Quaternion.Euler(0,(u-.5f)*120,0)*Vector3.forward*distance;
            var rng=new System.Random(137+7919*i);
            return Quaternion.Euler(0,(float)(rng.NextDouble()*150-75),0)*Vector3.forward*(float)((.5+rng.NextDouble())*distance);
        }
        internal static bool Contains(TargetQueryDefinition q,EffectEvaluationContext v, Vector3 delta,float bodyRadius=0)
        {
            var center=delta+Vector3.up*bodyRadius;
            float r=q.Radius.Resolve(v);
            if(q.Kind==TargetQueryKind.Line){
                var nearest=new Vector3(Mathf.Clamp(center.x,-q.Width.Resolve(v)*.5f,q.Width.Resolve(v)*.5f),
                    Mathf.Clamp(center.y,-q.Height.Resolve(v)*.5f,q.Height.Resolve(v)*.5f),Mathf.Clamp(center.z,0,r));
                return (center-nearest).sqrMagnitude<=bodyRadius*bodyRadius;
            }
            if(center.sqrMagnitude>(r+bodyRadius)*(r+bodyRadius))return false;
            return q.Kind!=TargetQueryKind.Sector || delta.sqrMagnitude<1e-8f || Vector2.Angle(Vector2.up,new Vector2(delta.x,delta.z))<=q.Angle.Resolve(v)*.5f;
        }
        public void Sample(SkillDocument doc,Transform caster,int count,DummyLayout layout,float distance,GameObject prefab,int level,bool showEffects,SkillPreviewTools drawing,bool showRanges=false)
        {
            if(prefab!=_prefab){Clear();_prefab=prefab;}
            while(_units.Count<count)
            {
                GameObject go;
                if(prefab!=null)go=_preview.InstantiatePrefabInScene(prefab);
                else {go=GameObject.CreatePrimitive(PrimitiveType.Sphere);_preview.AddSingleGO(go);go.transform.localScale=Vector3.one*(2*DummyRadius);}
                go.hideFlags=HideFlags.HideAndDontSave;foreach(var c in go.GetComponentsInChildren<Collider>(true))c.enabled=false;
                foreach(var b in go.GetComponentsInChildren<Behaviour>(true))b.enabled=false;
                _units.Add(go);var bones=go.GetComponentsInChildren<Transform>(true);_bones.Add(bones);
                _rest.Add(Array.ConvertAll(bones,b=>(b.localPosition,b.localRotation,b.localScale)));
            }
            _hit.Clear();_points.Clear();End=doc.Root?.Animation?.Duration??0;
            for(int i=0;i<_units.Count;i++){_units[i].SetActive(i<count); if(i<count){var p=Position(layout,i,count,distance);_points.Add(p);_units[i].transform.position=caster.TransformPoint(p)+(prefab==null?Vector3.up*DummyRadius:Vector3.zero);}}
            if(doc.Root==null || (count==0 && !showEffects && !showRanges))return;
            var root=doc.Root; float now=doc.Playback.Time; var values=new EffectEvaluationContext(level,root.ResolveParameters(level));
            var aim=new Vector3(0,0,distance); int target=AimIndex>0 && count>0?Mathf.Clamp(AimIndex-1,0,count-1):-1; if(target>=0)aim=_points[target];
            void Mark(TargetQuery authored,EffectEvaluationContext v)
            {
                var q=authored.Build();
                if(showRanges)drawing.Query(q,v,caster,distance,caster.TransformPoint(q.Kind==TargetQueryKind.TargetPoint?aim:Vector3.zero));
                if(!TagsMatch(TargetTags,authored.requiredTags,authored.blockedTags) || count==0 || q.Kind==TargetQueryKind.Self)return;
                if(q.Kind==TargetQueryKind.FixedTarget){if(target>=0)_hit.Add(target);return;}
                var center=q.Kind==TargetQueryKind.TargetPoint?aim:Vector3.zero;_ordered.Clear();
                for(int i=0;i<count;i++)if((authored.layerMask.value & (1<<_units[i].layer))!=0 && Contains(q,v,_points[i]-center,DummyRadius))_ordered.Add(i);
                _ordered.Sort((a,b)=>(_points[a]-center).sqrMagnitude.CompareTo((_points[b]-center).sqrMagnitude));
                for(int j=0;j<Mathf.Min(_ordered.Count,q.MaxTargets);j++)_hit.Add(_ordered[j]);
            }
            void Visit(SkillAction action,float at,EffectEvaluationContext v,int depth)
            {
                if(action==null || depth>8)return;
                float age=now-at;bool pulse=age>=0 && age<.15f;
                void Branch(SkillAction[] children,float when,EffectEvaluationContext vals){if(children!=null)foreach(var child in children)Visit(child,when,vals,depth+1);}
                switch(action)
                {
                    case ApplyEffectAction a when pulse:Mark(a.targets,v);break;
                    case LaunchProjectileAction p:
                        float limit=p.lifetime.Resolve(v),speed=Mathf.Max(.001f,p.speed.Resolve(v)),flight=Mathf.Min(limit,aim.magnitude/speed);
                        End=Mathf.Max(End,at+flight);
                        if(showEffects && age>=0 && age<=flight)
                        {
                            Vector3 At(float t)=>caster.TransformPoint(aim*t+Vector3.up*(4*p.arcHeight*t*(1-t)));
                            float fraction=speed/Mathf.Max(.001f,aim.magnitude),end=Mathf.Clamp01(flight*fraction);
                            for(int i=0;i<24;i+=2)drawing.Line(At(end*i/24),At(end*(i+1)/24),Color.yellow);
                            drawing.Circle(At(Mathf.Clamp01(age*fraction)),Mathf.Max(.04f,p.radius),Color.yellow);
                        }
                        // Sweep the projectile sphere against the same explicit dummy proxy used by area queries.
                        var hits=new List<(float time,int unit)>(); var previous=Vector3.zero;
                        float extent=DummyRadius+Mathf.Max(0,p.radius);
                        for(int step=1;step<=48;step++)
                        {
                            float f=step/48f, u=Mathf.Clamp01(f*flight*speed/Mathf.Max(.00001f,aim.magnitude));
                            var next=aim*u+Vector3.up*(4*p.arcHeight*u*(1-u));var span=next-previous;
                            for(int i=0;i<count;i++)
                            {
                                if((p.collision.layerMask.value & (1<<_units[i].layer))==0 || hits.Exists(h=>h.unit==i))continue;
                                var from=previous-_points[i]-Vector3.up*DummyRadius;
                                float len=span.magnitude,proj=len>0?Vector3.Dot(from,span)/len:0,c=from.sqrMagnitude-extent*extent;
                                float disc=proj*proj-c;float entry=c<=0?0:disc>=0?-proj-Mathf.Sqrt(disc):float.PositiveInfinity;
                                if(entry>=0 && entry<=len)hits.Add((at+flight*((step-1)+(len>0?entry/len:0))/48f,i));
                            }
                            previous=next;
                        }
                        hits.Sort((x,y)=>x.time.CompareTo(y.time));
                        if(TagsMatch(TargetTags,p.collision.requiredTags,p.collision.blockedTags))
                            foreach(var impact in hits.Take(Math.Max(1,p.maxHits))){if(now>=impact.time && now-impact.time<.15f)_hit.Add(impact.unit);int prior=target;var previousAim=aim; target=impact.unit;aim=_points[target];Branch(p.onHit,impact.time,v);target=prior;aim=previousAim;}
                        break;
                    case CreateAreaAction a:
                        float life=a.duration.Resolve(v); if(life<=0)life=Mathf.Max(0,(root.Animation?.Duration??now)-at); End=Mathf.Max(End,at+life);
                        if(age>=0 && age<=life)
                        {var q=a.targets.Build();Mark(a.targets,v);float period=a.period.Resolve(v);
                         if(showEffects)
                         {
                             drawing.Query(q,v,caster,distance,caster.TransformPoint(q.Kind==TargetQueryKind.TargetPoint?aim:Vector3.zero));
                             if(period>0 && age>=period && Mathf.Repeat(age,period)<.15f)
                                 drawing.Circle(caster.TransformPoint(q.Kind==TargetQueryKind.TargetPoint?aim:Vector3.zero),q.Radius.Resolve(v),Color.yellow);
                         }
                         if(period>0 && age>=period)Branch(a.onPeriod,at+Mathf.Floor(age/period)*period,v);}
                        break;
                    case ConditionalAction c:
                        bool hasTarget=target>=0;
                        bool yes=(!c.requireTarget || hasTarget) && (!c.testTarget || hasTarget) &&
                            (string.IsNullOrEmpty(c.booleanParameterId) || v.Parameters.Get(c.booleanParameterId).Boolean) &&
                            TagsMatch(c.testTarget?TargetTags:SourceTags,c.requiredTags,c.blockedTags) &&
                            (!c.compareAttribute || ConditionalAction.Compare(c.comparison,c.testTarget?TargetAttribute:SourceAttribute,c.value.Resolve(v)));
                        Branch(yes?c.whenTrue:c.whenFalse,at,v);break;
                    case InlineParameterScopeAction inline:
                        var locals=inline.parameters.ToDictionary(x=>x.Id,x=>x.Resolve(level));
                        foreach(var b in inline.bindings)locals[b.moduleParameterId]=v.Parameters.Get(b.callerParameterId);
                        Branch(inline.actions,at,v.WithParameters(new SkillParameterValues(locals)));break;
                    case CallModuleAction c when c.module!=null:
                        var map=new Dictionary<string,SkillParameterValue>();
                        if(c.module.parameters!=null)foreach(var p in c.module.parameters.parameters) map[p.Id]=p.Resolve(level);
                        foreach(var b in c.bindings)map[b.moduleParameterId]=v.Parameters.Get(b.callerParameterId);
                        Branch(c.module.actions,at,v.WithParameters(new SkillParameterValues(map)));break;
                }
            }
            foreach(var a in root.activationActions)Visit(a,0,values,1);
            var markers=root.Animation!=null?doc.Serialized(root.Animation).FindProperty("markers"):null;
            for(int i=0;i<(markers?.arraySize??0);i++)
            {
                var m=markers.GetArrayElementAtIndex(i);float at=m.FindPropertyRelative("time").floatValue;
                var list=m.FindPropertyRelative("actions");for(int j=0;j<list.arraySize;j++) Visit(list.GetArrayElementAtIndex(j).managedReferenceValue as SkillAction,at,values,1);
                if(m.FindPropertyRelative("effect").objectReferenceValue!=null && m.FindPropertyRelative("target").enumValueIndex==(int)AnimationMarkerTarget.Target && now>=at && now-at<.15f && target>=0)_hit.Add(target);
            }
            for(int i=0;i<count;i++)
            {
                if(prefab!=null)
                {
                    var bones=_bones[i];var rest=_rest[i];for(int j=1;j<bones.Length;j++) {bones[j].localPosition=rest[j].p;bones[j].localRotation=rest[j].r;bones[j].localScale=rest[j].s;}
                    var pos=_units[i].transform.position;
                    if(_hit.Contains(i) && Reaction!=null) Reaction.SampleAnimation(_units[i],Mathf.Repeat(now,Mathf.Max(.001f,Reaction.length)));
                    _units[i].transform.position=pos;
                }
                Color tint=_hit.Contains(i)?new Color(1,.45f,.12f):new Color(.45f,.62f,.78f);
                _tint.SetColor("_Color",tint);_tint.SetColor("_BaseColor",tint);
                foreach(var renderer in _units[i].GetComponentsInChildren<Renderer>())renderer.SetPropertyBlock(_tint);
            }
        }
        private void Clear(){foreach(var go in _units)if(go!=null)UnityEngine.Object.DestroyImmediate(go);_units.Clear();_bones.Clear();_rest.Clear();}
        public void Dispose()=>Clear();
    }
}
