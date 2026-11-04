using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;
using Object = UnityEngine.Object;

namespace ZFramework.GAS.Editor
{
    public readonly struct ChangeSet
    {
        public readonly Object Asset;
        public readonly string[] Paths;
        public readonly bool Structure;
        public ChangeSet(Object asset, bool structure = false, params string[] paths)
        { Asset = asset; Structure = structure; Paths = paths ?? Array.Empty<string>(); }
    }

    public sealed class SelectionModel
    {
        public Object Asset { get; private set; }
        public string Track { get; private set; }
        public string ElementId { get; private set; }
        public event Action Changed;
        public void Select(Object asset, string track = null, string id = null)
        {
            if (Asset == asset && Track == track && ElementId == id) return;
            Asset = asset; Track = track; ElementId = id;
            Changed?.Invoke();
        }
    }

    public sealed class PlaybackModel
    {
        public float Time { get; private set; }
        public int SeekVersion { get; private set; }
        private bool _playing;
        public bool Playing
        {
            get => _playing;
            set { if (_playing == value) return; _playing = value; Changed?.Invoke(); }
        }
        public bool Loop = true;
        public float Speed = 1;
        private bool _live;
        public bool Live
        {
            get => _live;
            set { if (_live == value) return; _live = value; if (value) _playing = false; Changed?.Invoke(); }
        }
        public event Action Changed;
        public void Seek(float time, float duration)
        { SeekVersion++; Time = Mathf.Clamp(time, 0, Mathf.Max(0, duration)); Changed?.Invoke(); }
        public void Tick(float delta, float duration)
        {
            if (Live || !Playing || duration <= 0) return;
            float next = Time + delta * Speed;
            if (next >= duration) { if (Loop) { next %= duration; SeekVersion++; } else { next = duration; _playing = false; } }
            Time = Mathf.Clamp(next, 0, duration); Changed?.Invoke();
        }
    }

    /// <summary>One document per window. Views project serialized assets; they never own authoring data.</summary>
    public sealed class SkillDocument : IDisposable
    {
        private readonly Dictionary<Object, SerializedObject> _serialized = new();
        private readonly Dictionary<Object, uint> _contentHashes = new();
        private readonly Dictionary<Object, HashSet<string>> _pendingChanges = new();
        private bool _notifyScheduled;
        private readonly List<ScriptableObject> _assets = new();
        private readonly List<ScriptableObject> _loose = new();
        private readonly Dictionary<ScriptableObject, HashSet<ScriptableObject>> _parents = new();
        public GameplayAbilityAsset Root { get; private set; }
        public IReadOnlyList<ScriptableObject> Assets => _assets;
        public SelectionModel Selection { get; } = new();
        public PlaybackModel Playback { get; } = new();
        public LayoutStore Layout { get; private set; }
        public GameObject PreviewPrefab { get; set; }
        private float _previewEnd;
        public event Action PreviewRangeChanged;
        public float PreviewEnd
        {
            get=>_previewEnd;
            internal set{if(_previewEnd==value)return;_previewEnd=value;PreviewRangeChanged?.Invoke();}
        }
        public float PlaybackDuration => Mathf.Max(Root?.Animation?.Duration??0,PreviewEnd);
        private readonly DebugSubscribers<Action<ChangeSet>> _observers=new();
        public event Action<ChangeSet> Changed {add=>_observers.Add(value);remove=>_observers.Remove(value);}
        private void Publish(ChangeSet change)
        {
            foreach(var handler in _observers.Snapshot)
                try{handler(change);}catch(Exception error){Debug.LogException(error);}
        }
        public event Action<string> Feedback;
        public event Action FlowRequested;
        public void OpenFlow() => FlowRequested?.Invoke();
        public SkillDocument() { ModuleFileUndo.AfterReplay += OnUndo; }

