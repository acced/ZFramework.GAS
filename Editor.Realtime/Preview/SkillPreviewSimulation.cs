using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using GameplayTags;
using ZFramework.GAS.Unity;
namespace ZFramework.GAS.Editor
{
    internal enum DummyLayout { 直线, 扇形, 随机 }
    /// <summary>One projected contact, in caster-local space. Indices refer to the supplied targets; -1 is the caster.</summary>
    public readonly struct SkillPreviewContact
    {
        public readonly SkillAction Action;
        public readonly int FromUnit, ToUnit;
        public readonly Vector3 From, To;
        public readonly float Launch, Arrival;
        internal SkillPreviewContact(SkillAction action,int fromUnit,int toUnit,Vector3 from,Vector3 to,float launch,float arrival)
        { Action=action; FromUnit=fromUnit; ToUnit=toUnit; From=from; To=to; Launch=launch; Arrival=arrival; }
    }
    // Geometric authoring preview: never executes actions, physics, AI or project callbacks.
    public sealed class SkillPreviewSimulation : IDisposable
    {
        private readonly PreviewRenderUtility _preview;
        private readonly List<GameObject> _units=new();
        private GameObject _prefab;
        private readonly List<Vector3> _points=new();
        private readonly List<int> _ordered=new();
        private readonly HashSet<int> _hit=new();
        private readonly MaterialPropertyBlock _tint=new();
        internal const float DummyRadius=.18f;
        internal const float ImpactPulse=.15f;
        public string SourceTags="",TargetTags="";
        public int AimIndex=1; // 1-based; 0 aims at a ground point.
        public float SourceAttribute,TargetAttribute;
        private readonly GameplayTagCountContainer _sourceTags = new(), _targetTags = new();
        private string _lastSourceTags, _lastTargetTags;
        private static void SetTags(GameplayTagCountContainer tags, string text, ref string previous)
        {
            if (text == previous) return;
            tags.Clear();
            foreach (string name in (text ?? "").Split(new[]{',','，',' '}, StringSplitOptions.RemoveEmptyEntries))
                if (GameplayTagManager.RequestTag(name, out var tag)) tags.AddTag(tag);
            previous = text;
        }
        private Action<SkillPreviewContact> _contact;
        internal IReadOnlyList<Vector3> Targets=>_points;
        public int HitCount=>_hit.Count;
        public string SourceNote { get; private set; }
        internal bool WasHit(int dummy)=>_hit.Contains(dummy);
        public float End {get;private set;}
        public AnimationClip Reaction { get;set; }
        private readonly List<Transform[]> _bones=new();
        private readonly List<(Vector3 p,Quaternion r,Vector3 s)[]> _rest=new();
        public SkillPreviewSimulation() { }
        internal SkillPreviewSimulation(PreviewRenderUtility preview)=>_preview=preview;
        internal static Vector3 Position(DummyLayout layout,int i,int count,float distance)
        {
            float u=count<2?.5f:i/(count-1f);
            if(layout==DummyLayout.直线)return new Vector3((u-.5f)*(count-1)*.8f,0,distance);
            if(layout==DummyLayout.扇形)return Quaternion.Euler(0,(u-.5f)*120,0)*Vector3.forward*distance;
            var rng=new System.Random(137+7919*i);
            return Quaternion.Euler(0,(float)(rng.NextDouble()*150-75),0)*Vector3.forward*(float)((.5+rng.NextDouble())*distance);
        }
        internal static bool Contains(TargetQueryDefinition q,EffectEvaluationContext v, Vector3 delta,float bodyRadius=0)
            => Contains(q.ResolveShape(v), delta, bodyRadius);
        private static bool Contains(TargetShape shape, Vector3 delta, float bodyRadius)
        {
            var center=delta+Vector3.up*bodyRadius;
            float r=shape.Radius;
            if(shape.Kind==TargetQueryKind.Line){
                var half = shape.HalfExtents;
                var nearest=new Vector3(Mathf.Clamp(center.x,-half.x,half.x),
                    Mathf.Clamp(center.y,-half.y,half.y),Mathf.Clamp(center.z,0,r));
                return (center-nearest).sqrMagnitude<=bodyRadius*bodyRadius;
            }
            if(center.sqrMagnitude>(r+bodyRadius)*(r+bodyRadius))return false;
            return shape.IncludesDirection(Vector2.up,delta);
        }
        // ── Traversal state shared by Visit and its helpers ──
        private sealed class Traversal
        {
            public SkillPreviewSimulation Sim;
            public GameplayAbilityAsset Root;
            public Transform Caster;
            public SkillPreviewTools Drawing;
            public float Now;
            public int Count;
            public bool ShowEffects;
            // Mutable per-branch cursor
            public int Target, HitUnit;
            public Vector3 Aim;
            public Vector3? HitPoint;
            public (TargetSequenceAction action,HashSet<int> seen,int limit,int used,int index,int depth,EffectEvaluationContext values) Chain;
            public Dictionary<string,HashSet<int>> Records = new();
            public HashSet<int> Record(string key)
            {
                if(string.IsNullOrEmpty(key))return new HashSet<int>();
                if(!Records.TryGetValue(key,out var s))Records.Add(key,s=new());return s;
            }
            public int FromUnit(SkillSpawnPosition origin)=>origin switch{
                SkillSpawnPosition.HitPoint=>HitUnit, SkillSpawnPosition.Target or SkillSpawnPosition.TargetPoint=>Target, _=>-1 };
            public Vector3? Origin(SkillSpawnPosition origin)=>origin switch{
                SkillSpawnPosition.HitPoint=>HitPoint, SkillSpawnPosition.Target=>Target>=0?Sim._points[Target]:(Vector3?)null,
                SkillSpawnPosition.TargetPoint=>Aim, SkillSpawnPosition.EventPosition=>null, _=>Vector3.zero};
            public void Candidates(TargetQueryDefinition q, TargetShape shape, Vector3? origin, bool draw, float yaw = 0)
            {
                Sim._ordered.Clear();
                if (draw && origin.HasValue && TargetQuery.IsVolume(q.Kind)) Drawing?.Query(shape,Caster,Caster.TransformPoint(origin.Value),yaw);
                if (q.Kind == TargetQueryKind.Self || !q.MatchesTags(Sim._targetTags)) return;
                if (q.Kind == TargetQueryKind.FixedTarget) { if (Target >= 0) Sim._ordered.Add(Target); return; }
                if (q.Kind == TargetQueryKind.Project) { Sim.SourceNote = "项目来源「" + q.SourceKey + "」的命中仅在运行时确定。"; return; }
                if (q.Kind == TargetQueryKind.Hitbox)
                {
                    if (Caster == null || !SkillColliderVolume.TryRead(Caster.GetComponent<SkillHitboxes>()?.Find(q.SourceKey), out var volume))
                    { Sim.SourceNote = "预览角色没有可用的碰撞体槽位：" + q.SourceKey; return; }
                    if (draw) Drawing?.Hitbox(volume);
                    for (int i = 0; i < Count; i++)
                    {
                        var point = Caster.TransformPoint(Sim._points[i]) + Vector3.up * DummyRadius;
                        if ((q.LayerMask.value & (1 << Sim.LayerOf(i))) != 0 && volume.TouchesSphere(point, DummyRadius)) Sim._ordered.Add(i);
                    }
                    var worldCenter = volume.Center;
                    Sim._ordered.Sort((a,b) => (Caster.TransformPoint(Sim._points[a])-worldCenter).sqrMagnitude.CompareTo((Caster.TransformPoint(Sim._points[b])-worldCenter).sqrMagnitude));
                    return;
                }
                if (origin is not Vector3 center) return;
                var inverse = Quaternion.Euler(0, -yaw, 0);
                for(int i=0;i<Count;i++)
                    if((q.LayerMask.value & (1<<Sim.LayerOf(i)))!=0 && Contains(shape,inverse*(Sim._points[i]-center),DummyRadius)) Sim._ordered.Add(i);
                Sim._ordered.Sort((a,b) => {
                    int order=(Sim._points[a]-center).sqrMagnitude.CompareTo((Sim._points[b]-center).sqrMagnitude);
                    return order!=0 ? order : a.CompareTo(b);
                });
            }
            public void Mark(TargetQuery authored, EffectEvaluationContext v)
            {
                var q=authored.Build();
                Candidates(q,q.ResolveShape(v),Origin(q.EffectiveOrigin),false);
                for(int j=0;j<Math.Min(Sim._ordered.Count,q.MaxTargets);j++) Sim._hit.Add(Sim._ordered[j]);
            }
        }
        private Traversal _ctx;

