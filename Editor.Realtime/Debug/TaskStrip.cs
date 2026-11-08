using System;
using ZFramework.GAS.Unity;
using UnityEngine;
using UnityEngine.UIElements;

namespace ZFramework.GAS.Editor
{
    public sealed class AnimationTaskSnapshot : RuntimeSnapshot.PresentationTask
    {
        public readonly SkillAnimationDefinition Definition;
        public readonly string SectionId, QueuedSectionId;
        public readonly ClipWeight[] Weights;
        internal AnimationTaskSnapshot(AnimationTask task)
        {
            Definition = task.Definition; SectionId = task.SectionId; QueuedSectionId = task.QueuedSectionId;
            var weights = new ClipWeight[8]; int count = task.CopyWeights(weights); Array.Resize(ref weights, count); Weights = weights;
            AbilityAsset = DefinitionSource.Of(task.Ability.Definition) as GameplayAbilityAsset;
            Asset = DefinitionSource.Of(Definition) as SkillAnimationAsset;
            AbilityName = AbilityAsset != null ? AbilityAsset.name : Definition.ClipName;
            ClipName = Definition.ClipName; Loop = Definition.Loop; AuthoredDuration = Definition.Duration;
            Duration = task.Duration; NormalizedTime = task.NormalizedTime;
        }
        public override void BindStrip(VisualElement host)
        {
            if (host.childCount == 0) host.Add(new TaskStrip());
            ((TaskStrip)host[0]).SetTask(this);
        }
    }

    /// <summary>运行中定义的只读时间轴，不读取可能已修改的授权资产。</summary>
    public sealed class TaskStrip : VisualElement
    {
        private AnimationTaskSnapshot _task;
        public TaskStrip()
        {
            AddToClassList("debug-task-strip");
            generateVisualContent += Draw;
            tooltip = "从上到下：动画 / 混入、效果标记点、Cue 区间、标签窗口、任务事件。竖线为授权时间播放头。";
        }
        public void SetTask(AnimationTaskSnapshot task) { _task = task; tooltip=task==null?null:string.Join(" · ",System.Array.ConvertAll(task.Weights,w=>w.Name+" "+w.Weight.ToString("P0"))); MarkDirtyRepaint(); }
        private void Draw(MeshGenerationContext context)
        {
            if (_task == null || contentRect.width <= 16) return;
            var definition = _task.Definition;
            float width = contentRect.width - 12, duration = definition.Duration;
            float X(float time) => 6 + TimelineMath.X(Mathf.Clamp(time, 0, duration), width / duration, 0);
            var p = context.painter2D;
            void Rect(float x, float y, float w, float h, Color color)
            {
                p.fillColor = color; p.BeginPath(); p.MoveTo(new Vector2(x, y)); p.LineTo(new Vector2(x + w, y));
                p.LineTo(new Vector2(x + w, y + h)); p.LineTo(new Vector2(x, y + h)); p.ClosePath(); p.Fill();
            }
            void Diamond(float time, float y, Color color)
            {
                float x = X(time); p.fillColor = color; p.BeginPath(); p.MoveTo(new Vector2(x, y - 4));
                p.LineTo(new Vector2(x + 4, y)); p.LineTo(new Vector2(x, y + 4)); p.LineTo(new Vector2(x - 4, y)); p.ClosePath(); p.Fill();
            }
            Rect(6, 5, width, 14, new Color(.3f, .22f, .3f));
            Rect(6, 5, X(_task.AuthoredTime) - 6, 14, SkillColors.Animation);
            if (definition.BlendIn > 0)
            {
                p.fillColor = new Color(0, 0, 0, .45f); p.BeginPath(); p.MoveTo(new Vector2(6, 5));
                p.LineTo(new Vector2(X(definition.BlendIn), 5)); p.LineTo(new Vector2(6, 19)); p.ClosePath(); p.Fill();
            }
            foreach(var s in definition.Sections)
                Rect(X(s.Start),19,Mathf.Max(1,X(s.End)-X(s.Start)-1),3,s.Id==_task.SectionId?Color.green:s.Id==_task.QueuedSectionId?Color.yellow:Color.gray);
            foreach (var marker in definition.Markers) Diamond(marker.Time, 28, SkillColors.Ability);
            foreach (var region in definition.CueRegions)
                Rect(X(region.Start), 37, Mathf.Max(1, X(region.End < 0 ? duration : region.End) - X(region.Start)), 7, SkillColors.Cue);
            foreach (var window in definition.TagWindows)
                Rect(X(window.Start), 49, Mathf.Max(1, X(window.End < 0 ? duration : window.End) - X(window.Start)), 7, SkillColors.Cost);
            p.strokeColor = SkillColors.Cooldown; p.lineWidth = 2; p.BeginPath();
            p.MoveTo(new Vector2(X(_task.AuthoredTime), 1)); p.LineTo(new Vector2(X(_task.AuthoredTime), 74)); p.Stroke();
        }
    }
}
