using System;
using System.Collections.Generic;
using UnityEditor;
using System.Linq;
using UnityEngine;
using ZFramework.GAS.Unity;
namespace ZFramework.GAS.Editor
{
    internal enum DummyLayout { 直线, 扇形, 随机, 接力 }
    // Local-space endpoints are the ones used by onHit and the next target query.
    internal readonly struct PreviewLeg
    {
        public readonly int FromUnit, ToUnit; // zero-based dummy index, -1 = caster
        public readonly Vector3 From, To;
        public readonly float Launch, Arrival;
        public readonly bool Lightning;
        public PreviewLeg(int fromUnit,int toUnit,Vector3 from,Vector3 to,float launch,float arrival,bool lightning)
        { FromUnit=fromUnit;ToUnit=toUnit;From=from;To=to;Launch=launch;Arrival=arrival;Lightning=lightning; }
        public string Caption => $"{(FromUnit<0?"施法者":"#"+(FromUnit+1))} → #{ToUnit+1} · {(Lightning?"闪电":"飞弹")} {Arrival:0.00}s";
    }
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
        private const float ImpactPulse=.15f;
        public string SourceTags="",TargetTags="";
        public int AimIndex=1; // 1-based; 0 aims at a ground point.
        public float SourceAttribute,TargetAttribute;
        private static bool TagsMatch(string tags,GameplayTags.GameplayTag[] required,GameplayTags.GameplayTag[] blocked)
        {
            var names=(tags??"").Split(new[]{',','，',' '},StringSplitOptions.RemoveEmptyEntries);
            bool Has(GameplayTags.GameplayTag tag)=>!string.IsNullOrEmpty(tag.m_Name) && names.Any(n=>n==tag.m_Name || n.StartsWith(tag.m_Name+".",StringComparison.Ordinal));
            return !(required?.Any(t=>!string.IsNullOrEmpty(t.m_Name) && !Has(t))??false) && !(blocked?.Any(Has)??false);
        }
        private readonly List<PreviewLeg> _legs=new();
        internal IReadOnlyList<PreviewLeg> Legs=>_legs;
        internal IReadOnlyList<Vector3> Targets=>_points;
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
            if(layout==DummyLayout.接力)return new Vector3(i%2==0?0:distance*.35f,0,distance*(1+i));
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
        public void Sample(SkillDocument doc,Transform caster,int count,DummyLayout layout,float distance,GameObject prefab,int level,bool showEffects,SkillPreviewTools drawing,bool showRanges=false,
            SkillParameterOverride[] castOverrides=null,bool routes=false)
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
            _hit.Clear();_legs.Clear();_points.Clear();End=doc.Root?.Animation?.Duration??0;
            for(int i=0;i<_units.Count;i++){_units[i].SetActive(i<count); if(i<count){var p=Position(layout,i,count,distance);_points.Add(p);_units[i].transform.position=caster.TransformPoint(p)+(prefab==null?Vector3.up*DummyRadius:Vector3.zero);}}
            if(doc.Root==null || (count==0 && !showEffects && !showRanges))return;
            var root=doc.Root; float now=doc.Playback.Time; var values=new EffectEvaluationContext(level,root.ResolveParameters(level,castOverrides));
            var aim=new Vector3(0,0,distance); int target=AimIndex>0 && count>0?Mathf.Clamp(AimIndex-1,0,count-1):-1; if(target>=0)aim=_points[target];
            Vector3? hitPoint=null; int hitUnit=-1;
            int FromUnit(SkillSpawnPosition origin)=>origin switch{
                SkillSpawnPosition.HitPoint=>hitUnit, SkillSpawnPosition.Target or SkillSpawnPosition.TargetPoint=>target, _=>-1 };
            void Route(Vector3 from,Vector3 to,Color color,bool pending){
                Vector3 a=caster.TransformPoint(from), b=caster.TransformPoint(to);
                if(pending)for(int i=0;i<12;i+=2)drawing.Line(Vector3.Lerp(a,b,i/12f),Vector3.Lerp(a,b,(i+1)/12f),color);
                else drawing.Line(a,b,color);
            }
            (TargetSequenceAction action,HashSet<int> seen,int limit,int used,int index,int depth,EffectEvaluationContext values) chain=default;
            var records=new Dictionary<string,HashSet<int>>();
            HashSet<int> Record(string key){
                if(string.IsNullOrEmpty(key))return new HashSet<int>();if(!records.TryGetValue(key,out var s))records.Add(key,s=new());return s;
            }
            Vector3? Origin(SkillSpawnPosition origin)=>origin switch{
                SkillSpawnPosition.HitPoint=>hitPoint,SkillSpawnPosition.Target=>target>=0?_points[target]:(Vector3?)null,
                SkillSpawnPosition.TargetPoint=>aim,SkillSpawnPosition.EventPosition=>null,_=>Vector3.zero};
            void Mark(TargetQuery authored,EffectEvaluationContext v)
            {
                var q=authored.Build();
                var origin=Origin(q.Kind==TargetQueryKind.TargetPoint?SkillSpawnPosition.TargetPoint:q.Origin);
                if(!origin.HasValue)return;
                if(showRanges)drawing.Query(q,v,caster,caster.TransformPoint(origin.Value));
                if(!TagsMatch(TargetTags,authored.requiredTags,authored.blockedTags) || count==0 || q.Kind==TargetQueryKind.Self)return;
                if(q.Kind==TargetQueryKind.FixedTarget){if(target>=0)_hit.Add(target);return;}
                var center=origin.Value;_ordered.Clear();
                for(int i=0;i<count;i++)if((authored.layerMask.value & (1<<_units[i].layer))!=0 && Contains(q,v,_points[i]-center,DummyRadius))_ordered.Add(i);
                _ordered.Sort((a,b)=>(_points[a]-center).sqrMagnitude.CompareTo((_points[b]-center).sqrMagnitude));
                for(int j=0;j<Mathf.Min(_ordered.Count,q.MaxTargets);j++)_hit.Add(_ordered[j]);
            }
            void Visit(SkillAction action,float at,EffectEvaluationContext v,int depth)
            {
                if(action==null || depth>8)return;
                float age=now-at;bool pulse=age>=0 && age<ImpactPulse;
                void Branch(SkillAction[] children,float when,EffectEvaluationContext vals){if(children!=null)foreach(var child in children)Visit(child,when,vals,depth+1);}
                void Step(bool first){
                    var seq=chain.action;if(seq==null || target<0 || chain.used>=chain.limit || (!seq.allowRepeat && chain.seen.Contains(target)))return;
                    chain.used++;chain.seen.Add(target);int last=chain.index;chain.index=first?0:last+1;
                    var parameters = TargetSequenceAction.StepParameters(chain.values.Parameters, seq.indexParameterId, seq.scaleParameterId, chain.index, seq.retention.Resolve(chain.values));
                    var stepValues = chain.values.WithParameters(parameters);
                    foreach(var child in (first?seq.first:seq.next)??Array.Empty<SkillAction>())Visit(child,at,stepValues,chain.depth+1);
                    chain.index=last;
                }
                switch(action)
                {
                    case TargetSequenceAction seq:
                        var outer=chain;chain=(seq,Record(seq.sharedRecord),Mathf.Clamp((int)seq.maxTargets.Resolve(v),0,64),0,0,depth,v);
                        Step(true);chain=outer;break;
                    case NextTargetStepAction:Step(false);break;
                    case SelectTargetsAction pick:
                        var query=pick.targets.Build();
                        var center=Origin(query.Kind==TargetQueryKind.TargetPoint?SkillSpawnPosition.TargetPoint:query.Origin);
                        if(!center.HasValue || !TagsMatch(TargetTags,pick.targets.requiredTags,pick.targets.blockedTags))break;
                        if(showRanges && pulse)drawing.Query(query,v,caster,caster.TransformPoint(center.Value));
                        int take=Math.Min(query.MaxTargets,(int)pick.count.Resolve(v)),oldTarget=target;var oldAim=aim;
                        var record=!string.IsNullOrEmpty(pick.sharedRecord)?Record(pick.sharedRecord):null;
                        var candidates=Enumerable.Range(0,count).Where(i=>query.Kind==TargetQueryKind.FixedTarget?i==oldTarget:
                            query.Kind!=TargetQueryKind.Self && (pick.targets.layerMask.value&(1<<_units[i].layer))!=0 && Contains(query,v,_points[i]-center.Value,DummyRadius))
                            .OrderBy(i=>(_points[i]-center.Value).sqrMagnitude).ToArray();
                        foreach(int unit in candidates){
                            if(take<=0 || chain.action!=null && chain.used>=chain.limit)break;
                            if(pick.excludeCurrent && unit==oldTarget || pick.excludeVisited && (record?.Contains(unit)==true ||
                                chain.action!=null && !chain.action.allowRepeat && chain.seen.Contains(unit)))continue;
                            take--;target=unit;aim=_points[unit]+Vector3.up*DummyRadius;if(pick.recordSelection)record?.Add(unit);
                            Branch(pick.actions,at,v);
                        }target=oldTarget;aim=oldAim;break;
                    case ShowTargetLinkAction link:
                        float until=at+link.duration.Resolve(v);End=Mathf.Max(End,Mathf.Max(until,at+ImpactPulse));
                        if(target>=0 && Origin(link.from) is Vector3 startLink){
                            if(routes)_legs.Add(new PreviewLeg(FromUnit(link.from),target,startLink,aim,at,at,true));
                            if(drawing!=null && (routes || showEffects && now>=at && now<until)){
                                Color color=now<at?new Color(.22f,.55f,.60f):now<until?Color.cyan:new Color(.17f,.7f,.78f);
                                Route(startLink,aim,color,now<at);
                            }
                        }
                        break;
                    case ApplyEffectAction a when pulse:Mark(a.targets,v);break;
                    case LaunchProjectileAction p:
                        if(Origin(p.origin) is not Vector3 start || p.onlySelectedTarget && target<0)break;
                        var destination=aim;float length=Vector3.Distance(start,destination);
                        float limit=p.lifetime.Resolve(v),speed=Mathf.Max(.001f,p.speed.Resolve(v)),flight=Mathf.Min(limit,length/speed);
                        Vector3 Path(float ageOfShot){
                            float traveled = ageOfShot * speed;
                            var flat = Vector3.MoveTowards(start, destination, traveled);
                            flat.y += LaunchProjectileAction.ArcOffset(p.arcHeight, traveled, length);
                            return flat;
                        }
                        // Sweep the projectile sphere against the same explicit dummy proxy used by area queries.
                        var hits=new List<(float time,int unit)>(); var previous=start;
                        float extent=DummyRadius+Mathf.Max(0,p.radius);
                        for(int step=1;step<=48;step++)
                        {
                            var next=Path(flight*step/48f);var span=next-previous;
                            for(int i=0;i<count;i++)
                            {
                                if(p.onlySelectedTarget && i!=target || (p.collision.layerMask.value & (1<<_units[i].layer))==0 || hits.Exists(h=>h.unit==i))continue;
                                var from=previous-_points[i]-Vector3.up*DummyRadius;
                                float len=span.magnitude,proj=len>0?Vector3.Dot(from,span)/len:0,c=from.sqrMagnitude-extent*extent;
                                float disc=proj*proj-c;float entry=c<=0?0:disc>=0?-proj-Mathf.Sqrt(disc):float.PositiveInfinity;
                                if(entry>=0 && entry<=len)hits.Add((at+flight*((step-1)+(len>0?entry/len:0))/48f,i));
                            }
                            previous=next;
                        }
                        hits.Sort((x,y)=>x.time!=y.time?x.time.CompareTo(y.time):x.unit.CompareTo(y.unit));
                        int accepted=TagsMatch(TargetTags,p.collision.requiredTags,p.collision.blockedTags)
                            ?Math.Min(hits.Count,Math.Max(1,p.maxHits)):0;
                        float stop=accepted==Math.Max(1,p.maxHits)?hits[accepted-1].time:at+flight;
                        End=Mathf.Max(End,stop);
                        if(drawing!=null && (routes || showEffects && age>=0 && now<stop)){
                            bool pending=now<at;
                            Color color=pending?new Color(.62f,.52f,.24f):now<stop?Color.yellow:new Color(.9f,.66f,.22f);
                            int stride=pending || !routes?2:1;
                            for(int i=0;i<24;i+=stride)drawing.Line(caster.TransformPoint(Path((stop-at)*i/24)),
                                caster.TransformPoint(Path((stop-at)*(i+1)/24)),color);
                            if(age>=0 && now<stop)drawing.Circle(caster.TransformPoint(Path(age)),Mathf.Max(.04f,p.radius),Color.yellow);
                        }
                        for(int i=0;i<accepted;i++){
                            var impact=hits[i];var projectileCenter=Path(impact.time-at);var body=_points[impact.unit]+Vector3.up*DummyRadius;
                            // ClosestPoint for an overlap inside the dummy is the projectile position itself.
                            var contact=body+Vector3.ClampMagnitude(projectileCenter-body,DummyRadius);
                            if(routes){
                                _legs.Add(new PreviewLeg(FromUnit(p.origin),impact.unit,start,contact,at,impact.time,false));
                                if(drawing!=null){
                                    Route(projectileCenter,contact,now<at?Color.gray:Color.yellow,false);
                                    drawing.Circle(caster.TransformPoint(contact),.06f,now>=impact.time?Color.yellow:Color.gray);
                                }
                            }
                            // Leave the last impact visible before a looping preview starts its next cast.
                            End=Mathf.Max(End,impact.time+ImpactPulse);
                            if(now>=impact.time && now-impact.time<ImpactPulse)_hit.Add(impact.unit);
                            int prior=target,priorUnit=hitUnit;var previousAim=aim;var priorHit=hitPoint;
                            target=hitUnit=impact.unit;hitPoint=contact;aim=_points[target];
                            Branch(p.onHit,impact.time,v);target=prior;aim=previousAim;hitPoint=priorHit;hitUnit=priorUnit;
                        }
                        break;
                    case CreateAreaAction a:
                        float life=a.duration.Resolve(v); if(life<=0)life=Mathf.Max(0,(root.Animation?.Duration??now)-at); End=Mathf.Max(End,at+life);
                        if(age>=0 && age<=life)
                        {var q=a.targets.Build();Mark(a.targets,v);float period=a.period.Resolve(v);
                         if(showEffects)
                         {
                             drawing.Query(q,v,caster,caster.TransformPoint(q.Kind==TargetQueryKind.TargetPoint?aim:Vector3.zero));
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
            }
            // Preserve authored order for simultaneous arrivals (including zero-length hops).
            if(_legs.Count>1){var ordered=_legs.OrderBy(leg=>leg.Arrival).ToArray();_legs.Clear();_legs.AddRange(ordered);}
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
