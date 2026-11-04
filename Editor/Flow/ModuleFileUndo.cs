using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    [InitializeOnLoad]
    internal static class ModuleFileUndo
    {
        // Unity 6 provides stack lengths and the group at a valid undo-stack index. Names are
        // presentation only. A redo journal stays reachable until its branch is discarded.
        private const BindingFlags Static=BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public;
        private static T OptionalApi<T>(string method,params Type[] signature) where T:Delegate
        {
            try{return typeof(Undo).GetMethod(method,Static,null,signature,null)?.CreateDelegate(typeof(T)) as T;}
            catch(ArgumentException){return null;}
            catch(MemberAccessException){return null;}
        }
        private static readonly Action<List<string>,List<string>> ReadHistory=OptionalApi<Action<List<string>,List<string>>>("GetRecords",typeof(List<string>),typeof(List<string>));
        private static readonly Func<int,int> GroupAt=OptionalApi<Func<int,int>>("GetGroupFromStack",typeof(int));
        private static double _nextPrune;
        internal static event Action AfterReplay;
        static ModuleFileUndo()
        {
            Undo.undoRedoPerformed+=()=>{try{Restore();}finally{AfterReplay?.Invoke();}};
            EditorApplication.update+=()=>{double now=EditorApplication.timeSinceStartup;if(now<_nextPrune)return;
                _nextPrune=now+15;Prune();};
        }
        internal static void Prune()
        {
            var entries=ModuleFileJournal.instance.entries;
            entries.RemoveAll(e=>{if(e.token!=null && !e.obsolete)return false;ReleaseEntry(e);return true;});
            if(entries.Count==0 || ReadHistory==null)return;
            var undo=new List<string>();var redo=new List<string>();ReadHistory(undo,redo);
            int first=undo.Count!=0 && GroupAt!=null?GroupAt(0):int.MinValue;
            entries.RemoveAll(e=>{
                if(e.token.published ? undo.Count>0 && (e.group<=0 || e.group>=first) : redo.Count>0)return false;
                ReleaseEntry(e);return true;
            });
        }
        internal static void Track(string path,ScriptableObject owner,string list,int index,string original,CallModuleAction call,string operation,ModuleFileJournal.InlineEffects effects,string[] inlineIds)
        {
            SkillReferenceGuard.Prepare();
            var token=ScriptableObject.CreateInstance<ModulePublication>();token.hideFlags=HideFlags.HideAndDontSave;
            var e=new ModuleFileJournal.Entry{token=token,owner=GlobalObjectId.GetGlobalObjectIdSlow(owner).ToString(),path=path,effects=effects,
                guid=AssetDatabase.AssetPathToGUID(path),list=list,index=index,original=original,inlineIds=inlineIds,live=call.module,group=Undo.GetCurrentGroup(),callJson=JsonUtility.ToJson(call),published=true};
            Undo.RegisterCompleteObjectUndo(token,operation);token.published=true;
            ModuleFileJournal.instance.entries.Add(e);
        }
        // RevertAllDownToGroup rolls back object state, but file creation is not a Unity Undo operation.
        private static bool _reverting;
        internal static void RevertTransaction(int group){
            // RevertAllDownToGroup synchronously fires undoRedoPerformed. The file pass
            // below owns this rollback; replaying its journal twice can delete/read twice.
            _reverting=true;
            try{EditCommand.Cleanup(()=>Undo.RevertAllDownToGroup(group));}
            finally{_reverting=false;Rollback(group);}
        }
        internal static void Rollback(int group)
        {
            var entries=ModuleFileJournal.instance.entries;
            for(int i=entries.Count-1;i>=0;i--){var e=entries[i];if(e.group<group)continue;
                try{if(e.published && !e.obsolete && !e.repairOnly)Unpublish(e);}
                catch(Exception error){Report(e,"回滚",error);}
                finally{Retire(e);}
            }
        }
        private static void Retire(ModuleFileJournal.Entry e){
            ModuleFileJournal.instance.entries.Remove(e);ReleaseEntry(e);
        }
        private static void ReleaseEntry(ModuleFileJournal.Entry e){
            e.effects?.DiscardParked();
            if(e.token!=null)EditCommand.Cleanup(()=>UnityEngine.Object.DestroyImmediate(e.token));
        }
        internal static string RecoveryFolder=>Path.Combine(Path.GetDirectoryName(Application.dataPath),"GASRecovery~","Modules");
        private static void Report(ModuleFileJournal.Entry e,string operation,Exception error,string inlineStatus=null){
            string disk=File.Exists(e.path)?"该路径仍有文件，请检查后处理。":"目标路径没有模块文件。";
            string backup=e.recovery!=null?" 恢复副本（含 .meta）："+e.recovery:"";
            // Unity otherwise displays only the innermost exception in a collapsed console row.
            Debug.LogError($"{operation}模块文件失败：{e.path}。{disk}{inlineStatus}{backup} 不再重试此文件操作。\n{error}");
        }
        private enum InlineState { Restored,AlreadyInline,OwnerMissing,SlotChanged,Failed }
        private static string Describe(InlineState state)=>state switch{
            InlineState.Restored=>" 动作已恢复为内联。",
            InlineState.AlreadyInline=>" 无需恢复：原动作已经内联。",
            InlineState.OwnerMissing=>" 动作未恢复：所属资产不存在。",
            InlineState.SlotChanged=>" 动作未恢复：原动作槽位已更改，现有内容保留。",
            _=>" 动作未恢复：内联修复抛出异常。"
        };
        private static InlineState Inline(ModuleFileJournal.Entry e){
            var owner=Owner(e);if(owner==null)return InlineState.OwnerMissing;
            bool repaired=RepairOwner(e,null);
            bool changed=AttachRestored(e);
            if(!e.published && e.effects!=null)changed|=e.effects.ParkUnusedRepairs(owner);
            // Saving the whole file here would also commit unrelated inspector edits.
            if(repaired || changed)EditorUtility.SetDirty(owner);
            if(repaired)return InlineState.Restored;
            using var so=new SerializedObject(owner);var list=so.FindProperty(e.list);
            if(list!=null && e.inlineIds!=null && e.inlineIds.Length>0){
                for(int i=0;i+e.inlineIds.Length<=list.arraySize;i++){
                    int n=0;while(n<e.inlineIds.Length && (list.GetArrayElementAtIndex(i+n).managedReferenceValue as SkillAction)?.Id==e.inlineIds[n])n++;
                    if(n==e.inlineIds.Length)return InlineState.AlreadyInline;
                }
            }
            return InlineState.SlotChanged;
        }
        private static void Replay(ModuleFileJournal.Entry e,bool publish){
            bool repaired=false;
            try{if(publish)Publish(e,ref repaired);else Unpublish(e);}
            catch(Exception error){
                if(publish){
                    e.repairOnly=true;e.published=e.token.published;
                    try{
                        if(e.data!=null){
                            // Outside Library and Assets: survives deleting Unity caches, and can
                            // be copied or versioned without importing duplicate asset GUIDs.
                            var folder=Path.Combine(RecoveryFolder,Guid.NewGuid().ToString("N"));
                            Directory.CreateDirectory(folder);
                            var path=Path.GetFullPath(Path.Combine(folder,Path.GetFileName(e.path)));
                            WritePair(path,e.data,e.meta);e.recovery=path;
                            File.WriteAllLines(Path.Combine(folder,Receipt),new[]{Path.GetFileName(path),Digest(path),Digest(path+".meta")});
                            e.data=e.meta=null;
                        }
                    }catch(Exception backupError){error=new AggregateException(error,backupError);}
                    InlineState state;
                    try{state=Inline(e);if(repaired && state==InlineState.AlreadyInline)state=InlineState.Restored;}
                    catch(Exception inlineError){state=InlineState.Failed;error=new AggregateException(error,inlineError);}
                    Report(e,"重做",error,Describe(state));
                }else{Retire(e);Report(e,"撤销",error);}
            }
        }
        [MenuItem("Tools/ZFramework GAS/打开模块恢复目录")]
        private static void OpenRecovery(){Directory.CreateDirectory(RecoveryFolder);EditorUtility.RevealInFinder(Path.GetFullPath(RecoveryFolder));}
        private const string Receipt=".gas-module-recovery";
        private static string Digest(string path){
            using var stream=File.OpenRead(path);using var sha=System.Security.Cryptography.SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(stream));
        }
        internal static void ClearRecoveryDirectory(string dir){
            if(string.IsNullOrEmpty(dir) || !Directory.Exists(dir))return;
            var modules=Path.GetDirectoryName(Path.GetFullPath(dir));
            if(!string.Equals(modules,Path.GetFullPath(RecoveryFolder),StringComparison.OrdinalIgnoreCase))return;
            if(!Guid.TryParseExact(Path.GetFileName(dir),"N",out _) || (File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)return;
            string receipt=Path.Combine(dir,Receipt);if(!File.Exists(receipt))return;
            var lines=File.ReadAllLines(receipt);if(lines.Length!=3 || Path.GetFileName(lines[0])!=lines[0] || !lines[0].EndsWith(".asset",StringComparison.OrdinalIgnoreCase))return;
            var path=Path.Combine(dir,lines[0]);var meta=path+".meta";
            if(!File.Exists(path) || !File.Exists(meta) || Digest(path)!=lines[1] || Digest(meta)!=lines[2])return;
            File.Delete(path);File.Delete(meta);File.Delete(receipt);
            if(Directory.GetFileSystemEntries(dir).Length==0)Directory.Delete(dir);
            void RemoveEmpty(string path){
                if(Directory.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)==0 && !Directory.EnumerateFileSystemEntries(path).Any())Directory.Delete(path);
            }
            RemoveEmpty(modules);RemoveEmpty(Path.GetDirectoryName(modules));
        }
        [MenuItem("Tools/ZFramework GAS/清理模块恢复副本…")]
        private static void ClearRecovery(){
            if(!Directory.Exists(RecoveryFolder) || !EditorUtility.DisplayDialog("清理模块恢复副本","确认已经检查或另存恢复文件？仅删除此目录中工具生成的 .asset/.meta 对，无法撤销。","清理副本","取消"))return;
            foreach(var dir in Directory.GetDirectories(RecoveryFolder))
                EditCommand.Cleanup(()=>ClearRecoveryDirectory(dir));
        }
        internal static void Restore()
        {
            if(_reverting)return;
            var entries=ModuleFileJournal.instance.entries;
            entries.RemoveAll(e=>{if(e.token!=null)return false;ReleaseEntry(e);return true;});
            var ordered=entries.ToArray();
            for(int i=ordered.Length-1;i>=0;i--){
                var e=ordered[i];if(!e.repairOnly && !e.obsolete && e.token!=null && e.published && !e.token.published)Replay(e,false);
            }
            foreach(var e in ordered){
                if(e.obsolete || e.token==null)continue;
                if(e.repairOnly){
                    bool changed=e.published!=e.token.published;e.published=e.token.published;
                    if(changed)try{
                        var state=Inline(e);string message="模块未重建："+e.path+Describe(state);
                        if(state==InlineState.Restored)Debug.Log(message);
                        else if(state!=InlineState.AlreadyInline)Debug.LogWarning(message);
                    }catch(Exception error){Debug.LogError("模块未重建："+e.path+Describe(InlineState.Failed)+"\n"+error);}
                }else if(!e.published && e.token.published)Replay(e,true);
            }
        }
        private static void Unpublish(ModuleFileJournal.Entry e)
        {
            var path=AssetDatabase.GUIDToAssetPath(e.guid);
            if(string.IsNullOrEmpty(path) || !File.Exists(path) || e.live==null || AssetDatabase.LoadMainAssetAtPath(path)!=e.live){
                // A non-undoable external delete/replacement is not our file operation.
                RepairOwner(e,null);AttachRestored(e);e.obsolete=true;
                Debug.LogWarning("模块已在撤销历史外删除或替换；停止重放此文件操作："+e.path);return;
            }
            RepairOwner(e,null);
            AttachRestored(e);
            var module=AssetDatabase.LoadAssetAtPath<SkillActionModuleAsset>(path);
            AssetDatabase.SaveAssetIfDirty(module);
            e.path=path;e.data=File.ReadAllBytes(path);e.meta=File.ReadAllBytes(path+".meta");
            if(StillReferenced(e,path))
            {
                Debug.Log("已撤销提取；其他资产仍引用模块，保留文件："+path);
                e.retained=module;e.published=false;return;
            }
            if(!AssetDatabase.DeleteAsset(path))throw new IOException("不能撤销模块文件："+path);
            e.published=false;
        }
        private static void Publish(ModuleFileJournal.Entry e,ref bool repaired)
        {
            bool failedParent=GlobalObjectId.TryParse(e.owner,out var ownerId) && ModuleFileJournal.instance.entries.Any(parent=>parent.repairOnly && parent.guid==ownerId.assetGUID.ToString());
            if(failedParent || Owner(e)==null)throw new IOException("所属资产没有恢复，不创建孤立的子模块："+e.path);
            string present=AssetDatabase.GUIDToAssetPath(e.guid);
            bool guidOccupied=!string.IsNullOrEmpty(present) && File.Exists(present) &&
                AssetDatabase.AssetPathToGUID(present,AssetPathToGUIDOptions.OnlyExistingAssets)==e.guid;
            if(guidOccupied && e.retained!=null && AssetDatabase.LoadMainAssetAtPath(present)==e.retained)e.path=present; // Retained because another asset referenced it.
            else if(e.data!=null && (File.Exists(e.path) || File.Exists(e.path+".meta") || guidOccupied))
            {
                e.path=AssetDatabase.GenerateUniqueAssetPath(e.path);
                // NativeFormatImporter can reuse a recently deleted GUID even for an unrelated
                // newly-created asset. Never hijack that file or publish a duplicate GUID.
                if(guidOccupied)
                {
                    string guid=Guid.NewGuid().ToString("N");
                    var meta=System.Text.Encoding.UTF8.GetString(e.meta);
                    e.meta=System.Text.Encoding.UTF8.GetBytes(meta.Replace("guid: "+e.guid,"guid: "+guid));
                    // Child extractions identify this module as their owner by global object ID.
                    // Keep their redo records attached to the restored module, not the occupant.
                    foreach(var child in ModuleFileJournal.instance.entries)
                        if(GlobalObjectId.TryParse(child.owner,out var childOwner) && childOwner.assetGUID.ToString()==e.guid)
                            child.owner=child.owner.Replace("-"+e.guid+"-","-"+guid+"-");
                    e.guid=guid;
                }
                Debug.LogWarning("原路径或 GUID 已被占用，已保留现有文件并恢复模块到："+e.path);
            }
            bool created=false;
            try{
            if(!File.Exists(e.path))
            {
                if(e.data==null){
                    // The retained file was deleted outside Undo. Do not retry on unrelated undo events.
                    throw new IOException("保留的模块文件已被外部删除："+e.path);
                }
                WritePair(e.path,e.data,e.meta);created=true;
                AssetDatabase.ImportAsset(e.path,ImportAssetOptions.ForceSynchronousImport);
            }
            e.live=AssetDatabase.LoadAssetAtPath<SkillActionModuleAsset>(e.path);
            if(e.live==null)throw new IOException("恢复的文件无法加载为模块："+e.path);
            RepairOwner(e,e.live);
            e.retained=null;e.data=e.meta=null;e.published=true;
            }catch{
                // Only remove a file installed by this attempt; never delete an occupied/retained path.
                bool inlined=false;
                if(created)EditCommand.Cleanup(()=>{
                    if(!File.ReadAllBytes(e.path).SequenceEqual(e.data) || !File.ReadAllBytes(e.path+".meta").SequenceEqual(e.meta))return;
                    // Unity has already redone the owner's reference. Repair our own slot
                    // before deciding whether anyone else actually acquired the new file.
                    e.repairOnly=true;inlined=RepairOwner(e,null);AttachRestored(e);
                    var other=SkillReferenceGuard.Find(e.path,AssetDatabase.LoadAllAssetsAtPath(e.path),null,AssetDatabase.GetAssetPath(Owner(e)));
                    if(other!=null){Debug.LogWarning("新文件已被 "+other+" 引用，保留："+e.path);return;}
                    if(!AssetDatabase.DeleteAsset(e.path))throw new IOException("未能清理新写入的模块："+e.path);
                });
                repaired|=inlined;throw;
            }
        }
        private static void WritePair(string path,byte[] data,byte[] meta){
            bool assetCreated=false,metaCreated=false;
            try {
                using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){assetCreated=true;file.Write(data,0,data.Length);}
                using(var file=new FileStream(path+".meta",FileMode.CreateNew,FileAccess.Write,FileShare.None)){metaCreated=true;file.Write(meta,0,meta.Length);}
            }catch{
                if(metaCreated)EditCommand.Cleanup(()=>File.Delete(path+".meta"));
                if(assetCreated)EditCommand.Cleanup(()=>File.Delete(path));
                throw;
            }
        }
        private static ScriptableObject Owner(ModuleFileJournal.Entry e)=>
            GlobalObjectId.TryParse(e.owner,out var id)?GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as ScriptableObject:null;

        private static bool AttachRestored(ModuleFileJournal.Entry e)
        {
            var owner=Owner(e);if(owner==null)return false;bool changed=false;
            var visited=new HashSet<ScriptableObject>();
            void Attach(ScriptableObject parent){
                if(!visited.Add(parent))return;
                foreach(var effect in AssetReferences.Children(parent).OfType<GameplayEffectAsset>()){
                    if(!EditorUtility.IsPersistent(effect)){AssetDatabase.AddObjectToAsset(effect,owner);changed=true;}
                    if(AssetDatabase.GetAssetPath(effect)==AssetDatabase.GetAssetPath(owner))Attach(effect);
                }
            }
            Attach(owner);return changed;
        }
        private static bool StillReferenced(ModuleFileJournal.Entry e,string path)
        {
            return SkillReferenceGuard.Find(path,AssetDatabase.LoadAllAssetsAtPath(path),null,
                AssetDatabase.GetAssetPath(Owner(e)))!=null;
        }
        private static bool RepairOwner(ModuleFileJournal.Entry e,SkillActionModuleAsset module)
        {
            if(!GlobalObjectId.TryParse(e.owner,out var id) || GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) is not ScriptableObject owner)return false;
            using var so=new SerializedObject(owner);var list=so.FindProperty(e.list);
            if(list==null)return false;
            int index=e.index;var savedCall=JsonUtility.FromJson<CallModuleAction>(e.callJson);
            if(e.repairOnly && module==null){
                // Native Undo can restore the broken call at another index. Match identity,
                // and leave later user edits (including a valid replacement module) alone.
                index=-1;
                for(int i=0;i<list.arraySize;i++)if(list.GetArrayElementAtIndex(i).managedReferenceValue is CallModuleAction c
                    && c.Id==savedCall.Id && (c.module==null || AssetDatabase.GetAssetPath(c.module)==e.path)){index=i;break;}
            }
            if(index<0 || index>=list.arraySize)return false;
            var slot=list.GetArrayElementAtIndex(index);
            if(slot.managedReferenceValue is CallModuleAction existing && existing.Id==savedCall.Id && module!=null)
            {
                existing.module=module;slot.FindPropertyRelative("module").objectReferenceValue=module;
            }
            else
            {
                // Native Undo cannot target the old instance of a module deleted and reimported
                // by a parent extraction. Replay only the original slice, never the entire owner.
                var original=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
                try
                {
                    EditorJsonUtility.FromJsonOverwrite(e.original,original);
                    if(module!=null)
                    {
                        for(int i=0;i<original.actions.Length;i++)
                            if(index+i>=list.arraySize || (list.GetArrayElementAtIndex(index+i).managedReferenceValue as SkillAction)?.Id!=original.actions[i].Id)return false;
                        for(int i=1;i<original.actions.Length;i++)list.DeleteArrayElementAtIndex(index+1);
                        savedCall.module=module;list.GetArrayElementAtIndex(index).managedReferenceValue=savedCall;
                    }
                    else
                    {
                        if((slot.managedReferenceValue as SkillAction)?.Id!=savedCall.Id)return false;
                        if(e.repairOnly)e.effects?.Bind(original,owner);
                        for(int i=0;i<original.actions.Length;i++){
                            if(i>0)list.InsertArrayElementAtIndex(index+i);
                            list.GetArrayElementAtIndex(index+i).managedReferenceValue=original.actions[i];}
                    }
                }
                finally{UnityEngine.Object.DestroyImmediate(original);}
            }
            so.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(owner);return true;
        }
    }
}
