using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class SkillDiffWindow : EditorWindow
    {
        private ObjectField _skill; private ScrollView _results;
        [MenuItem("Window/ZFramework GAS/技能语义 Diff")]
        public static void Open() => GetWindow<SkillDiffWindow>("技能语义 Diff");
        private void CreateGUI()
        {
            rootVisualElement.Clear(); _skill = new ObjectField("当前技能") { objectType = typeof(GameplayAbilityAsset), value = Selection.activeObject as GameplayAbilityAsset }; rootVisualElement.Add(_skill);
            rootVisualElement.Add(new Button(() =>
            {
                if (_skill.value is not GameplayAbilityAsset ability) return;
                string path = EditorUtility.SaveFilePanel("保存语义快照", "", ability.name + ".skill", "json");
                if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, JsonUtility.ToJson(SkillSemanticDiff.Capture(ability), true));
            }) { text = "保存当前参数 / 动作 / 时间轴 / 引用快照" });
            rootVisualElement.Add(new Button(() =>
            {
                if (_skill.value is not GameplayAbilityAsset ability) return;
                string path = EditorUtility.OpenFilePanel("与已保存快照比较", "", "json"); if (string.IsNullOrEmpty(path)) return;
                var before = JsonUtility.FromJson<SkillSemanticDiff.Snapshot>(File.ReadAllText(path)); var after = SkillSemanticDiff.Capture(ability);
                _results.Clear(); var diff = SkillSemanticDiff.Compare(before, after); _results.Add(new Label(diff.Count + " 个业务字段变化"));
                foreach (var entry in diff) _results.Add(new Label(entry.Label + "\n" + (entry.Before ?? "（不存在）") + " → " + (entry.After ?? "（已删除）")) { style = { whiteSpace = WhiteSpace.Normal, marginBottom = 10 } });
                var layout = new Foldout { text = before.layout == after.layout ? "编辑器布局未变化" : "编辑器布局变化（与业务数据分开）", value = false };
                layout.Add(new Label(after.layout) { style = { whiteSpace = WhiteSpace.Normal } }); _results.Add(layout);
            }) { text = "与快照比较…" });
            rootVisualElement.Add(new Button(()=>
            {
                if(_skill.value is not GameplayAbilityAsset ours)return;
                string b=EditorUtility.OpenFilePanel("共同基础快照","","json");if(string.IsNullOrEmpty(b))return;
                string t=EditorUtility.OpenFilePanel("另一方快照","","json");if(string.IsNullOrEmpty(t))return;
                var conflict=new List<string>();
                var merged=SkillSemanticDiff.Merge(JsonUtility.FromJson<SkillSemanticDiff.Snapshot>(File.ReadAllText(b)),SkillSemanticDiff.Capture(ours),JsonUtility.FromJson<SkillSemanticDiff.Snapshot>(File.ReadAllText(t)),conflict);
                _results.Clear();_results.Add(new Label("三方合并 · "+conflict.Count+" 个冲突"));
                foreach(var key in conflict)_results.Add(new Label(key));
                if(conflict.Count==0){var path=EditorUtility.SaveFilePanel("导出已合并快照","",ours.name+".merged","json");if(!string.IsNullOrEmpty(path))File.WriteAllText(path,JsonUtility.ToJson(merged,true));}
            }){text="与基础 / 另一方快照合并…"});
            _results = new ScrollView(); rootVisualElement.Add(_results);
        }
    }
}