        private int LayerOf(int index) => _preview!=null && _units.Count>index ? _units[index].layer : 0;

        // Geometry only: no preview scene, dummy objects or project gameplay callbacks are needed.
        public void ProjectContacts(SkillDocument doc,IReadOnlyList<Vector3> targets,Vector3 groundPoint,int level,
            SkillParameterOverride[] overrides,Action<SkillPreviewContact> contact)
        {
            _points.Clear();
            foreach(var point in targets) _points.Add(point);
            _contact=contact;
            try { Evaluate(doc,null,groundPoint,level,overrides,false,null); }
            finally { _contact=null; }
        }

        internal void Sample(SkillDocument doc,Transform caster,int count,DummyLayout layout,float distance,GameObject prefab,int level,bool showEffects,SkillPreviewTools drawing)
        {
            SetupDummies(prefab,count);
            _points.Clear();
            for(int i=0;i<_units.Count;i++) {
                _units[i].SetActive(i<count);
                if(i>=count) continue;
                var point=Position(layout,i,count,distance); _points.Add(point);
                _units[i].transform.position=caster.TransformPoint(point)+(prefab==null?Vector3.up*DummyRadius:Vector3.zero);
            }
            Evaluate(doc,caster,Vector3.forward*distance,level,null,showEffects,drawing);
            TintDummies(prefab,count,doc.Playback.Time);
        }
        private void Evaluate(SkillDocument doc,Transform caster,Vector3 groundPoint,int level,SkillParameterOverride[] castOverrides,bool showEffects,SkillPreviewTools drawing)
        {
            int count=_points.Count;
            _hit.Clear(); SourceNote = null; End=doc.Root?.Animation()?.Duration??0;
            if(doc.Root==null) return;
            SetTags(_sourceTags, SourceTags, ref _lastSourceTags);
            SetTags(_targetTags, TargetTags, ref _lastTargetTags);
            var root=doc.Root; float now=doc.Playback.Time; var values=new EffectEvaluationContext(level,root.ResolveParameters(level,castOverrides));
            int target=AimIndex>0 && count>0?Mathf.Clamp(AimIndex-1,0,count-1):-1;
            _ctx ??= new Traversal();
            _ctx.Sim=this; _ctx.Root=root; _ctx.Caster=caster; _ctx.Drawing=drawing; _ctx.Now=now; _ctx.Count=count;
            _ctx.ShowEffects=showEffects;
            _ctx.Target=target; _ctx.HitUnit=-1; _ctx.Aim=target>=0?_points[target]:groundPoint;
            _ctx.HitPoint=null; _ctx.Chain=default; _ctx.Records.Clear();

            foreach(var a in root.activationActions) Visit(a,0,values,1);
            if (root.Animation() is { } animation)
                foreach (var window in animation.hitWindows)
                {
                    if (now < window.start || now >= window.end) continue;
                    var q = window.targets.Build();
                    _ctx.Candidates(q, q.ResolveShape(values), _ctx.Origin(q.EffectiveOrigin) + window.offset, true, window.yaw);
                    if (window.effect != null)
                        for (int j = 0; j < Math.Min(_ordered.Count, q.MaxTargets); j++) _hit.Add(_ordered[j]);
                }
            var markers=root.Animation()!=null?doc.Serialized(root.Animation()).FindProperty("markers"):null;
            for(int i=0;i<(markers?.arraySize??0);i++)
            {
                var m=markers.GetArrayElementAtIndex(i);float at=m.FindPropertyRelative("time").floatValue;
                var list=m.FindPropertyRelative("actions");for(int j=0;j<list.arraySize;j++) Visit(list.GetArrayElementAtIndex(j).managedReferenceValue as SkillAction,at,values,1);
            }

        }

