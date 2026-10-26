using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    /// <summary>
    /// 技能动画时间轴，挂在技能结构图窗口底部。
    ///
    /// 它只管一件事：<b>时间</b>。标记点的名字与效果、Cue 标签、作用目标都是普通字段，
    /// Inspector 的列表编辑已经够用；唯有「这一下落在动画的哪个位置」必须看着刻度拖。
    ///
    /// <b>拖动期间不写序列化数据</b>，只改本类的临时字段。因此不会触发写回 → 整图重建 → 把正在拖的东西换掉；
    /// 抬手时一次性 <see cref="SerializedObject.ApplyModifiedProperties"/>，一次拖拽正好一步撤销。
    ///
    /// 一个能力只有一段动画，所以面板不跟踪图上的选中项——窗口重建时把根能力的动画交给它即可。
    /// </summary>
    internal sealed class AnimationTimelinePanel : VisualElement
    {
        private const float Pad = 14f;
        private const float HeaderHeight = 18f;
        private const float RulerHeight = 16f;
        private const float MarkerLaneHeight = 30f;
        private const float RegionLaneHeight = 18f;
        private const float GripWidth = 6f;
        private const float MarkerHitRadius = 7f;
        private const float MaxHeight = 220f;

        /// <summary>刻度间隔的候选值，取第一个宽到能放下标签的。</summary>
        private static readonly float[] TickSteps = { 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f };

        private static readonly Color TrackColor = new(0f, 0f, 0f, 0.20f);
        private static readonly Color TickColor = new(1f, 1f, 1f, 0.18f);
        private static readonly Color EffectMarkerColor = new(0.96f, 0.76f, 0.26f);
        private static readonly Color PlainMarkerColor = new(0.56f, 0.76f, 0.96f);
        private static readonly Color RegionColor = new(0.32f, 0.72f, 0.54f, 0.40f);
        private static readonly Color RegionGripColor = new(0.32f, 0.72f, 0.54f, 0.95f);

        /// <summary>落在时长之外的时间点永远不会触发，运行期构造定义时会直接抛异常，这里先标红。</summary>
        private static readonly Color OverflowColor = new(0.95f, 0.35f, 0.30f);

        private readonly IMGUIContainer _imgui;

        private SkillAnimationAsset _animation;
        private SerializedObject _serialized;
        private float _duration;
        private float _frameRate;
        private float _trackWidth;

        private DragKind _drag;
        private int _dragIndex;
        private float _dragStart;
        private float _dragEnd;

        /// <summary>抓取点与元素时间的差值，拖动时元素不会跳到鼠标下。</summary>
        private float _grabOffset;

        private enum DragKind
        {
            None,
            Marker,
            RegionStart,
            RegionEnd,
            RegionMove,
        }

        public AnimationTimelinePanel()
        {
            style.borderTopWidth = 1f;
            style.borderTopColor = new Color(0f, 0f, 0f, 0.4f);
            style.display = DisplayStyle.None;

            // 不让弹性布局压扁自己：空间不够时该缩的是上面的结构图，刻度被压扁就没法看了。
            style.flexShrink = 0f;

            _imgui = new IMGUIContainer(OnGUI);

            // 区间多到超过面板高度时用滚动而不是压扁：压扁会让后面的区间无声消失。
            var scroll = new ScrollView(ScrollViewMode.Vertical) { style = { flexGrow = 1f } };
            scroll.Add(_imgui);
            Add(scroll);
        }

        /// <summary>增删标记点或区间后通知窗口重建——动画节点的端口数量随之变化。</summary>
        public event Action StructureChanged;

        /// <summary>切换到某段动画；传 null 则整条隐藏，把高度让给结构图。</summary>
        public void Show(SkillAnimationAsset animation)
        {
            _animation = animation;
            _serialized = animation != null ? new SerializedObject(animation) : null;
            _drag = DragKind.None;
            style.display = animation != null ? DisplayStyle.Flex : DisplayStyle.None;
            ResizeToContent();
        }

        private void ResizeToContent()
        {
            if (_serialized == null || _serialized.targetObject == null)
            {
                return;
            }

            _serialized.Update();
            int regions = _serialized.FindProperty("cueRegions")?.arraySize ?? 0;
            float content = HeaderHeight + RulerHeight + MarkerLaneHeight + regions * RegionLaneHeight + 6f;
            _imgui.style.height = content;
            style.height = Mathf.Min(content + 4f, MaxHeight);
        }

        private void OnGUI()
        {
            if (_serialized == null || _serialized.targetObject == null)
            {
                return;
            }

            _serialized.Update();
            _duration = _serialized.FindProperty("duration")?.floatValue ?? 0f;
            _frameRate = _serialized.FindProperty("frameRate")?.floatValue ?? 0f;

            Rect area = _imgui.contentRect;
            _trackWidth = area.width - 2f * Pad;
            DrawHeader(new Rect(Pad, 0f, Mathf.Max(_trackWidth, 0f), HeaderHeight));

            if (!(_duration > 0f) || _trackWidth < 1f)
            {
                // 时长非正就无从换算像素。说清楚，而不是画一条除零出来的轴。
                GUI.Label(new Rect(Pad, HeaderHeight, area.width, RulerHeight),
                    "时长必须大于 0，先在 Inspector 里填好。", EditorStyles.miniLabel);
                return;
            }

            SerializedProperty markers = _serialized.FindProperty("markers");
            SerializedProperty regions = _serialized.FindProperty("cueRegions");
            float rulerY = HeaderHeight;
            float markerY = rulerY + RulerHeight;
            float regionY = markerY + MarkerLaneHeight;

            HandleMouse(markers, regions, markerY, regionY);
            DrawRuler(rulerY);
            DrawMarkers(markers, markerY);
            DrawRegions(regions, regionY);
        }

        // ================= 绘制 =================

        private void DrawHeader(Rect rect)
        {
            string clip = _serialized.FindProperty("clipName")?.stringValue;
            bool loop = _serialized.FindProperty("loop")?.boolValue ?? false;
            string clipText = string.IsNullOrEmpty(clip) ? "未填片段名" : clip;
            string loopText = loop ? "　循环" : string.Empty;
            GUI.Label(rect, $"{_animation.name}　{clipText}　{_duration:0.##}s{loopText}", EditorStyles.miniBoldLabel);

            var hint = new GUIContent("拖动改时间　右键增删　按住 Alt 关闭吸帧");
            float width = EditorStyles.miniLabel.CalcSize(hint).x;
            GUI.Label(new Rect(rect.xMax - width, rect.y, width, rect.height), hint, EditorStyles.miniLabel);
        }

        private void DrawRuler(float y)
        {
            float step = ChooseTickStep();
            int count = Mathf.FloorToInt(_duration / step);
            for (int i = 0; i <= count; i++)
            {
                float x = TimeToX(i * step);
                EditorGUI.DrawRect(new Rect(x, y, 1f, RulerHeight), TickColor);
                // 标签宽度小于刻度间距的下限，任何两个标签的矩形都不会叠在一起。
                GUI.Label(new Rect(x + 2f, y - 1f, 42f, RulerHeight), $"{i * step:0.##}s", EditorStyles.miniLabel);
            }

            EditorGUI.DrawRect(new Rect(TimeToX(_duration), y, 1f, RulerHeight), TickColor);
        }

        private float ChooseTickStep()
        {
            for (int i = 0; i < TickSteps.Length; i++)
            {
                if (TickSteps[i] / _duration * _trackWidth >= 44f)
                {
                    return TickSteps[i];
                }
            }

            // 长到连 10s 一格都挤不下：只标首尾。
            return _duration;
        }

        private void DrawMarkers(SerializedProperty markers, float y)
        {
            EditorGUI.DrawRect(new Rect(Pad, y, _trackWidth, MarkerLaneHeight), TrackColor);

            for (int i = 0; i < markers.arraySize; i++)
            {
                SerializedProperty element = markers.GetArrayElementAtIndex(i);
                float time = MarkerTime(markers, i);
                bool overflow = time > _duration;
                Color color = overflow ? OverflowColor
                    : element.FindPropertyRelative("effect").objectReferenceValue != null
                        ? EffectMarkerColor
                        : PlainMarkerColor;

                float x = TimeToX(Mathf.Min(time, _duration));
                EditorGUI.DrawRect(new Rect(x - 1f, y, 2f, MarkerLaneHeight), color);
                EditorGUI.DrawRect(new Rect(x - 5f, y, 10f, 10f), color);

                string key = element.FindPropertyRelative("key").stringValue;
                string name = string.IsNullOrEmpty(key) ? $"#{i}" : key;
                string suffix = overflow ? "超出时长"
                    : _drag == DragKind.Marker && _dragIndex == i ? TimeLabel(time)
                    : $"{time:0.##}s";
                GUI.Label(new Rect(x + 7f, y + MarkerLaneHeight - 15f, 160f, 14f), $"{name}　{suffix}", EditorStyles.miniLabel);
            }
        }

        private void DrawRegions(SerializedProperty regions, float y)
        {
            for (int i = 0; i < regions.arraySize; i++)
            {
                Rect bar = RegionRect(regions, i, y);
                EditorGUI.DrawRect(new Rect(Pad, bar.y, _trackWidth, bar.height), TrackColor);

                float start = RegionStart(regions, i);
                float end = RegionEnd(regions, i);
                bool overflow = start > _duration || end > _duration;

                EditorGUI.DrawRect(bar, overflow ? OverflowColor * new Color(1f, 1f, 1f, 0.4f) : RegionColor);
                EditorGUI.DrawRect(new Rect(bar.xMin, bar.y, GripWidth, bar.height), RegionGripColor);
                if (end >= 0f)
                {
                    EditorGUI.DrawRect(new Rect(bar.xMax - GripWidth, bar.y, GripWidth, bar.height), RegionGripColor);
                }

                string tag = regions.GetArrayElementAtIndex(i).FindPropertyRelative("cueTag").FindPropertyRelative("m_Name").stringValue;
                string tagText = string.IsNullOrEmpty(tag) ? "未填 Cue 标签" : tag;
                string endText = end < 0f ? "随动画结束" : $"{end:0.##}s";
                GUI.Label(new Rect(bar.xMin + GripWidth + 3f, bar.y, _trackWidth, bar.height),
                    $"{tagText}　{start:0.##}s → {endText}", EditorStyles.miniLabel);
            }
        }

        // ================= 交互 =================

        private void HandleMouse(SerializedProperty markers, SerializedProperty regions, float markerY, float regionY)
        {
            Event e = Event.current;

            // 每一遍事件都取一次，控件 id 才稳定；hotControl 让拖动划出面板也不断。
            int control = GUIUtility.GetControlID(FocusType.Passive);

            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && TryBeginDrag(markers, regions, markerY, regionY, e.mousePosition))
                    {
                        GUIUtility.hotControl = control;
                        e.Use();
                    }

                    break;

                case EventType.MouseDrag when GUIUtility.hotControl == control:
                    UpdateDrag(e.mousePosition);
                    _imgui.MarkDirtyRepaint();
                    e.Use();
                    break;

                case EventType.MouseUp when GUIUtility.hotControl == control:
                    CommitDrag(markers, regions);
                    GUIUtility.hotControl = 0;
                    e.Use();
                    break;

                case EventType.ContextClick:
                    ShowContextMenu(markers, regions, markerY, regionY, e.mousePosition);
                    e.Use();
                    break;
            }
        }

        private bool TryBeginDrag(SerializedProperty markers, SerializedProperty regions, float markerY, float regionY, Vector2 mouse)
        {
            // 上一次拖动若被打断（热控件被抢走、抬手事件丢失），残留的 _drag 会让命中判定读到别人的临时值。
            _drag = DragKind.None;

            // 标记点画在区间之上，抓取顺序也先它。
            int marker = HitMarker(markers, markerY, mouse);
            if (marker >= 0)
            {
                _drag = DragKind.Marker;
                _dragIndex = marker;
                _dragStart = markers.GetArrayElementAtIndex(marker).FindPropertyRelative("time").floatValue;
                _grabOffset = _dragStart - XToTime(mouse.x);
                return true;
            }

            int region = HitRegion(regions, regionY, mouse);
            if (region < 0)
            {
                return false;
            }

            SerializedProperty element = regions.GetArrayElementAtIndex(region);
            _dragIndex = region;
            _dragStart = element.FindPropertyRelative("start").floatValue;
            _dragEnd = element.FindPropertyRelative("end").floatValue;

            Rect bar = RegionRect(regions, region, regionY);
            if (mouse.x <= bar.xMin + GripWidth)
            {
                _drag = DragKind.RegionStart;
            }
            else if (_dragEnd >= 0f && mouse.x >= bar.xMax - GripWidth)
            {
                _drag = DragKind.RegionEnd;
            }
            else
            {
                // 终点为负的区间没有右手柄——它的右端由动画时长决定，不是一个可拖的时间。
                _drag = DragKind.RegionMove;
            }

            _grabOffset = (_drag == DragKind.RegionEnd ? _dragEnd : _dragStart) - XToTime(mouse.x);
            return true;
        }

        private void UpdateDrag(Vector2 mouse)
        {
            float time = Mathf.Clamp(Snap(XToTime(mouse.x) + _grabOffset), 0f, _duration);

            switch (_drag)
            {
                case DragKind.Marker:
                    _dragStart = time;
                    break;

                case DragKind.RegionStart:
                    _dragStart = _dragEnd >= 0f ? Mathf.Min(time, _dragEnd) : time;
                    break;

                case DragKind.RegionEnd:
                    _dragEnd = Mathf.Max(time, _dragStart);
                    break;

                case DragKind.RegionMove:
                    // 整段平移保持长度，撞到轴的两端就停住。
                    float length = _dragEnd >= 0f ? _dragEnd - _dragStart : 0f;
                    _dragStart = Mathf.Clamp(time, 0f, _duration - length);
                    if (_dragEnd >= 0f)
                    {
                        _dragEnd = _dragStart + length;
                    }

                    break;
            }
        }

        private void CommitDrag(SerializedProperty markers, SerializedProperty regions)
        {
            switch (_drag)
            {
                case DragKind.Marker:
                    markers.GetArrayElementAtIndex(_dragIndex).FindPropertyRelative("time").floatValue = _dragStart;
                    break;

                case DragKind.RegionStart:
                case DragKind.RegionEnd:
                case DragKind.RegionMove:
                    SerializedProperty element = regions.GetArrayElementAtIndex(_dragIndex);
                    element.FindPropertyRelative("start").floatValue = _dragStart;
                    element.FindPropertyRelative("end").floatValue = _dragEnd;
                    break;

                default:
                    // 只是点了一下没拖动，也走这里；没有改过任何值就什么都不写。
                    return;
            }

            _drag = DragKind.None;
            _serialized.ApplyModifiedProperties();
            _imgui.MarkDirtyRepaint();
        }

        private void ShowContextMenu(SerializedProperty markers, SerializedProperty regions, float markerY, float regionY, Vector2 mouse)
        {
            var menu = new GenericMenu();
            float time = Mathf.Clamp(Snap(XToTime(mouse.x)), 0f, _duration);

            int marker = HitMarker(markers, markerY, mouse);
            int region = marker >= 0 ? -1 : HitRegion(regions, regionY, mouse);

            if (marker >= 0)
            {
                menu.AddItem(new GUIContent("删除标记点"), false, () => DeleteElement("markers", marker));
            }
            else if (region >= 0)
            {
                menu.AddItem(new GUIContent("删除区间"), false, () => DeleteElement("cueRegions", region));
                menu.AddItem(new GUIContent("终点改为随动画结束"), false, () => SetRegionEnd(region, -1f));
            }
            else
            {
                menu.AddItem(new GUIContent($"在 {TimeLabel(time)} 加标记点"), false, () => AddMarker(time));
                menu.AddItem(new GUIContent($"在 {TimeLabel(time)} 加 Cue 区间"), false, () => AddRegion(time));
            }

            menu.ShowAsContext();
        }

        // ================= 写回 =================
        // 菜单项在 OnGUI 返回之后才执行，因此一律按名字重新取属性，不捕获这一帧的 SerializedProperty。

        private void AddMarker(float time)
        {
            SerializedProperty list = _serialized.FindProperty("markers");
            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);

            // InsertArrayElementAtIndex 会复制前一个元素，逐个字段写清楚，
            // 免得新标记点悄悄继承上一个的效果。
            SerializedProperty element = list.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("time").floatValue = time;
            element.FindPropertyRelative("key").stringValue = string.Empty;
            element.FindPropertyRelative("effect").objectReferenceValue = null;
            element.FindPropertyRelative("target").enumValueIndex = 0;

            _serialized.ApplyModifiedProperties();
            Refresh();
        }

        private void AddRegion(float start)
        {
            SerializedProperty list = _serialized.FindProperty("cueRegions");
            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);

            SerializedProperty element = list.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("start").floatValue = start;
            element.FindPropertyRelative("end").floatValue = -1f;
            element.FindPropertyRelative("cueTag").FindPropertyRelative("m_Name").stringValue = string.Empty;

            _serialized.ApplyModifiedProperties();
            Refresh();
        }

        private void DeleteElement(string listName, int index)
        {
            SerializedProperty list = _serialized.FindProperty(listName);
            if (list == null || index < 0 || index >= list.arraySize)
            {
                return;
            }

            list.DeleteArrayElementAtIndex(index);
            _serialized.ApplyModifiedProperties();
            Refresh();
        }

        private void SetRegionEnd(int index, float end)
        {
            SerializedProperty list = _serialized.FindProperty("cueRegions");
            if (list == null || index < 0 || index >= list.arraySize)
            {
                return;
            }

            list.GetArrayElementAtIndex(index).FindPropertyRelative("end").floatValue = end;
            _serialized.ApplyModifiedProperties();
            _imgui.MarkDirtyRepaint();
        }

        private void Refresh()
        {
            ResizeToContent();
            _imgui.MarkDirtyRepaint();
            StructureChanged?.Invoke();
        }

        // ================= 几何 =================

        private float TimeToX(float time) => Pad + time / _duration * _trackWidth;

        private float XToTime(float x) => (x - Pad) / _trackWidth * _duration;

        /// <summary>按帧吸附——帧率就是为这件事存在的；按住 Alt 走自由值。</summary>
        private float Snap(float time)
            => _frameRate > 0f && !Event.current.alt ? Mathf.Round(time * _frameRate) / _frameRate : time;

        private string TimeLabel(float time)
            => _frameRate > 0f ? $"{time:0.###}s（帧 {Mathf.RoundToInt(time * _frameRate)}）" : $"{time:0.###}s";

        private float MarkerTime(SerializedProperty markers, int index)
            => _drag == DragKind.Marker && _dragIndex == index
                ? _dragStart
                : markers.GetArrayElementAtIndex(index).FindPropertyRelative("time").floatValue;

        private bool IsDraggingRegion(int index)
            => _dragIndex == index
                && (_drag == DragKind.RegionStart || _drag == DragKind.RegionEnd || _drag == DragKind.RegionMove);

        private float RegionStart(SerializedProperty regions, int index)
            => IsDraggingRegion(index)
                ? _dragStart
                : regions.GetArrayElementAtIndex(index).FindPropertyRelative("start").floatValue;

        private float RegionEnd(SerializedProperty regions, int index)
            => IsDraggingRegion(index)
                ? _dragEnd
                : regions.GetArrayElementAtIndex(index).FindPropertyRelative("end").floatValue;

        private Rect RegionRect(SerializedProperty regions, int index, float regionY)
        {
            float start = Mathf.Min(RegionStart(regions, index), _duration);
            float end = RegionEnd(regions, index);
            float x0 = TimeToX(start);
            float x1 = TimeToX(end < 0f ? _duration : Mathf.Min(end, _duration));

            // 太窄就抓不住两个手柄，给一个最小宽度。
            float width = Mathf.Max(x1 - x0, GripWidth * 2f + 2f);
            return new Rect(x0, regionY + index * RegionLaneHeight, width, RegionLaneHeight - 2f);
        }

        private int HitMarker(SerializedProperty markers, float markerY, Vector2 mouse)
        {
            if (mouse.y < markerY || mouse.y > markerY + MarkerLaneHeight)
            {
                return -1;
            }

            // 从后往前：后画的在上面，抓取顺序与视觉一致。
            for (int i = markers.arraySize - 1; i >= 0; i--)
            {
                float x = TimeToX(Mathf.Min(MarkerTime(markers, i), _duration));
                if (Mathf.Abs(mouse.x - x) <= MarkerHitRadius)
                {
                    return i;
                }
            }

            return -1;
        }

        private int HitRegion(SerializedProperty regions, float regionY, Vector2 mouse)
        {
            for (int i = 0; i < regions.arraySize; i++)
            {
                if (RegionRect(regions, i, regionY).Contains(mouse))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
