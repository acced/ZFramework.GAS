using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class SkillNodeSearchProvider : ScriptableObject, ISearchWindowProvider
    {
        private SkillDocument _doc; private ScriptableObject _parent; private PortId? _port; private Vector2 _position;
        private sealed class Entry { public ScriptableObject Asset; public Type Type; public EffectDurationPolicy Policy; public string Name; }
        public void Initialize(SkillDocument doc, ScriptableObject parent, PortId? port, Vector2 position)
        { _doc = doc; _parent = parent; _port = port; _position = position; }
        public List<SearchTreeEntry> CreateSearchTree(SearchWindowContext context)
        {
            var tree = new List<SearchTreeEntry> { new SearchTreeGroupEntry(new GUIContent("添加节点"), 0) };
            if (!_port.HasValue || _port.Value.AcceptedType == typeof(GameplayEffectAsset))
            {
                tree.Add(new SearchTreeGroupEntry(new GUIContent("新建内嵌效果"), 1));
                foreach (var item in new[] { ("瞬时效果", EffectDurationPolicy.Instant), ("持续效果", EffectDurationPolicy.Duration), ("永久效果", EffectDurationPolicy.Infinite) })
                {
                    if (_port?.Kind == PortKind.Cost && item.Item2 != EffectDurationPolicy.Instant) continue;
                    tree.Add(new SearchTreeEntry(new GUIContent(item.Item1)) { level = 2, userData = new Entry { Type = typeof(GameplayEffectAsset), Policy = item.Item2, Name = item.Item1 } });
                }
                tree.Add(new SearchTreeGroupEntry(new GUIContent("引用项目效果"), 1));
                foreach (var effect in SkillIndex.Effects)
                {
                    // 其他技能的内嵌效果归那个技能所有，跨文件引用会把两个技能隐式绑在一起。
                    if (AssetDatabase.IsSubAsset(effect) && !SkillAssetFactory.IsEmbedded(_doc.Root, effect)) continue;
                    if (_port.HasValue && !PortRules.CanAttach(_parent, _port.Value, effect, out _, _doc)) continue;
                    tree.Add(new SearchTreeEntry(new GUIContent(effect.name + "  ↗")) { level = 2, userData = new Entry { Asset = effect } });
                }
            }
            if (!_port.HasValue || _port.Value.Kind == PortKind.Animation)
                tree.Add(new SearchTreeEntry(new GUIContent("新建动画")) { level = 1, userData = new Entry { Type = typeof(SkillAnimationAsset), Name = "技能动画" } });
            return tree;
        }
        public bool OnSelectEntry(SearchTreeEntry entry, SearchWindowContext context)
        {
            if (entry.userData is not Entry item) return false;
            ScriptableObject asset = item.Asset;
            if (asset != null) { if (_port.HasValue) PortRules.Attach(_doc, _parent, _port.Value, asset); else _doc.AddLoose(asset); }
            else asset = SkillAssetFactory.Create(_doc, item.Type, item.Name, item.Policy, _parent, _port);
            _doc.Layout.SetPosition(asset, _position); _doc.Selection.Select(asset);
            _doc.Notify(new ChangeSet(asset, true));
            DestroyImmediate(this); return true;
        }
    }
}