        private void SetupDummies(GameObject prefab, int count)
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
        }

        private void TintDummies(GameObject prefab, int count, float now)
        {
            for(int i=0;i<count;i++)
            {
                if(prefab!=null)
                {
                    var bones=_bones[i];var rest=_rest[i];for(int j=1;j<bones.Length;j++){bones[j].localPosition=rest[j].p;bones[j].localRotation=rest[j].r;bones[j].localScale=rest[j].s;}
                    var pos=_units[i].transform.position;
                    if(_hit.Contains(i) && Reaction!=null) Reaction.SampleAnimation(_units[i],Mathf.Repeat(now,Mathf.Max(.001f,Reaction.length)));
                    _units[i].transform.position=pos;
                }
                Color tint=_hit.Contains(i)?new Color(1,.45f,.12f):new Color(.45f,.62f,.78f);
                _tint.SetColor("_Color",tint);_tint.SetColor("_BaseColor",tint);
                foreach(var renderer in _units[i].GetComponentsInChildren<Renderer>())renderer.SetPropertyBlock(_tint);
            }
        }

        private void Visit(SkillAction action,float at,EffectEvaluationContext v,int depth)
        {
            if(action==null || depth>8)return;
            var ctx=_ctx; float now=ctx.Now; float age=now-at;
            switch(action)
            {
                case TargetSequenceAction seq: VisitSequence(seq,at,v,depth); break;
                case NextTargetStepAction: VisitStep(false,at,v,depth); break;
                case SelectTargetsAction pick: VisitSelect(pick,at,v,depth); break;
                case LaunchProjectileAction p: VisitProjectile(p,at,v,depth,age); break;
                case CreateAreaAction a: VisitArea(a,at,v,depth,age); break;
                case ConditionalAction c: VisitConditional(c,at,v,depth); break;
                case InlineParameterScopeAction inline:
                    VisitBranch(inline.actions,at,v.WithParameters(ParameterScopeAction.ResolveParameters(v,inline.parameters,inline.bindings)),depth); break;
                case CallModuleAction c when c.module!=null:
                    VisitBranch(c.module.actions,at,v.WithParameters(ParameterScopeAction.ResolveParameters(v,c.module.parameters?.parameters,c.bindings)),depth); break;
                default: VisitCustom(action,at,v,depth,age); break;
            }
        }

