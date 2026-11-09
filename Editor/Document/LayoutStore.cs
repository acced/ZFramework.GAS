using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    public sealed class LayoutStore
    {
        [Serializable] public sealed class NodeLayout { public string key; public Vector2 position; }
        [Serializable] public sealed class FlowLayout { public string owner, actionId; public Vector2 position; }
        [Serializable] private sealed class Data
        {
            public string owner = "ZFramework.GAS.Layout.v3";
            public List<FlowLayout> flow = new();
            public string previousUserData;
            public string previewGuid;
            public List<NodeLayout> nodes = new();
            public List<string> looseAssets = new();
        }
        // 每次按资产取路径：技能可能在编辑期间被重命名或移动，缓存的路径会让保存静默落空。
        private readonly GameplayAbilityAsset _root;
        private string Path => AssetDatabase.GetAssetPath(_root);
        private Data _data = new();
        private bool _dirty;
        private sealed class UndoState : ScriptableObject { public string json; }
        private UndoState _undo;
        private readonly bool _trackUndo;
        private string _lastSaved;
        public void Dispose(){if(_undo!=null) UnityEngine.Object.DestroyImmediate(_undo);}
        internal bool RestoreUndo()
        {
            if(_undo==null || _undo.json==_lastSaved || string.IsNullOrEmpty(_undo.json))return false;
            _data=JsonUtility.FromJson<Data>(_undo.json);_lastSaved=_undo.json;_dirty=true;Save();return true;
        }
        public List<string> LooseAssets => _data.looseAssets;
        public GameObject PreviewPrefab
        {
            get => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(_data.previewGuid ?? ""));
            set { string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value)); if (_data.previewGuid != guid) { _data.previewGuid = guid; _dirty = true; } }
        }
        public LayoutStore(GameplayAbilityAsset root,bool trackUndo=false)
        {
            _root = root; _trackUndo=trackUndo;
            var importer = AssetImporter.GetAtPath(Path);
            _lastSaved=JsonUtility.ToJson(_data);
            if (importer == null || string.IsNullOrEmpty(importer.userData)) return;
            string json = importer.userData;
            if (json.Contains("ZFramework.GAS.Layout.v2") || json.Contains("ZFramework.GAS.Layout.v3"))
            { try { _data = JsonUtility.FromJson<Data>(json) ?? new Data(); } catch (ArgumentException) { _data.previousUserData = json; } }
            else _data.previousUserData = json;
            _lastSaved=JsonUtility.ToJson(_data);
        }
        public Vector2 Position(UnityEngine.Object asset, Vector2 fallback)
            => Position(AssetReferences.Key(asset), fallback);
        public bool TryGetPosition(UnityEngine.Object asset, out Vector2 position)
        {
            var node = _data.nodes.Find(n => n.key == AssetReferences.Key(asset));
            position = node?.position ?? default;
            return node != null;
        }
        public Vector2 Position(string key, Vector2 fallback)
            => _data.nodes.Find(n => n.key == key)?.position ?? fallback;
        public void SetPosition(UnityEngine.Object asset, Vector2 position)
            => SetPosition(AssetReferences.Key(asset), position);
        public void SetPosition(string key, Vector2 position)
        {
            var node = _data.nodes.Find(n => n.key == key);
            if (node == null) { node = new NodeLayout { key = key }; _data.nodes.Add(node); }
            node.position = position; _dirty = true;
        }
        public Vector2 FlowPosition(string owner, string id, Vector2 fallback)
            => _data.flow?.Find(n => n.owner == owner && n.actionId == id)?.position ?? fallback;
        internal void PruneFlow(string owner,ICollection<string> live)
        {
            if (_data.flow!=null && _data.flow.RemoveAll(n=>n.owner==owner && n.actionId!="$entry" && !live.Contains(n.actionId))>0) _dirty=true;
        }
        internal void PruneDeletedEntries(IEnumerable<ScriptableObject> assets)
        {
            if(_data.flow==null || _data.flow.Count==0)return;
            var valid=new HashSet<string>();
            foreach(var asset in assets){
                string key=AssetReferences.Key(asset)+"#";
                if(asset is GameplayAbilityAsset)valid.Add(key+"activationActions");
                if (SkillEditorExtensions.Supports(asset))
                {
                    using var so = new SerializedObject(asset);
                    foreach (var entry in SkillEditorExtensions.Entries(so)) valid.Add(key + entry.Id);
                }
            }
            if(_data.flow.RemoveAll(e=>!valid.Contains(e.owner))>0)_dirty=true;
        }
        internal void ResetFlowPositions(string owner)
        {
            if (_data.flow != null && _data.flow.RemoveAll(n => n.owner == owner) > 0) _dirty = true;
        }
        public void SetFlowPosition(string owner, string id, Vector2 position)
        {
            _data.flow ??= new List<FlowLayout>();
            var item = _data.flow.Find(n => n.owner == owner && n.actionId == id);
            if (item == null) _data.flow.Add(item = new FlowLayout { owner = owner, actionId = id });
            item.position = position; _dirty = true;
        }
        public void MarkDirty() => _dirty = true;
        public void Save(bool recordUndo=true)
        {
            string path = Path;
            if (!_dirty || string.IsNullOrEmpty(path)) return;
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) return;
            _data.owner = "ZFramework.GAS.Layout.v3";
            string json=JsonUtility.ToJson(_data);
            if(_lastSaved!=json)
            {
                if(_trackUndo && recordUndo)
                {
                    if(_undo==null){_undo=ScriptableObject.CreateInstance<UndoState>();_undo.hideFlags=HideFlags.HideAndDontSave;_undo.json=_lastSaved;}
                    Undo.RecordObject(_undo,"修改技能编辑器布局");
                }
                if(_undo!=null)_undo.json=json;
                _lastSaved=json;
            }
            importer.userData = json;
            EditorUtility.SetDirty(importer);
            AssetDatabase.WriteImportSettingsIfDirty(path);
            _dirty = false;
        }
    }
}
