using System;
using System.Collections.Generic;
using GameplayTags;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class DetailsPanel : VisualElement, IDisposable
    {
        private readonly SkillDocument _doc;
        private readonly Label _title;
        private readonly ScrollView _body;
        public DetailsPanel(SkillDocument doc)
        {
            _doc = doc; AddToClassList("details-panel");
            _title = new Label("Details"); _title.AddToClassList("panel-heading"); Add(_title);
            _body = new ScrollView(); _body.style.flexGrow = 1; Add(_body);
            doc.Selection.Changed += Rebuild; doc.Changed += Changed;
        }
        private void Changed(ChangeSet change) { if (change.Structure) Rebuild(); }
        private void Rebuild()
        {
            _body.Unbind(); _body.Clear(); var asset = _doc.Selection.Asset;
            _title.text = "Details · " + (asset != null ? asset.name : "未选择");
            if (asset == null) { _body.Add(new HelpBox("选择节点或时间轴元素以编辑属性。", HelpBoxMessageType.Info)); return; }
            var so = _doc.Serialized(asset);
            string track = _doc.Selection.Track;
            var element = track != null ? TimelineCommands.Find(so, track, _doc.Selection.ElementId) : null;
            if (element != null)
            {
                var field = new PropertyField(element, TrackTypeRegistry.Get(track)?.DisplayName ?? "事件"); _body.Add(field); field.BindProperty(element);
                var effect = element.FindPropertyRelative("effect")?.objectReferenceValue;
                if (effect != null)
                {
                    var heading = new Label(effect.name + " · 效果设置"); heading.AddToClassList("panel-heading"); _body.Add(heading);
                    AddInspector(_doc.Serialized(effect));
                }
                field.RegisterCallback<SerializedPropertyChangeEvent>(e => schedule.Execute(() => _doc.Notify(new ChangeSet(asset, false, track))));
            }
            else
            {
                var name = new TextField("名称") { value = asset.name, isDelayed = true };
                name.RegisterValueChangedCallback(e =>
                {
                    if (string.IsNullOrWhiteSpace(e.newValue)) return;
                    if (AssetDatabase.IsMainAsset(asset))
                    {
                        // 主资产名必须与文件名一致，只改 m_Name 会让 Unity 报名称不匹配。文件改名不进 Undo。
                        string error = AssetDatabase.RenameAsset(AssetDatabase.GetAssetPath(asset), e.newValue.Trim());
                        if (!string.IsNullOrEmpty(error)) { _doc.Report(error); name.SetValueWithoutNotify(asset.name); return; }
                        _doc.Notify(new ChangeSet(asset, true));
                    }
                    else EditCommand.Run(_doc, "重命名资产", asset, target => target.FindProperty("m_Name").stringValue = e.newValue);
                }); _body.Add(name);
                if (asset != _doc.Root && asset is ScriptableObject child)
                {
                    bool embedded = SkillAssetFactory.IsEmbedded(_doc.Root, asset);
                    var hint = new Label(embedded ? "内嵌资产 · 随技能一起保存" : "↗ 外部共享资产 · 修改会影响引用它的技能"); hint.AddToClassList("muted"); _body.Add(hint);
                    _body.Add(new Button(() =>
                    {
                        string path = embedded ? EditorUtility.SaveFilePanelInProject("外置效果", asset.name, "asset", "选择独立资产的位置") : null;
                        if (!embedded || !string.IsNullOrEmpty(path)) SkillAssetFactory.Convert(_doc, child, !embedded, path);
                    }) { text = embedded ? "外置为共享资产…" : "复制为内嵌资产" });
                }
                AddInspector(so);
            }
        }
        private void AddInspector(SerializedObject so)
        {
            var inspector = new InspectorElement(so); _body.Add(inspector);
            inspector.RegisterCallback<SerializedPropertyChangeEvent>(e =>
            {
                string path = e.changedProperty.propertyPath;
                var asset = so.targetObject;
                schedule.Execute(() => _doc.Notify(new ChangeSet(asset, false, path)));
            });
        }
        public void Dispose() { _doc.Selection.Changed -= Rebuild; _doc.Changed -= Changed; _body.Unbind(); }
    }
    internal sealed class AbilitySettingsPanel : ScrollView
    {
        private readonly SkillDocument _doc;
        public AbilitySettingsPanel(SkillDocument doc) { _doc = doc; AddToClassList("ability-settings"); }
        public void Rebuild()
        {
            this.Unbind(); Clear(); if (_doc.Root == null) return;
            var so = _doc.Serialized(_doc.Root);
            foreach (var pair in new[] { ("abilityTags", "能力标识"), ("activationRequiredTags", "激活需要"), ("activationBlockedTags", "激活阻断"), ("activationOwnedTags", "激活期间授予"), ("cancelAbilitiesWithTags", "激活时取消"), ("callerParameters", "调用方参数声明") })
            {
                var field = new PropertyField(so.FindProperty(pair.Item1), pair.Item2); Add(field); field.BindProperty(so.FindProperty(pair.Item1));
                field.RegisterCallback<SerializedPropertyChangeEvent>(_ => _doc.Notify(new ChangeSet(_doc.Root)));
            }
            var title = new Label("门禁模拟器"); title.AddToClassList("panel-heading"); Add(title);
            var input = new TextField("当前持有标签") { multiline = true, tooltip = "用逗号或换行分隔已注册标签" }; Add(input);
            var result = new HelpBox("输入持有者标签，验证 Core 的激活标签门禁。", HelpBoxMessageType.Info); Add(result);
            input.RegisterValueChangedCallback(e =>
            {
                try
                {
                    var owned = new GameplayTagCountContainer(); var unknown = new List<string>();
                    foreach (var name in e.newValue.Split(new[] { ',', '，', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    { var tag = GameplayTagManager.RequestTag(name.Trim()); if (tag.IsValid) owned.AddTag(tag); else unknown.Add(name.Trim()); }
                    GameplayTagSet Tags(string path)
                    {
                        var p = so.FindProperty(path); var tags = new List<GameplayTag>();
                        for (int i = 0; i < p.arraySize; i++)
                        { string name = p.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name").stringValue; var tag = GameplayTagManager.RequestTag(name); if (tag.IsValid) tags.Add(tag); else if (!string.IsNullOrEmpty(name)) unknown.Add(name); }
                        return new GameplayTagSet(tags);
                    }
                    var definition = new GameplayAbilityDefinition(activationRequiredTags: Tags("activationRequiredTags"), activationBlockedTags: Tags("activationBlockedTags"));
                    bool matches = definition.ActivationRequirements.Matches(owned);
                    result.text = unknown.Count > 0 ? "标签未注册：" + string.Join("，", unknown) : matches ? "✓ 标签门禁通过（消耗与冷却需在运行期判定）" : "✕ 缺少必需标签，或持有阻断标签";
                    result.messageType = unknown.Count > 0 || !matches ? HelpBoxMessageType.Warning : HelpBoxMessageType.Info;
                }
                catch (Exception ex) { result.text = ex.Message; result.messageType = HelpBoxMessageType.Warning; }
            });
        }
    }
}