        private void VisitBranch(SkillAction[] children,float when,EffectEvaluationContext vals,int depth)
        { if(children!=null)foreach(var child in children)Visit(child,when,vals,depth+1); }

        private void VisitStep(bool first,float at,EffectEvaluationContext v,int depth)
        {
            var ctx=_ctx; var seq=ctx.Chain.action;
            if(seq==null || ctx.Target<0 || ctx.Chain.used>=ctx.Chain.limit || (!seq.allowRepeat && ctx.Chain.seen.Contains(ctx.Target)))return;
            ctx.Chain.used++;ctx.Chain.seen.Add(ctx.Target);int last=ctx.Chain.index;ctx.Chain.index=first?0:last+1;
            var parameters = TargetSequenceAction.StepParameters(ctx.Chain.values.Parameters, seq.indexParameterId, seq.scaleParameterId, ctx.Chain.index, seq.retention.Resolve(ctx.Chain.values));
            var stepValues = ctx.Chain.values.WithParameters(parameters);
            foreach(var child in (first?seq.first:seq.next)??Array.Empty<SkillAction>())Visit(child,at,stepValues,ctx.Chain.depth+1);
            ctx.Chain.index=last;
        }

        private void VisitSequence(TargetSequenceAction seq,float at,EffectEvaluationContext v,int depth)
        {
            var ctx=_ctx; var outer=ctx.Chain;
            ctx.Chain=(seq,ctx.Record(seq.sharedRecord),Mathf.Clamp((int)seq.maxTargets.Resolve(v),0,64),0,0,depth,v);
            VisitStep(true,at,v,depth); ctx.Chain=outer;
        }

        private void VisitSelect(SelectTargetsAction pick,float at,EffectEvaluationContext v,int depth)
        {
            var ctx=_ctx;
            var query=pick.targets.Build();
            ctx.Candidates(query,query.ResolveShape(v),ctx.Origin(query.EffectiveOrigin),false);
            int take=Math.Min(query.MaxTargets,(int)pick.count.Resolve(v)),oldTarget=ctx.Target;var oldAim=ctx.Aim;
            var record=!string.IsNullOrEmpty(pick.sharedRecord)?ctx.Record(pick.sharedRecord):null;
            var candidates=_ordered.ToArray();
            foreach(int unit in candidates){
                if(take<=0 || ctx.Chain.action!=null && ctx.Chain.used>=ctx.Chain.limit)break;
                if(pick.excludeCurrent && unit==oldTarget || pick.excludeVisited && (record?.Contains(unit)==true ||
                    ctx.Chain.action!=null && !ctx.Chain.action.allowRepeat && ctx.Chain.seen.Contains(unit)))continue;
                take--;ctx.Target=unit;ctx.Aim=_points[unit]+Vector3.up*DummyRadius;if(pick.recordSelection)record?.Add(unit);
                VisitBranch(pick.actions,at,v,depth);
            }ctx.Target=oldTarget;ctx.Aim=oldAim;
        }

