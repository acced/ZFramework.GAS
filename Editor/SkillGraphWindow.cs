using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    /// <summary>
    /// 技能结构图窗口。选中一个 <see cref="GameplayAbilityAsset"/> 即展开其消耗、冷却与整条子效果链。
    /// 双击资产也会打开本窗口。
    /// </summary>
    public sealed class SkillGraphWindow : EditorWindow
    {
        private SkillGraphView _graph;
        private AnimationTimelinePanel _timeline;
        private ObjectField _rootField;
        private Label _status;

        [MenuItem("Window/ZFramework GAS/技能结构图")]
        public static SkillGraphWindow Open()
        {
            var window = GetWindow<SkillGraphWindow>();
            window.titleContent = new GUIContent("技能结构图");
            window.minSize = new Vector2(480f, 320f);
            return window;
        }

        /// <summary>双击 Project 面板里的能力资产时打开并定位。</summary>
        [UnityEditor.Callbacks.OnOpenAsset]
        private static bool OnOpenAsset(int instanceId, int line)
        {
            if (EditorUtility.EntityIdToObject(instanceId) is not GameplayAbilityAsset ability)
            {
                return false;
            }

            Open().Show(ability);
            return true;
        }

        /// <summary>以给定能力为根重建图。也是测试的入口。</summary>
        public void Show(GameplayAbilityAsset ability)
        {
            EnsureUI();
            _rootField.SetValueWithoutNotify(ability);
            Rebuild(ability);
        }

        /// <summary>当前图中的节点数，供测试断言。</summary>
        public int NodeCount => _graph?.NodeCount ?? 0;

        private void CreateGUI() => EnsureUI();

        private void EnsureUI()
        {
            if (_graph != null)
            {
                return;
            }

            var toolbar = new Toolbar();
            _rootField = new ObjectField("能力资产")
            {
                objectType = typeof(GameplayAbilityAsset),
                allowSceneObjects = false,
                style = { width = 360f },
            };
            _rootField.RegisterValueChangedCallback(evt => Rebuild(evt.newValue as GameplayAbilityAsset));
            toolbar.Add(_rootField);

            var refresh = new ToolbarButton(() => Rebuild(_rootField.value as GameplayAbilityAsset)) { text = "刷新" };
            toolbar.Add(refresh);

            _status = new Label { style = { unityTextAlign = TextAnchor.MiddleLeft, marginLeft = 8f } };
            toolbar.Add(_status);

            rootVisualElement.Add(toolbar);

            _graph = new SkillGraphView();
            // 把能力资产拖进空白处即可切换根节点；图上的编辑写回后也走这里，工具栏与时间轴一并同步。
            _graph.RebuildRequested += ability =>
            {
                _rootField.SetValueWithoutNotify(ability);
                Rebuild(ability);
            };
            rootVisualElement.Add(_graph);

            _timeline = new AnimationTimelinePanel();
            // 增删标记点会改变动画节点的端口数量，图必须跟着重画。
            _timeline.StructureChanged += () => Rebuild(_rootField.value as GameplayAbilityAsset);
            rootVisualElement.Add(_timeline);
        }

        private void Rebuild(GameplayAbilityAsset ability)
        {
            _graph.Rebuild(ability);
            _timeline.Show(ability != null ? ability.Animation : null);
            _status.text = ability == null ? "未选择能力资产" : $"{_graph.NodeCount} 个节点";
        }

        /// <summary>资产在别处被改动后，重新打开窗口即可看到最新结构；这里保证切换焦点时也刷新。</summary>
        private void OnFocus()
        {
            if (_graph != null && _rootField?.value is GameplayAbilityAsset ability)
            {
                Rebuild(ability);
            }
        }
    }
}
