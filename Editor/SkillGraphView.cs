using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    /// <summary>
    /// 技能结构图。**不引入新的数据格式**——节点与连线直接反映
    /// <see cref="GameplayAbilityAsset"/> / <see cref="GameplayEffectAsset"/> 之间已有的引用
    /// （消耗、冷却、子效果）。因此不存在图与运行期数据两份来源，也不需要编译步骤。
    ///
    /// 图上的编辑一律写回资产的序列化字段，写完立即按数据重建整张图——
    /// 视图永远是数据的投影，不持有独立状态。
    /// </summary>
    public sealed class SkillGraphView : GraphView
    {
        private const float ColumnSpacing = 320f;
        private const float RowSpacing = 190f;

        private readonly Dictionary<ScriptableObject, AssetNode> _nodes = new();
        private GameplayAbilityAsset _root;
        private bool _rebuilding;

        /// <summary>当前图中的节点数，供测试与状态栏使用。</summary>
        public int NodeCount => _nodes.Count;

        public SkillGraphView()
        {
            style.flexGrow = 1f;
            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            Insert(0, new GridBackground { name = "grid" });
            graphViewChanged = OnGraphViewChanged;

            // 把能力资产拖到空白处 → 切换根节点。
            RegisterCallback<DragUpdatedEvent>(evt =>
            {
                if (DraggedAsset(typeof(GameplayAbilityAsset)) != null)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Link;
                    evt.StopPropagation();
                }
            });
            RegisterCallback<DragPerformEvent>(evt =>
            {
                if (DraggedAsset(typeof(GameplayAbilityAsset)) is not GameplayAbilityAsset ability)
                {
                    return;
                }

                DragAndDrop.AcceptDrag();
                RebuildRequested?.Invoke(ability);
                evt.StopPropagation();
            });
        }

        /// <summary>
        /// 请求以给定能力为根重建整个窗口——拖入新的根能力时，以及图上的编辑写回之后。
        /// 两种情形要做的事完全一样，因此是同一个信号。
        /// </summary>
        public event Action<GameplayAbilityAsset> RebuildRequested;

        /// <summary>取当前拖拽中的第一个指定类型资产；没有则返回 null。</summary>
        internal static ScriptableObject DraggedAsset(Type type)
        {
            UnityEngine.Object[] refs = DragAndDrop.objectReferences;
            for (int i = 0; i < refs.Length; i++)
            {
                if (refs[i] is ScriptableObject asset && type.IsInstanceOfType(asset))
                {
                    return asset;
                }
            }

            return null;
        }

        /// <summary>以一个能力资产为根重建整张图。</summary>
        public void Rebuild(GameplayAbilityAsset root)
        {
            _root = root;
            _rebuilding = true;
            try
            {
                DeleteElements(graphElements);
                _nodes.Clear();
                if (root == null)
                {
                    return;
                }

                var abilityNode = new AssetNode(root, AssetNodeKind.Ability);
                AddNode(root, abilityNode, new Vector2(40f, 40f));

                int row = 0;
                LinkEffect(abilityNode, AssetNode.CostPort, ReadObjectField(root, "cost") as GameplayEffectAsset, 1, ref row);
                LinkEffect(abilityNode, AssetNode.CooldownPort, ReadObjectField(root, "cooldown") as GameplayEffectAsset, 1, ref row);
                LinkAnimation(abilityNode, ReadObjectField(root, "animation") as SkillAnimationAsset, ref row);
            }
            finally
            {
                _rebuilding = false;
            }
        }

        /// <summary>展开动画节点，并把挂在标记点上的效果接着展开成子树。</summary>
        private void LinkAnimation(AssetNode abilityNode, SkillAnimationAsset animation, ref int row)
        {
            if (animation == null)
            {
                return;
            }

            var node = new AssetNode(animation, AssetNodeKind.Animation);
            AddNode(animation, node, new Vector2(40f + ColumnSpacing, 40f + row * RowSpacing));
            row++;
            Connect(abilityNode, AssetNode.AnimationPort, node);

            foreach ((GameplayEffectAsset effect, string port) in ReadMarkerEffects(animation))
            {
                LinkEffect(node, port, effect, 2, ref row);
            }
        }

        /// <summary>
        /// 递归展开效果及其子效果。<paramref name="row"/> 按引用传递，
        /// 保证同一列内的节点不会重叠。
        /// </summary>
        private void LinkEffect(AssetNode parent, string parentPort, GameplayEffectAsset effect, int column, ref int row)
        {
            if (effect == null)
            {
                return;
            }

            // 同一效果被多处引用时复用同一个节点，连线自然汇聚，不重复展开（也就不会因环而死循环）。
            if (_nodes.TryGetValue(effect, out AssetNode existing))
            {
                Connect(parent, parentPort, existing);
                return;
            }

            var node = new AssetNode(effect, AssetNodeKind.Effect);
            AddNode(effect, node, new Vector2(40f + column * ColumnSpacing, 40f + row * RowSpacing));
            row++;
            Connect(parent, parentPort, node);

            foreach ((GameplayEffectAsset child, string port) in ReadConditionalEffects(effect))
            {
                LinkEffect(node, port, child, column + 1, ref row);
            }
        }

        private void AddNode(ScriptableObject asset, AssetNode node, Vector2 position)
        {
            node.SetPosition(new Rect(position, new Vector2(240f, 120f)));
            node.AssetDropped += ScheduleRebuild;
            AddElement(node);
            _nodes[asset] = node;
        }

        /// <summary>
        /// 延迟到本次事件处理结束后重建：在 GraphView 处理变更或拖放的回调里改动元素集合会出问题。
        /// 期间窗口可能已被关闭（放下资产后随手关窗），此时视图已脱离面板，重建没有意义。
        /// 走窗口而非直接重建自己：底部的时间轴面板也要跟着换成新的动画。
        /// </summary>
        private void ScheduleRebuild()
        {
            GameplayAbilityAsset root = _root;
            EditorApplication.delayCall += () =>
            {
                if (panel != null)
                {
                    RebuildRequested?.Invoke(root);
                }
            };
        }

        private void Connect(AssetNode from, string fromPort, AssetNode to)
        {
            Port output = from.GetOutput(fromPort);
            if (output == null)
            {
                return;
            }

            Edge edge = output.ConnectTo(to.Input);
            AddElement(edge);
        }

        // ================= 编辑 =================

        private GraphViewChange OnGraphViewChanged(GraphViewChange change)
        {
            if (_rebuilding || _root == null)
            {
                return change;
            }

            bool dataChanged = false;

            if (change.elementsToRemove != null)
            {
                foreach (GraphElement element in change.elementsToRemove)
                {
                    if (element is Edge removed && TryResolve(removed, out AssetNode from, out string port, out AssetNode to))
                    {
                        Disconnect(from, port, to);
                        dataChanged = true;
                    }
                }
            }

            if (change.edgesToCreate != null)
            {
                foreach (Edge created in change.edgesToCreate)
                {
                    if (TryResolve(created, out AssetNode from, out string port, out AssetNode to)
                        && AttachWithFeedback(from.Asset, port, to.Asset))
                    {
                        dataChanged = true;
                    }
                }

                // 连线的结果由数据决定：清空本次待建边，稍后整图重建。
                change.edgesToCreate.Clear();
            }

            if (dataChanged)
            {
                ScheduleRebuild();
            }

            return change;
        }

        private bool TryResolve(Edge edge, out AssetNode from, out string port, out AssetNode to)
        {
            from = edge.output?.node as AssetNode;
            to = edge.input?.node as AssetNode;
            port = edge.output?.portName;
            return from != null && to != null && !string.IsNullOrEmpty(port);
        }

        /// <summary>图上操作（连线 / 拖放）的公共入口：写回失败时把原因弹给用户。</summary>
        public static bool AttachWithFeedback(ScriptableObject parent, string port, ScriptableObject child)
        {
            bool attached = TryAttach(parent, port, child, out string rejection);
            if (rejection != null)
            {
                // 弹窗延后到本次事件处理结束：模态框会跑自己的消息循环，
                // 在 GraphView 的变更回调中途开这个循环等于在遍历过程中重入。
                string message = rejection;
                EditorApplication.delayCall += () => EditorUtility.DisplayDialog("连接被拒绝", message, "知道了");
            }

            return attached;
        }

        /// <summary>
        /// 把 <paramref name="child"/> 挂到 <paramref name="parent"/> 的指定端口。
        /// 那些原本只在运行期构造定义时才抛出的约束，在此提前拦截——配错不必等跑起来才知道。
        /// <paramref name="rejection"/> 非空表示配置违规需要告知用户；为空则表示该端口本就不接受此连接，静默忽略即可。
        /// </summary>
        public static bool TryAttach(ScriptableObject parent, string port, ScriptableObject child, out string rejection)
        {
            rejection = null;
            if (parent == null || child == null)
            {
                return false;
            }

            if (parent is GameplayAbilityAsset ability)
            {
                if (port == AssetNode.AnimationPort)
                {
                    if (child is not SkillAnimationAsset animation)
                    {
                        return false;
                    }

                    var so = new SerializedObject(animation);
                    if (string.IsNullOrEmpty(so.FindProperty("clipName")?.stringValue))
                    {
                        rejection = $"「{animation.name}」还没填片段名，驱动无从知道该播哪一段。";
                        return false;
                    }

                    if (!(so.FindProperty("duration")?.floatValue > 0f))
                    {
                        rejection = $"「{animation.name}」的时长必须大于 0：它同时是时间轴的长度与标记点缩放的分母。";
                        return false;
                    }

                    WriteObjectField(ability, "animation", animation);
                    return true;
                }

                if (child is not GameplayEffectAsset effect)
                {
                    return false;
                }

                if (port == AssetNode.CostPort)
                {
                    if (ReadDurationPolicy(effect) != EffectDurationPolicy.Instant)
                    {
                        rejection = $"「{effect.name}」不能作为消耗：消耗必须是 Instant 效果。";
                        return false;
                    }

                    WriteObjectField(ability, "cost", effect);
                    return true;
                }

                if (port == AssetNode.CooldownPort)
                {
                    if (!HasGrantedTag(effect))
                    {
                        rejection = $"「{effect.name}」不能作为冷却：冷却效果必须至少授予一个标签，那个标签就是「在冷却中」的标志。";
                        return false;
                    }

                    WriteObjectField(ability, "cooldown", effect);
                    return true;
                }

                return false;
            }

            if (parent is SkillAnimationAsset owner)
            {
                // 标记点的效果作用于谁（固定目标 / 施法者自己）在时间轴面板上选，这里只管挂效果。
                return child is GameplayEffectAsset markerEffect
                    && AssetNode.TryParseMarkerPort(port, out int index)
                    && WriteMarkerEffect(owner, index, markerEffect);
            }

            if (parent is GameplayEffectAsset parentEffect)
            {
                if (child is not GameplayEffectAsset childEffect)
                {
                    return false;
                }

                if (parentEffect == childEffect)
                {
                    rejection = $"「{parentEffect.name}」不能把自己挂为子效果，那会无限递归。";
                    return false;
                }

                if (!AssetNode.TryParseTriggerPort(port, out EffectTrigger trigger))
                {
                    return false;
                }

                if (HasConditional(parentEffect, childEffect, trigger))
                {
                    rejection = $"「{parentEffect.name}」的「{port}」已经挂着「{childEffect.name}」了。重复挂载会让它在运行期施加两遍。";
                    return false;
                }

                AddConditional(parentEffect, childEffect, trigger);
                return true;
            }

            return false;
        }

        private static void Disconnect(AssetNode from, string port, AssetNode to)
        {
            if (from.Asset is GameplayAbilityAsset ability)
            {
                if (port == AssetNode.CostPort)
                {
                    WriteObjectField(ability, "cost", null);
                }
                else if (port == AssetNode.CooldownPort)
                {
                    WriteObjectField(ability, "cooldown", null);
                }
                else if (port == AssetNode.AnimationPort)
                {
                    WriteObjectField(ability, "animation", null);
                }

                return;
            }

            // 断开只清掉标记点上的效果，标记点本身留在时间轴上——时间点是授权者摆的，不该被一次拖动抹掉。
            if (from.Asset is SkillAnimationAsset animation && AssetNode.TryParseMarkerPort(port, out int index))
            {
                WriteMarkerEffect(animation, index, null);
                return;
            }

            if (from.Asset is GameplayEffectAsset parent && to.Asset is GameplayEffectAsset child
                && AssetNode.TryParseTriggerPort(port, out EffectTrigger trigger))
            {
                RemoveConditional(parent, child, trigger);
            }
        }

        // ================= 序列化读写 =================
        // 一律走 SerializedObject：ApplyModifiedProperties 自带 Undo 记录，
        // 与 Inspector 编辑、撤销重做、资产保存共用同一套机制。

        private static UnityEngine.Object ReadObjectField(ScriptableObject target, string fieldName)
        {
            var so = new SerializedObject(target);
            return so.FindProperty(fieldName)?.objectReferenceValue;
        }

        private static void WriteObjectField(ScriptableObject target, string fieldName, UnityEngine.Object value)
        {
            var so = new SerializedObject(target);
            SerializedProperty prop = so.FindProperty(fieldName);
            if (prop == null)
            {
                return;
            }

            prop.objectReferenceValue = value;
            so.ApplyModifiedProperties();
        }

        private static EffectDurationPolicy ReadDurationPolicy(GameplayEffectAsset effect)
        {
            var so = new SerializedObject(effect);
            return (EffectDurationPolicy)(so.FindProperty("durationPolicy")?.enumValueIndex ?? 0);
        }

        private static bool HasGrantedTag(GameplayEffectAsset effect)
        {
            var so = new SerializedObject(effect);
            SerializedProperty granted = so.FindProperty("tags")?.FindPropertyRelative("granted");
            if (granted == null || !granted.isArray)
            {
                return false;
            }

            for (int i = 0; i < granted.arraySize; i++)
            {
                if (!string.IsNullOrEmpty(granted.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name").stringValue))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>该 (子效果, 触发时机) 组合是否已挂载。重复挂载会让它在运行期施加两遍。</summary>
        private static bool HasConditional(GameplayEffectAsset parent, GameplayEffectAsset child, EffectTrigger trigger)
        {
            var so = new SerializedObject(parent);
            SerializedProperty list = so.FindProperty("conditionalEffects");
            if (list == null || !list.isArray)
            {
                return false;
            }

            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty element = list.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative("effect")?.objectReferenceValue == child &&
                    element.FindPropertyRelative("trigger")?.enumValueIndex == (int)trigger)
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddConditional(GameplayEffectAsset parent, GameplayEffectAsset child, EffectTrigger trigger)
        {
            var so = new SerializedObject(parent);
            SerializedProperty list = so.FindProperty("conditionalEffects");
            if (list == null)
            {
                return;
            }

            int index = list.arraySize;
            list.InsertArrayElementAtIndex(index);
            SerializedProperty element = list.GetArrayElementAtIndex(index);
            element.FindPropertyRelative("effect").objectReferenceValue = child;
            element.FindPropertyRelative("trigger").enumValueIndex = (int)trigger;
            so.ApplyModifiedProperties();
        }

        private static void RemoveConditional(GameplayEffectAsset parent, GameplayEffectAsset child, EffectTrigger trigger)
        {
            var so = new SerializedObject(parent);
            SerializedProperty list = so.FindProperty("conditionalEffects");
            if (list == null || !list.isArray)
            {
                return;
            }

            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty element = list.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative("effect")?.objectReferenceValue != child ||
                    element.FindPropertyRelative("trigger")?.enumValueIndex != (int)trigger)
                {
                    continue;
                }

                list.DeleteArrayElementAtIndex(i);
                so.ApplyModifiedProperties();
                return;
            }
        }

        private static IEnumerable<(GameplayEffectAsset, string)> ReadConditionalEffects(GameplayEffectAsset effect)
        {
            var so = new SerializedObject(effect);
            SerializedProperty list = so.FindProperty("conditionalEffects");
            if (list == null || !list.isArray)
            {
                yield break;
            }

            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty element = list.GetArrayElementAtIndex(i);
                var child = element.FindPropertyRelative("effect")?.objectReferenceValue as GameplayEffectAsset;
                if (child == null)
                {
                    continue;
                }

                int trigger = element.FindPropertyRelative("trigger")?.enumValueIndex ?? 0;
                yield return (child, AssetNode.TriggerPort((EffectTrigger)trigger));
            }
        }

        /// <summary>标记点上挂着的效果，连同它所属端口的名字。</summary>
        private static IEnumerable<(GameplayEffectAsset, string)> ReadMarkerEffects(SkillAnimationAsset animation)
        {
            var so = new SerializedObject(animation);
            SerializedProperty list = so.FindProperty("markers");
            if (list == null || !list.isArray)
            {
                yield break;
            }

            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty element = list.GetArrayElementAtIndex(i);
                var effect = element.FindPropertyRelative("effect")?.objectReferenceValue as GameplayEffectAsset;
                if (effect == null)
                {
                    continue;
                }

                yield return (effect, AssetNode.MarkerPort(i, element.FindPropertyRelative("key")?.stringValue));
            }
        }

        /// <summary>写回（或清空）某个标记点上的效果。下标越界说明端口比数据新，此时什么都不做。</summary>
        private static bool WriteMarkerEffect(SkillAnimationAsset animation, int index, GameplayEffectAsset effect)
        {
            var so = new SerializedObject(animation);
            SerializedProperty list = so.FindProperty("markers");
            if (list == null || !list.isArray || index < 0 || index >= list.arraySize)
            {
                return false;
            }

            list.GetArrayElementAtIndex(index).FindPropertyRelative("effect").objectReferenceValue = effect;
            so.ApplyModifiedProperties();
            return true;
        }

        // ================= 右键菜单 =================

        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            if (_root != null)
            {
                evt.menu.AppendAction("新建效果资产", _ => CreateEffectAsset());
                evt.menu.AppendSeparator();
            }

            base.BuildContextualMenu(evt);
        }

        /// <summary>在根能力所在文件夹新建一个效果资产并选中它，随后连线即可挂上。</summary>
        private void CreateEffectAsset()
        {
            string rootPath = AssetDatabase.GetAssetPath(_root);
            string folder = string.IsNullOrEmpty(rootPath) ? "Assets" : Path.GetDirectoryName(rootPath);
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/新效果.asset");

            var asset = ScriptableObject.CreateInstance<GameplayEffectAsset>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);

            // 新资产尚未被引用，图上暂不显示；提示用户下一步操作。
            Debug.Log($"已创建 {path}。把它从 Project 面板拖进图中的端口即可挂上，或在 Inspector 里直接引用。", asset);
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter)
        {
            var compatible = new List<Port>();
            ports.ForEach(port =>
            {
                if (port == startPort || port.node == startPort.node || port.direction == startPort.direction)
                {
                    return;
                }

                // 端口类型即该端口收发的资产类型：输出端口声明自己接受什么，输入端口就是所属资产的类型。
                // 于是「往标记点连一段动画」「往消耗连另一个能力」这类连不通的线根本不会出现在候选里——
                // 若放任拉出去，写回时会被静默丢弃，等于让用户白拉一次。
                // 能力节点的输入端口也由此自然出局：没有任何输出端口接受能力资产，它只是个视觉锚点。
                Port output = startPort.direction == Direction.Output ? startPort : port;
                Port input = startPort.direction == Direction.Output ? port : startPort;
                if (!output.portType.IsAssignableFrom(input.portType))
                {
                    return;
                }

                compatible.Add(port);
            });
            return compatible;
        }
    }
}