        private void VisitProjectile(LaunchProjectileAction p,float at,EffectEvaluationContext v,int depth,float age)
        {
            var ctx=_ctx; float now=ctx.Now;
            if(ctx.Origin(p.origin) is not Vector3 start || p.onlySelectedTarget && ctx.Target<0)return;
            var destination=ctx.Aim;float length=Vector3.Distance(start,destination);
            float limit=p.lifetime.Resolve(v),speed=Mathf.Max(.001f,p.speed.Resolve(v)),flight=Mathf.Min(limit,length/speed);
            Vector3 Path(float ageOfShot){
                float traveled=ageOfShot*speed;var flat=Vector3.MoveTowards(start,destination,traveled);
                flat.y+=LaunchProjectileAction.ArcOffset(p.arcHeight,traveled,length);return flat;
            }
            var hits=new List<(float time,int unit)>(); var previous=start;
            float extent=DummyRadius+Mathf.Max(0,p.radius);
            for(int step=1;step<=48;step++)
            {
                var next=Path(flight*step/48f);var span=next-previous;
                for(int i=0;i<ctx.Count;i++){
                    if(p.onlySelectedTarget && i!=ctx.Target || (p.collision.layerMask.value & (1<<LayerOf(i)))==0 || hits.Exists(h=>h.unit==i))continue;
                    var from=previous-_points[i]-Vector3.up*DummyRadius;
                    float len=span.magnitude,proj=len>0?Vector3.Dot(from,span)/len:0,c=from.sqrMagnitude-extent*extent;
                    float disc=proj*proj-c;float entry=c<=0?0:disc>=0?-proj-Mathf.Sqrt(disc):float.PositiveInfinity;
                    if(entry>=0 && entry<=len)hits.Add((at+flight*((step-1)+(len>0?entry/len:0))/48f,i));
                }previous=next;
            }
            hits.Sort((x,y)=>x.time!=y.time?x.time.CompareTo(y.time):x.unit.CompareTo(y.unit));
            int accepted=p.collision.Build().MatchesTags(_targetTags)?Math.Min(hits.Count,Math.Max(1,p.maxHits)):0;
            float stop=accepted==Math.Max(1,p.maxHits)?hits[accepted-1].time:at+flight;
            End=Mathf.Max(End,stop);
            if(ctx.Drawing!=null && ctx.ShowEffects && age>=0 && now<stop){
                for(int i=0;i<24;i+=2)ctx.Drawing.Line(ctx.Caster.TransformPoint(Path((stop-at)*i/24)),ctx.Caster.TransformPoint(Path((stop-at)*(i+1)/24)),Color.yellow);
                if(age>=0 && now<stop)ctx.Drawing.Circle(ctx.Caster.TransformPoint(Path(age)),Mathf.Max(.04f,p.radius),Color.yellow);
            }
            for(int i=0;i<accepted;i++){
                var impact=hits[i];var projectileCenter=Path(impact.time-at);var body=_points[impact.unit]+Vector3.up*DummyRadius;
                var contact=body+Vector3.ClampMagnitude(projectileCenter-body,DummyRadius);
                _contact?.Invoke(new SkillPreviewContact(p,ctx.FromUnit(p.origin),impact.unit,start,contact,at,impact.time));
                End=Mathf.Max(End,impact.time+ImpactPulse);
                if(now>=impact.time && now-impact.time<ImpactPulse)_hit.Add(impact.unit);
                int prior=ctx.Target,priorUnit=ctx.HitUnit;var previousAim=ctx.Aim;var priorHit=ctx.HitPoint;
                ctx.Target=ctx.HitUnit=impact.unit;ctx.HitPoint=contact;ctx.Aim=_points[ctx.Target];
                VisitBranch(p.onHit,impact.time,v,depth);ctx.Target=prior;ctx.Aim=previousAim;ctx.HitPoint=priorHit;ctx.HitUnit=priorUnit;
            }
        }