        public SerializedObject Serialized(Object asset)
        {
            if (asset == null) return null;
            if (!_serialized.TryGetValue(asset, out var so)) _serialized[asset] = so = new SerializedObject(asset);
            so.UpdateIfRequiredOrScript();
            return so;
        }
        public void Open(GameplayAbilityAsset root, bool assignMissingIds = true)
        {
            Layout?.Save(); Layout?.Dispose();
            ClearPendingChanges();
            ClearPool(); _loose.Clear(); PreviewEnd=0; Root = root;
            Layout = new LayoutStore(root,trackUndo:true);
            foreach (string key in Layout.LooseAssets)
            {
                var pieces = key.Split(':');
                if (pieces.Length != 2 || !long.TryParse(pieces[1], out long localId)) continue;
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(pieces[0])).OfType<ScriptableObject>())
                {
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long id) && id == localId) _loose.Add(asset);
                }
            }
            PreviewPrefab = Layout.PreviewPrefab;
            Playback.Playing = false; Playback.Seek(0, root?.Animation?.Duration ?? 0);
            Reload();
            // Missing IDs are editor metadata only. Preserve all existing fields and array order.
            if (assignMissingIds)
                foreach (var animation in _assets.OfType<SkillAnimationAsset>()) TimelineCommands.EnsureIds(animation);
            CaptureContentHashes();
            Selection.Select(root);
            Publish(new ChangeSet(root, true));
        }
        public void AddLoose(ScriptableObject asset)
        {
            if (asset != null && !_loose.Contains(asset))
            { _loose.Add(asset); Layout.LooseAssets.Add(AssetReferences.Key(asset)); Layout.MarkDirty(); Layout.Save(); }
            Notify(new ChangeSet(asset, true));
        }
        public void RemoveLoose(ScriptableObject asset)
        { _loose.Remove(asset); Layout.LooseAssets.Remove(AssetReferences.Key(asset)); Layout.MarkDirty(); Layout.Save(); }
        public void Notify(ChangeSet change)
        {
            SkillContentIndex.Invalidate(change.Asset);
            bool structure = change.Structure || change.Paths.Length == 0;
            if (!structure && change.Asset != null)
                foreach (string path in change.Paths)
                {
                    var property = Serialized(change.Asset).FindProperty(path);
                    if (property == null || property.isArray || property.propertyType == SerializedPropertyType.ObjectReference ||
                        property.propertyType == SerializedPropertyType.ManagedReference || path.EndsWith(".Array.size", StringComparison.Ordinal))
                    { structure = true; break; }
                }
            if (structure || Array.Exists(change.Paths,p=>p.EndsWith(".ownership",StringComparison.Ordinal))) ModuleCallIndex.Changed(change.Asset);
            if (structure) Reload();
            if (change.Asset is SkillAnimationAsset edited) TimelineCommands.EnsureIds(edited, true);
            else if (structure) foreach (var animation in _assets.OfType<SkillAnimationAsset>()) TimelineCommands.EnsureIds(animation, true);
            var invalid = new HashSet<ScriptableObject>();
            void Invalidate(ScriptableObject asset)
            {
                if (asset == null || !invalid.Add(asset)) return;
                if (asset is GameplayAbilityAsset ability) ability.InvalidateDefinition();
                else if (asset is GameplayEffectAsset effect) effect.InvalidateDefinition();
                else if (asset is SkillAnimationAsset animation) animation.InvalidateDefinition();
                if (_parents.TryGetValue(asset, out var parents)) foreach (var parent in parents) Invalidate(parent);
            }
            if (change.Asset is ScriptableObject changed) Invalidate(changed);
            if (structure) foreach (var asset in _assets) Invalidate(asset);
            if (structure) CaptureContentHashes();
            else if (change.Asset != null) _contentHashes[change.Asset] = ContentHash(change.Asset);
            if (Selection.Asset == null) Selection.Select(Root);
            Publish(new ChangeSet(change.Asset, structure, change.Paths));
        }
        private uint ContentHash(Object asset)
        {
            using var property = Serialized(asset).GetIterator();
            // The iterator's initial root is a sentinel. Hash the serialized fields instead;
            // isExpanded is editor state and is deliberately absent from contentHash.
            uint hash = 2166136261;
            if (property.Next(true))
                do { hash = unchecked((hash ^ property.contentHash) * 16777619); } while (property.Next(false));
            return hash;
        }
        private void CaptureContentHashes()
        {
            _contentHashes.Clear();
            foreach (var asset in _assets) if (asset != null) _contentHashes[asset] = ContentHash(asset);
        }
        /// <summary>Foldout/binding synchronization can emit SerializedPropertyChangeEvent without
        /// changing asset data. Only real serialized edits invalidate definitions; defer view rebuilds
        /// until Unity has finished dispatching the current input/binding event.</summary>
        internal void NotifySerializedChange(Object asset, string path)
        {
            if (asset == null) return;
            var hash = ContentHash(asset);
            if (_contentHashes.TryGetValue(asset, out var previous) && previous == hash) return;
            _contentHashes[asset] = hash;
            if (!_pendingChanges.TryGetValue(asset, out var paths)) _pendingChanges.Add(asset, paths = new HashSet<string>());
            paths.Add(path);
            if (_notifyScheduled) return;
            _notifyScheduled = true; EditorApplication.update += FlushSerializedChanges;
        }
        private void FlushSerializedChanges()
        {
            EditorApplication.update -= FlushSerializedChanges; _notifyScheduled = false;
            var pending = _pendingChanges.ToArray(); _pendingChanges.Clear();
            foreach (var change in pending)
                if (change.Key != null) Notify(new ChangeSet(change.Key, false, change.Value.ToArray()));
        }
        internal void RefreshIfChanged()
        {
            if (Root == null) return;
            foreach (var asset in _assets)
                if (asset == null || !_contentHashes.TryGetValue(asset, out var previous) || ContentHash(asset) != previous)
                { Notify(new ChangeSet(Root, true)); return; }
        }
        public void Report(string message) => Feedback?.Invoke(message);
        public void Save()
        {
            Layout.PreviewPrefab = PreviewPrefab; Layout.Save();
            foreach (var asset in _assets) if (asset != null) AssetDatabase.SaveAssetIfDirty(asset);
            Report("技能已保存"); Publish(new ChangeSet(Root));
        }
        private void Reload()
        {
            _assets.Clear(); _parents.Clear();
            var visited = new HashSet<ScriptableObject>();
            Visit(Root, visited);
            if (Root != null)
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(Root)).OfType<ScriptableObject>())
                    Visit(asset, visited);
            foreach (var asset in _loose) Visit(asset, visited);
            Layout?.PruneDeletedEntries(_assets);
            foreach (var dead in _serialized.Keys.Where(o => o == null || o is ScriptableObject s && !visited.Contains(s)).ToArray())
            { _serialized[dead].Dispose(); _serialized.Remove(dead); }
        }
        private void Visit(ScriptableObject asset, HashSet<ScriptableObject> visited)
        {
            if (asset == null || !visited.Add(asset)) return;
            if (!(asset is GameplayAbilityAsset || asset is GameplayEffectAsset || asset is SkillAnimationAsset || asset is SkillActionModuleAsset || asset is SkillParameterSetAsset)) return;
            _assets.Add(asset);
            foreach (var child in AssetReferences.Children(asset))
            {
                if (!_parents.TryGetValue(child, out var parents)) _parents[child] = parents = new HashSet<ScriptableObject>();
                parents.Add(asset); Visit(child, visited);
            }
        }
        private void OnUndo()
        {
            bool layoutRestored=Layout?.RestoreUndo()==true;
            // Unity restores destroyed subassets as transient objects. Reattach references before
            // rebuilding the projection, otherwise a later save silently loses the restored object.
            if (Root != null)
            {
                var restored = new HashSet<ScriptableObject>();
                void AttachRestored(ScriptableObject owner)
                {
                    if (owner == null || !restored.Add(owner)) return;
                    foreach (var child in AssetReferences.Children(owner))
                    {
                          if (!EditorUtility.IsPersistent(child)) AssetDatabase.AddObjectToAsset(child, owner);
                        AttachRestored(child);
                    }
                }
                AttachRestored(Root);
            }
            foreach (var so in _serialized.Values) if (so.targetObject != null) so.Update();
            Notify(layoutRestored?new ChangeSet(Root,true,"@layout"):new ChangeSet(Root,true));
        }
        private void ClearPool() { foreach (var so in _serialized.Values) so.Dispose(); _serialized.Clear(); }
        private void ClearPendingChanges()
        {
            EditorApplication.update -= FlushSerializedChanges; _notifyScheduled = false; _pendingChanges.Clear();
        }
        public void Dispose()
        {
            ModuleFileUndo.AfterReplay -= OnUndo; ClearPendingChanges();
            _contentHashes.Clear(); Layout?.Save(); Layout?.Dispose(); ClearPool();
        }
    }

    public static class AssetReferences
    {
        public static IEnumerable<ScriptableObject> Children(Object asset)
        {
            using var so = new SerializedObject(asset);
            var p = so.GetIterator();
            while (p.Next(true))
                if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue is ScriptableObject child &&
                    (child is GameplayEffectAsset || child is SkillAnimationAsset || child is SkillActionModuleAsset || child is SkillParameterSetAsset)) yield return child;
        }
        public static void Replace(SerializedObject so, Object from, Object to)
        {
            var p = so.GetIterator();
            while (p.Next(true))
                if (p.propertyType == SerializedPropertyType.ObjectReference && p.objectReferenceValue == from)
                    p.objectReferenceValue = to;
        }
        public static string Key(Object asset)
        {
            if (asset == null) return "";
            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long id)
                ? guid + ":" + id : "instance:" + asset.GetInstanceID();
        }
    }
}
