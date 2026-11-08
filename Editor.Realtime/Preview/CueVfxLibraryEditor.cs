using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    [CustomEditor(typeof(CueVfxLibrary))]
    public sealed class CueVfxLibraryEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("角色的 Cue VFX Player 引用此配置。技能时间轴选择同名 Cue 标签后，运行时与预览都会加载对应 Prefab。\n挂点填写相对角色的路径；留空使用角色根节点。地面特效取消勾选“跟随挂点”。", MessageType.Info);
            DrawDefaultInspector();
            foreach (var binding in ((CueVfxLibrary)target).bindings)
            {
                if (binding == null) continue;
                if (string.IsNullOrWhiteSpace(binding.cueTag.Name) || binding.prefab == null)
                    EditorGUILayout.HelpBox("有映射缺少 Cue 标签或特效 Prefab，该项不能播放。", MessageType.Warning);
            }
            EditorGUILayout.HelpBox("停止发射模式会保留已有粒子，直到消失或达到尾迹最长时间。同一标签可配置多个 Prefab；重叠 Cue 区间共用一组表现，最后一个区间结束时停止。", MessageType.None);
        }
    }
}