        private void VisitArea(CreateAreaAction a,float at,EffectEvaluationContext v,int depth,float age)
        {
            var ctx=_ctx; float now=ctx.Now;
            float life=a.duration.Resolve(v); if(life<=0)life=Mathf.Max(0,(ctx.Root.Animation()?.Duration??now)-at); End=Mathf.Max(End,at+life);
            if(age<0 || age>life)return;
            var q=a.targets.Build();
            if (ctx.Origin(q.EffectiveOrigin) is not Vector3 center) return;
            var shape=q.ResolveShape(v);
            ctx.Candidates(q,shape,center,ctx.ShowEffects);
            int admitted=Math.Min(_ordered.Count,q.MaxTargets);
            for(int j=0;j<admitted;j++) _hit.Add(_ordered[j]);
            float period=a.period.Resolve(v);
            if(period>0 && age>=period){
                if(ctx.ShowEffects && Mathf.Repeat(age,period)<ImpactPulse) ctx.Drawing?.Circle(ctx.Caster.TransformPoint(center),shape.Radius,Color.yellow);
                if(a.onPeriod is { Length: > 0 }){
                    var occupants=_ordered.GetRange(0,admitted); int prior=ctx.Target; var priorAim=ctx.Aim;
                    foreach(int unit in occupants){ctx.Target=unit;ctx.Aim=_points[unit];VisitBranch(a.onPeriod,at+Mathf.Floor(age/period)*period,v,depth);}
                    ctx.Target=prior;ctx.Aim=priorAim;
                }
            }
        }

        private void VisitConditional(ConditionalAction c,float at,EffectEvaluationContext v,int depth)
        {
            var ctx=_ctx; bool hasTarget=ctx.Target>=0;
            bool yes=(!c.requireTarget || hasTarget) && (!c.testTarget || hasTarget) &&
                (string.IsNullOrEmpty(c.booleanParameterId) || v.Parameters.Get(c.booleanParameterId).Boolean) &&
                c.BuildTagRequirements().Matches(c.testTarget?_targetTags:_sourceTags) &&
                (!c.compareAttribute || ConditionalAction.Compare(c.comparison,c.testTarget?TargetAttribute:SourceAttribute,c.value.Resolve(v)));
            VisitBranch(yes?c.whenTrue:c.whenFalse,at,v,depth);
        }

        private void VisitCustom(SkillAction action,float at,EffectEvaluationContext v,int depth,float age)
        {
            if (!SkillActionPreview.TryGet(action.GetType(), out var sample)) return;
            var ctx=_ctx;
            void Branch(SkillAction[] children, float delay, Vector3? groundPoint)
            {
                if (!(delay >= 0) || !float.IsFinite(delay)) throw new ArgumentOutOfRangeException(nameof(delay));
                End=Mathf.Max(End, at+delay+(children is { Length: > 0 } ? ImpactPulse : 0));
                var priorAim=ctx.Aim; int priorTarget=ctx.Target;
                if (groundPoint.HasValue) { ctx.Target=-1; ctx.Aim=groundPoint.Value; }
                try { VisitBranch(children,at+delay,v,depth); }
                finally { ctx.Target=priorTarget; ctx.Aim=priorAim; }
            }
            void Link(SkillSpawnPosition from, float duration)
            {
                float until = at + duration; End = Mathf.Max(End, Mathf.Max(until, at + ImpactPulse));
                if (ctx.Target < 0 || ctx.Origin(from) is not Vector3 start) return;
                _contact?.Invoke(new SkillPreviewContact(action, ctx.FromUnit(from), ctx.Target, start, ctx.Aim, at, at));
                if (ctx.Drawing != null && ctx.ShowEffects && ctx.Now >= at && ctx.Now < until)
                    ctx.Drawing.Line(ctx.Caster.TransformPoint(start), ctx.Caster.TransformPoint(ctx.Aim), Color.cyan);
            }
            sample(action,new PreviewFrame(v,age,ctx.Origin,q=>ctx.Mark(q,v),Branch,Link));
        }
        private void Clear(){foreach(var go in _units)if(go!=null)UnityEngine.Object.DestroyImmediate(go);_units.Clear();_bones.Clear();_rest.Clear();}
        public void Dispose()=>Clear();
    }
}
