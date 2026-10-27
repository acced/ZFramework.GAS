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
        [Serializable] public sealed class CommentLayout { public string title; public Rect rect; }
        [Serializable] private sealed class Data
        {
            public string owner = "ZFramework.GAS.Layout.v2";
            public string previousUserData;
            public string previewGuid;
            public List<NodeLayout> nodes = new();
            public List<CommentLayout> comments = new();
            public List<string> looseAssets = new();
        }
        // 每次按资产取路径：技能可能在编辑期间被重命名或移动，缓存的路径会让保存静默落空。
        private readonly GameplayAbilityAsset _root;
        private string Path => AssetDatabase.GetAssetPath(_root);
        private Data _data = new();
        private bool _dirty;
        public List<CommentLayout> Comments => _data.comments;
        public List<string> LooseAssets => _data.looseAssets;
        public GameObject PreviewPrefab
        {
            get => AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(_data.previewGuid ?? ""));
            set { string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value)); if (_data.previewGuid != guid) { _data.previewGuid = guid; _dirty = true; } }
        }
        public LayoutStore(GameplayAbilityAsset root)
        {
            _root = root;
            var importer = AssetImporter.GetAtPath(Path);
            if (importer == null || string.IsNullOrEmpty(importer.userData)) return;
            string json = importer.userData;
            if (json.Contains("ZFramework.GAS.Layout.v2"))
            { try { _data = JsonUtility.FromJson<Data>(json) ?? new Data(); } catch (ArgumentException) { _data.previousUserData = json; } }
            else _data.previousUserData = json;
        }
        public Vector2 Position(UnityEngine.Object asset, Vector2 fallback)
            => Position(AssetReferences.Key(asset), fallback);
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
        public void MarkDirty() => _dirty = true;
        public void Save()
        {
            string path = Path;
            if (!_dirty || string.IsNullOrEmpty(path)) return;
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) return;
            importer.userData = JsonUtility.ToJson(_data);
            EditorUtility.SetDirty(importer);
            AssetDatabase.WriteImportSettingsIfDirty(path);
            _dirty = false;
        }
    }
}
