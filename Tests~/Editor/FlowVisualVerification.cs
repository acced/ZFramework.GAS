using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;
namespace ZFramework.GAS.Editor.Tests
{
    // Reads the GAS window's own backing surface. Never captures the desktop or other applications.
    public static class FlowVisualVerification
    {
        private static SkillEditorWindow _w;private static double _due;private static int _stage;
        public static void Run()
        {
            var skill=AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>("Assets/GAS Samples/BasicCombat/Skills/斩击.asset");
            if(skill==null)throw new InvalidOperationException("生成示例后再验证流程图。");
            _w=ScriptableObject.CreateInstance<SkillEditorWindow>();_w.ShowUtility();_w.position=new Rect(10,30,1420,820);_w.Show(skill);
            var marker=_w.Document.Serialized(skill.Animation).FindProperty("markers").GetArrayElementAtIndex(0);
            _w.Document.Selection.Select(skill.Animation,"markers",marker.FindPropertyRelative("id").stringValue);_w.Document.OpenFlow();
            _w.Document.Playback.Seek(.32f,skill.Animation.Duration);_stage=0;_due=EditorApplication.timeSinceStartup+1;EditorApplication.update+=Step;
        }
        private static void Step()
        {
            if(EditorApplication.timeSinceStartup<_due)return;
            try
            {
                var flow=_w.rootVisualElement.Q<SkillFlowView>();var graph=flow.Q<GraphView>();
                if(_stage==0 || _stage==2){graph.FrameAll();_stage++;_due=EditorApplication.timeSinceStartup+.8;return;}
                var parent=typeof(EditorWindow).GetField("m_Parent",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(_w);
                var view=typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView");
                var flags=BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Public;view.GetMethod("RepaintImmediately",flags).Invoke(parent,null);
                int width=Mathf.RoundToInt(_w.position.width*EditorGUIUtility.pixelsPerPoint),height=Mathf.RoundToInt(_w.position.height*EditorGUIUtility.pixelsPerPoint);
                var rt=RenderTexture.GetTemporary(width,height);var old=RenderTexture.active;
                var texture=new Texture2D(width,height,TextureFormat.RGB24,false);
                try
                {
                    view.GetMethod("GrabPixels",flags).Invoke(parent,new object[]{rt,new Rect(0,0,width,height)});
                    RenderTexture.active=rt;texture.ReadPixels(new Rect(0,0,width,height),0,0);
                    if(SystemInfo.graphicsUVStartsAtTop)
                    {
                        var px=texture.GetPixels32();var line=new Color32[width];
                        for(int y=0;y<height/2;y++){int a=y*width,b=(height-y-1)*width;Array.Copy(px,a,line,0,width);Array.Copy(px,b,px,a,width);Array.Copy(line,0,px,b,width);}
                        texture.SetPixels32(px);
                    }
                    texture.Apply();
                    string dir=Path.Combine(Directory.GetCurrentDirectory(),"GASValidation~");Directory.CreateDirectory(dir);
                    var file=_stage==1?"flow-entry":"flow-module";
                    File.WriteAllBytes(Path.Combine(dir,file+".png"),texture.EncodeToPNG());
                    File.WriteAllText(Path.Combine(dir,file+".txt"),string.Join("\n",graph.nodes.ToList().Select(n=>n.title+" "+n.GetPosition())));
                }
                finally{RenderTexture.active=old;RenderTexture.ReleaseTemporary(rt);UnityEngine.Object.DestroyImmediate(texture);}
                if(_stage==1)
                {
                    var doc=_w.Document;
                    var action=(CallModuleAction)doc.Serialized(doc.Root.Animation).FindProperty("markers.Array.data[0].actions.Array.data[0]").managedReferenceValue;
                    doc.Selection.Select(action.module);doc.OpenFlow();
                    var toggles=_w.rootVisualElement.Q<PreviewPanel>().Query<Toggle>().ToList();
                    toggles.First(t=>t.label=="假人").value=true;toggles.First(t=>t.label=="范围").value=true;
                    doc.Playback.Seek(.37f,doc.Root.Animation.Duration);
                    _stage=2;_due=EditorApplication.timeSinceStartup+.5;return;
                }
                EditorApplication.update-=Step;_w.Close();EditorApplication.Exit(0);
            }
            catch(Exception e){EditorApplication.update-=Step;Debug.LogException(e);EditorApplication.Exit(1);}
        }
    }
}
