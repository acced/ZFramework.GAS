using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace ZFramework.GAS.Editor
{
    // File dependencies give candidates; a reference to a skill is not a reference to every
    // subasset in it. Inspect the exact object / serialized pointer before refusing an edit.
    internal static class SkillReferenceGuard
    {
        private static readonly Dictionary<string,string[]> Forward=new(StringComparer.Ordinal);
        private static readonly Dictionary<string,HashSet<string>> Reverse=new(StringComparer.Ordinal);
        private static readonly HashSet<string> Dirty=new(StringComparer.Ordinal);
        private static bool _ready;
        private static bool Supported(string p)
        {
            string ext=Path.GetExtension(p);
            if(string.IsNullOrEmpty(ext) && AssetDatabase.IsValidFolder(p))return false;
            if(ext.ToLowerInvariant() is ".asset" or ".unity" or ".prefab" or ".controller" or ".overridecontroller" or ".playable" or ".anim" or ".signal" or ".mixer" or ".preset")return true;
            var importer=AssetDatabase.GetImporterType(p);
            return importer?.Name=="NativeFormatImporter" || typeof(UnityEditor.AssetImporters.ScriptedImporter).IsAssignableFrom(importer)
                || typeof(ScriptableObject).IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(p));
        }
        internal static void Changed(IEnumerable<string> paths){if(_ready)foreach(string p in paths)if(Forward.ContainsKey(p) || Supported(p))Dirty.Add(p);}
        internal static void Prepare()
        {
            if(!_ready){_ready=true;foreach(var p in AssetDatabase.GetAllAssetPaths())if(Supported(p))Dirty.Add(p);}
            foreach(string p in Dirty.ToArray()){
                Dirty.Remove(p);
                if(Forward.TryGetValue(p,out var old)){
                    foreach(string dep in old)if(Reverse.TryGetValue(dep,out var users)){
                        users.Remove(p);if(users.Count==0)Reverse.Remove(dep);}
                    Forward.Remove(p);
                }
                if(AssetDatabase.GetMainAssetTypeAtPath(p)==null)continue;
                var deps=AssetDatabase.GetDependencies(p,false);Forward[p]=deps;
                foreach(var dep in deps){if(!Reverse.TryGetValue(dep,out var users))Reverse[dep]=users=new(StringComparer.Ordinal);users.Add(p);}
            }
        }

        // Cheap negative test for pure C# owners. Read their *live* fields, including
        // unmarked assignments and nested SerializeReference objects. The SerializedObject
        // iterator still confirms every hit. Native-backed/unknown data takes the slow path.
        private enum Probe { No, Maybe }
        private static readonly Dictionary<Type,FieldInfo[]> Fields=new();
        private static FieldInfo[] ReferenceFields(Type type){
            if(Fields.TryGetValue(type,out var fields))return fields;
            var result=new List<FieldInfo>();
            for(var t=type;t!=null && t!=typeof(ScriptableObject) && t!=typeof(MonoBehaviour);t=t.BaseType)
                foreach(var f in t.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)){
                    var ft=f.FieldType;
                    if(f.IsInitOnly || f.IsNotSerialized || ft.IsPrimitive || ft.IsEnum || ft==typeof(string))continue;
                    if(f.IsPublic || f.IsDefined(typeof(SerializeField),false) || f.IsDefined(typeof(SerializeReference),false))result.Add(f);
                }
            return Fields[type]=result.ToArray();
        }
        private static readonly Dictionary<Type,bool> ProjectTypes=new();
        private static bool ProjectType(Type t){
            if(!ProjectTypes.TryGetValue(t,out bool project))ProjectTypes[t]=project=!t.Assembly.GetName().Name.StartsWith("Unity",StringComparison.Ordinal);
            return project;
        }
        private sealed class IdentityComparer : IEqualityComparer<object>{
            public new bool Equals(object x,object y)=>ReferenceEquals(x,y);
            public int GetHashCode(object o)=>System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }
        private static bool CouldReference(Object owner,HashSet<Object> targets,HashSet<object> seen)
        {
            seen.Clear();
            Probe Visit(object value,int depth,bool root=false){
                if(value==null || value is string)return Probe.No;
                if(!root && value is Object asset)return targets.Contains(asset)?Probe.Maybe:Probe.No;
                var t=value.GetType();if(t.IsPrimitive || t.IsEnum)return Probe.No;
                if(depth>32 || value is ISerializationCallbackReceiver)return Probe.Maybe;
                if(!t.IsValueType && !seen.Add(value))return Probe.No;
                if(value is IList list){
                    Type item=t.IsArray ? t.GetElementType() :
                        t.IsGenericType && t.GetGenericTypeDefinition()==typeof(List<>)?t.GetGenericArguments()[0]:null;
                    if(item==null || t.IsArray && t.GetArrayRank()!=1)return Probe.Maybe;
                    if(item.IsPrimitive || item.IsEnum || item==typeof(string))return Probe.No;
                    for(int i=0;i<list.Count;i++)if(Visit(list[i],depth+1)==Probe.Maybe)return Probe.Maybe;
                    return Probe.No;
                }
                if(!ProjectType(t)){
                    // Common serializable Unity values cannot contain asset references.
                    if(value is Vector2 or Vector3 or Vector4 or Quaternion or Color or Color32 or Rect or Bounds or AnimationCurve)return Probe.No;
                    return Probe.Maybe;
                }
                foreach(var field in ReferenceFields(t))if(Visit(field.GetValue(value),depth+1)==Probe.Maybe)return Probe.Maybe;
                return Probe.No;
            }
            return Visit(owner,0,true)==Probe.Maybe;
        }

        // liveOwner is the file Unity just changed via Undo. Its live contents supersede the
        // saved version, which still contains the removed CallModule until the next save.
        internal static string Find(string targetFile,IEnumerable<Object> objects,ICollection<Object> skip=null,string liveOwner=null)
        {
            Prepare();
            var targets=new HashSet<Object>(objects.Where(o=>o!=null));
            bool moving=skip!=null;
            var seen=new HashSet<Object>();
            string Inspect(Object owner){
                if(owner==null || !seen.Add(owner) || skip?.Contains(owner)==true)return null;
                string path=AssetDatabase.GetAssetPath(owner);
                if(!moving && path==targetFile)return null;
                using var so=new SerializedObject(owner);using var p=so.GetIterator();
                bool descend=true;HashSet<long> managed=null;
                while(p.Next(descend)){
                    var type=p.propertyType;
                    descend=type is SerializedPropertyType.Generic or SerializedPropertyType.ExposedReference;
                    if(descend && p.isArray && p.arrayElementType is "char" or "int" or "unsigned int" or "float" or "double" or "string" or "UInt8" or "SInt8")
                        descend=false;
                    if(type==SerializedPropertyType.ManagedReference)
                        descend=p.managedReferenceId>=0 && (managed??=new()).Add(p.managedReferenceId);
                    if(type==SerializedPropertyType.ObjectReference && targets.Contains(p.objectReferenceValue))
                        return (path.Length==0 && owner is Component c?c.gameObject.scene.path:path)+" / "+owner.name+" : "+p.propertyPath;
                }
                return null;
            }

            var candidates=Reverse.TryGetValue(targetFile,out var users)?new HashSet<string>(users):new HashSet<string>();
            if(moving)candidates.Add(targetFile);
            if(!string.IsNullOrEmpty(liveOwner))candidates.Add(liveOwner);
            foreach(var path in candidates){
                if(!moving && path==targetFile)continue;
                if(path.EndsWith(".unity",StringComparison.OrdinalIgnoreCase)){
                    if(SerializedPointer(path,targetFile,targets,true))return path;continue;
                }
                if(path!=liveOwner && path!=targetFile && SerializedPointer(path,targetFile,targets,false))return path;
                if(AssetDatabase.GetImporterType(path) is Type type && typeof(UnityEditor.AssetImporters.ScriptedImporter).IsAssignableFrom(type)){
                    var hit=Inspect(AssetImporter.GetAtPath(path));if(hit!=null)return hit;
                }
                foreach(var asset in AssetDatabase.LoadAllAssetsAtPath(path)){
                    var hit=Inspect(asset);if(hit!=null)return hit;
                    if(asset is GameObject go)foreach(var component in go.GetComponentsInChildren<Component>(true)){
                        hit=Inspect(component);if(hit!=null)return hit;}
                }
            }
            // Direct field assignments, current prefab stage and unsaved scene edits can be
            // newer than AssetDatabase. Restrict the live scan to project objects.
            var live=new List<Object>();
            var managed=new HashSet<object>(new IdentityComparer());
            bool ScriptOwner(Object o){
                if(o is not (MonoBehaviour or ScriptableObject))return false;
                for(var t=o.GetType();t!=typeof(ScriptableObject) && t!=typeof(MonoBehaviour);t=t.BaseType)
                    if(!ProjectType(t))return false;
                return true;
            }
            string AddLive(Object o){
                if(o==null || targets.Contains(o) || seen.Contains(o) || skip?.Contains(o)==true)return null;
                if(ScriptOwner(o)){
                    if(!CouldReference(o,targets,managed))return null;
                    return Inspect(o);
                }
                if(moving || AssetDatabase.GetAssetPath(o)!=targetFile)live.Add(o);
                return null;
            }
            foreach(var so in Resources.FindObjectsOfTypeAll<ScriptableObject>())
                if(EditorUtility.IsPersistent(so) && !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(so))){var hit=AddLive(so);if(hit!=null)return hit;}
            foreach(var c in Resources.FindObjectsOfTypeAll<Component>())
                if(c!=null && c is not Transform && (EditorUtility.IsPersistent(c) || c.gameObject.scene.IsValid())){var hit=AddLive(c);if(hit!=null)return hit;}
            // The remaining native owners may have opaque reference tables (e.g. a
            // PlayableDirector). Reject batches in native code; confirm positives by exact pointer.
            if(live.Count>0 && EditorUtility.CollectDependencies(live.ToArray()).Any(targets.Contains)){
                for(int i=0;i<live.Count;i+=64){
                    var batch=new Object[Math.Min(64,live.Count-i)];live.CopyTo(i,batch,0,batch.Length);
                    if(batch.Length!=live.Count && !EditorUtility.CollectDependencies(batch).Any(targets.Contains))continue;
                    foreach(var o in batch){var hit=Inspect(o);if(hit!=null)return hit;}
                }
            }
            return null;
        }

        private static bool SerializedPointer(string scene,string asset,HashSet<Object> targets,bool unknown)
        {
            // Avoid loading/switching scenes. Binary scenes with a direct file dependency
            // cannot prove subasset exclusivity; retain the file conservatively.
            string physical=FileUtil.GetPhysicalPath(scene);
            if(!File.Exists(physical))return unknown;
            using var stream=File.OpenRead(physical);
            if(stream.ReadByte()!='%' || stream.ReadByte()!='Y' || stream.ReadByte()!='A' ||
                stream.ReadByte()!='M' || stream.ReadByte()!='L')return unknown;
            using var reader=new StreamReader(stream);
            var ids=new HashSet<long>();
            foreach(var o in targets)if(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o,out string _,out long id))ids.Add(id);
            string guid=AssetDatabase.AssetPathToGUID(asset);
            var pointer=new Regex(@"\{\s*fileID:\s*(-?\d+)\s*,\s*guid:\s*"+guid+@"\b");
            string line;
            while((line=reader.ReadLine())!=null)if(line.Contains(guid))
                foreach(Match match in pointer.Matches(line))
                    if(long.TryParse(match.Groups[1].Value,out var id) && ids.Contains(id))return true;
            return false;
        }
    }
}
