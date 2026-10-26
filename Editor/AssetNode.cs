using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public enum AssetNodeKind
    {
        Ability,
        Effect,
        Animation,
    }

    /// <summary>
    /// 一个资产对应一个节点。节点不持有任何独立数据——标题与端口都由资产的序列化内容推导。
    /// </summary>
    public sealed class AssetNode : Node
    {
        public const string CostPort = "消耗";
        public const string CooldownPort = "冷却";
        public const string AnimationPort = "动画";

        private const string MarkerPortPrefix = "标记 ";

        private readonly Dictionary<string, Port> _outputs = new();

        public ScriptableObject Asset { get; }
        public Port Input { get; }

        public AssetNode(ScriptableObject asset, AssetNodeKind kind)
        {
            Asset = asset;
            title = asset.name;

            // 节点即资产，删节点没有对应的数据语义。
            // 若放任删除，GraphView 会把该节点的所有连边一并交给变更回调，
            // 于是「从视图上移掉一个框」会静默清空这个资产的进出引用——删根节点更是直接抹掉能力的消耗与冷却。
            // 图上唯一有意义的删除是删一条连线。
            capabilities &= ~Capabilities.Deletable;

            // 端口类型即该端口收发的资产类型，图上的连线候选据此过滤（见 SkillGraphView.GetCompatiblePorts）。
            Input = InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, asset.GetType());
            Input.portName = kind == AssetNodeKind.Ability ? "能力" : "被引用";
            inputContainer.Add(Input);

            switch (kind)
            {
                case AssetNodeKind.Ability:
                    AddOutput(CostPort, typeof(GameplayEffectAsset));
                    AddOutput(CooldownPort, typeof(GameplayEffectAsset));
                    AddOutput(AnimationPort, typeof(SkillAnimationAsset));
                    break;

                case AssetNodeKind.Effect:
                    AddOutput(TriggerPort(EffectTrigger.Applied), typeof(GameplayEffectAsset));
                    AddOutput(TriggerPort(EffectTrigger.Periodic), typeof(GameplayEffectAsset));
                    AddOutput(TriggerPort(EffectTrigger.Removed), typeof(GameplayEffectAsset));
                    AddDescription(DescribeEffect(asset));
                    break;

                case AssetNodeKind.Animation:
                    AddMarkerOutputs(asset);
                    AddDescription(DescribeAnimation(asset));
                    break;
            }

            // 双击节点在 Project 面板中定位并选中该资产。
            RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2)
                {
                    Selection.activeObject = Asset;
                    EditorGUIUtility.PingObject(Asset);
                }
            });
        }

        public static string TriggerPort(EffectTrigger trigger) => trigger switch
        {
            EffectTrigger.Applied => "生效时",
            EffectTrigger.Periodic => "每周期",
            EffectTrigger.Removed => "移除时",
            _ => trigger.ToString(),
        };

        /// <summary>端口名反解为触发时机。连线时需要据此写回 <c>ConditionalEffect.trigger</c>。</summary>
        public static bool TryParseTriggerPort(string portName, out EffectTrigger trigger)
        {
            switch (portName)
            {
                case "生效时": trigger = EffectTrigger.Applied; return true;
                case "每周期": trigger = EffectTrigger.Periodic; return true;
                case "移除时": trigger = EffectTrigger.Removed; return true;
                default: trigger = default; return false;
            }
        }

        /// <summary>
        /// 标记点端口名。下标编在最前面——授权数组不按时间排序，下标是标记点在图上的稳定标识；
        /// 名字只是给人看的，可以为空也可以重复。
        /// </summary>
        public static string MarkerPort(int index, string key)
            => string.IsNullOrEmpty(key) ? $"{MarkerPortPrefix}{index}" : $"{MarkerPortPrefix}{index} · {key}";

        /// <summary>端口名反解为标记点下标。连线时需要据此写回 <c>markers[index].effect</c>。</summary>
        public static bool TryParseMarkerPort(string portName, out int index)
        {
            index = 0;
            if (portName == null || !portName.StartsWith(MarkerPortPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            int cursor = MarkerPortPrefix.Length;
            int digits = 0;
            int value = 0;
            while (cursor < portName.Length && portName[cursor] >= '0' && portName[cursor] <= '9')
            {
                value = value * 10 + (portName[cursor] - '0');
                cursor++;
                digits++;
            }

            if (digits == 0)
            {
                return false;
            }

            index = value;
            return true;
        }

        public Port GetOutput(string portName) => _outputs.TryGetValue(portName, out Port port) ? port : null;

        /// <summary>每个标记点一个输出端口，含没挂效果的——空端口正是「把效果拖到这一下」的落点。</summary>
        private void AddMarkerOutputs(ScriptableObject animation)
        {
            var so = new SerializedObject(animation);
            SerializedProperty markers = so.FindProperty("markers");
            if (markers == null || !markers.isArray)
            {
                return;
            }

            for (int i = 0; i < markers.arraySize; i++)
            {
                string key = markers.GetArrayElementAtIndex(i).FindPropertyRelative("key")?.stringValue;
                AddOutput(MarkerPort(i, key), typeof(GameplayEffectAsset));
            }
        }

        private void AddDescription(string text)
        {
            extensionContainer.Add(new Label(text) { style = { paddingLeft = 6f, paddingRight = 6f } });
            RefreshExpandedState();
        }

        private void AddOutput(string portName, Type accepts)
        {
            Port port = InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, accepts);
            port.portName = portName;
            EnableAssetDrop(port, portName, accepts);
            outputContainer.Add(port);
            _outputs[portName] = port;
        }

        /// <summary>
        /// 让端口能直接接收从 Project 面板拖来的资产——省去「先建资产、再回 Inspector 引用」的往返。
        /// 校验与连线共用 <see cref="SkillGraphView.TryAttach"/>，两条路径不会出现规则不一致。
        /// </summary>
        private void EnableAssetDrop(Port port, string portName, Type accepts)
        {
            port.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (SkillGraphView.DraggedAsset(accepts) != null)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                    evt.StopPropagation();
                }
            });

            port.RegisterCallback<DragPerformEvent>(evt =>
            {
                if (SkillGraphView.DraggedAsset(accepts) == null)
                {
                    return;
                }

                DragAndDrop.AcceptDrag();

                // 逐个处理拖来的资产，而不是只取第一个——Project 面板里多选拖拽很常见，
                // 静默丢弃用户明确选中的其余资产比挂错更让人困惑。
                // 消耗 / 冷却 / 动画这类单值端口会被后者覆盖前者，结果确定。
                bool any = false;
                UnityEngine.Object[] refs = DragAndDrop.objectReferences;
                for (int i = 0; i < refs.Length; i++)
                {
                    if (refs[i] is ScriptableObject asset && accepts.IsInstanceOfType(asset)
                        && SkillGraphView.AttachWithFeedback(Asset, portName, asset))
                    {
                        any = true;
                    }
                }

                if (any)
                {
                    AssetDropped?.Invoke();
                }

                evt.StopPropagation();
            });
        }

        /// <summary>拖放成功后由图重建，保持「视图是数据的投影」。</summary>
        public event Action AssetDropped;

        /// <summary>节点上直接显示时长策略与修改器数量，免去为看一眼而反复切换 Inspector。</summary>
        private static string DescribeEffect(ScriptableObject asset)
        {
            var so = new SerializedObject(asset);
            int policy = so.FindProperty("durationPolicy")?.enumValueIndex ?? 0;
            float duration = so.FindProperty("duration")?.floatValue ?? 0f;
            float period = so.FindProperty("period")?.floatValue ?? 0f;
            int modifiers = so.FindProperty("modifiers")?.arraySize ?? 0;

            string policyText = policy switch
            {
                0 => "瞬时",
                1 => $"持续 {duration:0.##}s",
                2 => "永久",
                _ => "未知",
            };
            string periodText = period > 0f ? $"　周期 {period:0.##}s" : string.Empty;
            return $"{policyText}{periodText}\n修改器 {modifiers} 个";
        }

        /// <summary>动画节点上直接显示片段、时长与时间轴规模；具体时间点在下方时间轴面板里编辑。</summary>
        private static string DescribeAnimation(ScriptableObject asset)
        {
            var so = new SerializedObject(asset);
            string clip = so.FindProperty("clipName")?.stringValue;
            float duration = so.FindProperty("duration")?.floatValue ?? 0f;
            bool loop = so.FindProperty("loop")?.boolValue ?? false;
            int markers = so.FindProperty("markers")?.arraySize ?? 0;
            int regions = so.FindProperty("cueRegions")?.arraySize ?? 0;

            string clipText = string.IsNullOrEmpty(clip) ? "未填片段名" : clip;
            string loopText = loop ? "　循环" : string.Empty;
            return $"{clipText}　{duration:0.##}s{loopText}\n标记 {markers} 个　区间 {regions} 个";
        }
    }
}
