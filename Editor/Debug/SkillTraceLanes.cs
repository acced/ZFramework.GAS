using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
namespace ZFramework.GAS.Editor
{
    internal sealed class SkillTraceLanes:VisualElement
    {
        private readonly List<SkillTrace> _events;
        private readonly Action<int> _select;
        private static readonly Color[] Colors={SkillColors.Ability,SkillColors.Animation,SkillColors.Duration,SkillColors.Infinite,SkillColors.Cost,SkillColors.Cue,SkillColors.Task,SkillColors.Error,SkillColors.Duration,SkillColors.Animation};
        private const float Header=68,Row=15;
        private long _cursorSerial;
        public SkillTraceLanes(List<SkillTrace> events,Action<int> select)
        {
            _events=events;_select=select;style.height=Row*Colors.Length;style.flexShrink=0;
            for(int i=0;i<Colors.Length;i++){var label=new Label(((TraceKind)i).ToString()){pickingMode=PickingMode.Ignore};label.style.position=Position.Absolute;label.style.top=i*Row;label.style.color=Colors[i];label.style.fontSize=10;Add(label);}
            generateVisualContent+=Draw;
            RegisterCallback<PointerDownEvent>(e=>{if(e.button==0){this.CapturePointer(e.pointerId);Scrub(e.localPosition);}});
            RegisterCallback<PointerMoveEvent>(e=>{if(this.HasPointerCapture(e.pointerId))Scrub(e.localPosition);});
            RegisterCallback<PointerUpEvent>(e=>{if(this.HasPointerCapture(e.pointerId))this.ReleasePointer(e.pointerId);});
        }
        private float X(int index)
        {
            int a=_events[_events.Count-1].Frame,b=_events[0].Frame;return Header+(_events[index].Frame-a)*(contentRect.width-Header-4)/Mathf.Max(1,b-a);
        }
        private void Scrub(Vector2 point)
        {
            if(_events.Count==0 || point.x<Header)return;int lane=Mathf.Clamp((int)(point.y/Row),0,Colors.Length-1);float best=float.PositiveInfinity;int nearest=-1;
            for(int i=0;i<_events.Count;i++){if((int)_events[i].Kind!=lane)continue;float d=Mathf.Abs(X(i)-point.x);if(d<best){best=d;nearest=i;}}
            if(nearest>=0){_cursorSerial=_events[nearest].Serial;tooltip=$"帧 {_events[nearest].Frame} · {_events[nearest].Label}";_select(nearest);MarkDirtyRepaint();}
        }
        private void Draw(MeshGenerationContext ctx)
        {
            if(contentRect.width<=Header || _events.Count==0)return;var p=ctx.painter2D;
            // Pixel buckets keep a full 4096-entry buffer cheap to paint.
            for(int k=0;k<Colors.Length;k++)
            {
                float last=-100;p.strokeColor=Colors[k];p.lineWidth=2;p.BeginPath();
                for(int i=0;i<_events.Count;i++)if((int)_events[i].Kind==k){float x=Mathf.Round(X(i));if(x==last)continue;last=x;float y=k*Row+2;p.MoveTo(new Vector2(x,y));p.LineTo(new Vector2(x,y+10));}
                p.Stroke();
            }
            int cursor=-1; for(int i=0;_cursorSerial!=0 && i<_events.Count;i++)if(_events[i].Serial==_cursorSerial){cursor=i;break;}
            if(cursor>=0){float x=X(cursor);p.strokeColor=Color.white;p.lineWidth=1;p.BeginPath();p.MoveTo(new Vector2(x,0));p.LineTo(new Vector2(x,contentRect.height));p.Stroke();}
        }
    }
}
