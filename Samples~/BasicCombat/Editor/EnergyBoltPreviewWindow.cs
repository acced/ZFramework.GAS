using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Editor;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Samples.BasicCombat.Editor
{
    /// <summary>The training arena's top-down route diagram. It shares the target projection with the skill editor.</summary>
    public sealed class EnergyBoltPreviewWindow : EditorWindow
    {
        private enum Formation { 直线, 接力, 扇形 }
        [SerializeField] private GameplayAbilityAsset skill;
        [SerializeField] private Formation formation;
        [SerializeField] private int count=3, aim=1, level=1;
        [SerializeField] private float spacing=2;
        private SkillDocument doc;
        private readonly List<Vector3> points=new();
        private readonly List<SkillPreviewContact> contacts=new();
        private SkillPreviewSimulation projection;
        private ObjectField assetField;
        private IMGUIContainer diagram;
        private Slider clock;
        private VisualElement stops;
        private Label hint;
        public float Time => doc.Playback.Time;

        [MenuItem("Tools/ZFramework GAS/BasicCombat/连锁路线预演")]
        private static void FromMenu() => Open(Selection.activeObject as GameplayAbilityAsset ?? Example("分叉闪电"));
        private static GameplayAbilityAsset Example(string name) => AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(
            BasicCombatSample.Folder+"/Skills/"+name+".asset");
        public static EnergyBoltPreviewWindow Open(GameplayAbilityAsset asset)
        {
            var window=GetWindow<EnergyBoltPreviewWindow>(); window.titleContent=new GUIContent("BasicCombat · 连锁路线");
            window.ShowAsset(asset);return window;
        }
        public void ShowAsset(GameplayAbilityAsset asset)
        {
            skill=asset;
            if(doc==null)return;
            assetField.SetValueWithoutNotify(asset);
            if(asset!=null && doc.Root!=asset)doc.Open(asset,false);
            Refresh();
        }
        private void CreateGUI()
        {
            doc=new SkillDocument();projection=new SkillPreviewSimulation();
            var root=rootVisualElement;root.Clear();
            assetField=new ObjectField("技能"){objectType=typeof(GameplayAbilityAsset),allowSceneObjects=false};
            assetField.RegisterValueChangedCallback(e=>ShowAsset(e.newValue as GameplayAbilityAsset));root.Add(assetField);
            var row=new VisualElement{style={flexDirection=FlexDirection.Row}}; root.Add(row);
            foreach(var name in new[]{"分叉闪电","逐个弹射"})row.Add(new Button(()=>ShowAsset(Example(name))){text=name});
            var options=new Foldout{text="预演设置",value=false};root.Add(options);
            var spacingField=new FloatField("目标距离"){value=spacing};options.Add(spacingField);
            spacingField.RegisterValueChangedCallback(e=>{spacing=Mathf.Max(.1f,e.newValue);Refresh();});
            var layout=new EnumField("排布",formation);options.Add(layout);layout.RegisterValueChangedCallback(e=>{formation=(Formation)e.newValue;Refresh();});
            void Number(string label,int value,Action<int> set){var input=new IntegerField(label){value=value};options.Add(input);input.RegisterValueChangedCallback(e=>{set(e.newValue);Refresh();});}
            Number("目标数",count,v=>count=Mathf.Clamp(v,1,32));Number("瞄准目标（0 为地面）",aim,v=>aim=Mathf.Max(0,v));Number("等级",level,v=>level=Mathf.Max(1,v));
            hint=new Label();root.Add(hint);
            diagram=new IMGUIContainer(Draw){style={minHeight=120,flexGrow=1}};root.Add(diagram);
            clock=new Slider("时间（秒）",0,1){showInputField=true};root.Add(clock);
            clock.RegisterValueChangedCallback(e=>Seek(e.newValue));
            var scroll=new ScrollView{style={maxHeight=140}};root.Add(scroll);stops=scroll.contentContainer;
            doc.Changed+=Changed;
            Undo.undoRedoPerformed+=Refresh;EditorApplication.projectChanged+=Refresh;
            ShowAsset(skill);
        }
        private void Changed(ChangeSet _) => Refresh();
        private void Refresh()
        {
            if(doc==null || diagram==null)return;
            points.Clear();contacts.Clear();stops.Clear();
            if(skill==null){hint.text="请选择技能，或先生成 BasicCombat 示例。";clock.SetEnabled(false);diagram.MarkDirtyRepaint();return;}
            for(int i=0;i<count;i++){
                float u=count==1?.5f:i/(count-1f);
                points.Add(formation switch {
                    Formation.接力=>new Vector3(i%2==0?0:spacing*.35f,0,spacing*(1+i)),
                    Formation.扇形=>Quaternion.Euler(0,(u-.5f)*120,0)*Vector3.forward*spacing,
                    _=>new Vector3((i-(count-1)*.5f)*.8f,0,spacing) });
            }
            try{
                projection.AimIndex=aim;
                projection.ProjectContacts(doc,points,Vector3.forward*spacing,level,null,contacts.Add);
                contacts.Sort((a,b)=>{int cmp=a.Arrival.CompareTo(b.Arrival);if(cmp!=0)return cmp;cmp=a.Launch.CompareTo(b.Launch);return cmp!=0?cmp:a.ToUnit.CompareTo(b.ToUnit);});
                doc.PreviewEnd=projection.End;clock.highValue=Mathf.Max(.001f,doc.PlaybackDuration);clock.SetEnabled(true);
                Seek(Mathf.Min(Time,clock.highValue));
                foreach(var hit in contacts){
                    string from=hit.FromUnit<0?"施法者":"#"+(hit.FromUnit+1);
                    var button=new Button(()=>Seek(hit.Arrival)){text=$"{from} → #{hit.ToUnit+1} · {(hit.Action is ShowTargetLinkAction?"闪电":"飞弹")} {hit.Arrival:0.00}s",userData=hit.Arrival};
                    stops.Add(button);
                }
                hint.text=contacts.Count==0?"当前配置没有命中路线。":"黄线：飞弹　青线：闪电　虚线：待发　点击命中定位";
            }catch(Exception e){contacts.Clear();clock.SetEnabled(false);hint.text=e.Message;}
            diagram.MarkDirtyRepaint();
        }
        private void Seek(float t){doc.Playback.Seek(t,doc.PlaybackDuration);clock.SetValueWithoutNotify(Time);diagram.MarkDirtyRepaint();}
        private void Draw()
        {
            var rect=GUILayoutUtility.GetRect(50,10000,80,10000);if(Event.current.type!=EventType.Repaint || points.Count==0)return;
            float minX=0,maxX=0,minZ=0,maxZ=0;
            void Include(Vector3 v){minX=Mathf.Min(minX,v.x);maxX=Mathf.Max(maxX,v.x);minZ=Mathf.Min(minZ,v.z);maxZ=Mathf.Max(maxZ,v.z);}
            foreach(var p in points)Include(p);foreach(var h in contacts){Include(h.From);Include(h.To);}
            float scale=Mathf.Min((rect.width-70)/Mathf.Max(.5f,maxX-minX),(rect.height-50)/Mathf.Max(.5f,maxZ-minZ));
            Vector2 At(Vector3 p)=>rect.center+new Vector2(p.x-(minX+maxX)*.5f,(minZ+maxZ)*.5f-p.z)*Mathf.Max(1,scale);
            Handles.BeginGUI();var saved=Handles.color;
            foreach(var hit in contacts){
                Handles.color=hit.Action is ShowTargetLinkAction?Color.cyan:Color.yellow;
                var a=At(hit.From);var b=At(hit.To);
                if(Time<hit.Launch)Handles.DrawDottedLine(a,b,4);else Handles.DrawLine(a,b,2);
            }
            void Pin(Vector3 p,string name){var v=At(p);Handles.color=Color.white;Handles.DrawSolidDisc(v,Vector3.forward,3);GUI.Label(new Rect(v.x+4,v.y-10,55,22),name);}
            Pin(Vector3.zero,"施法者");for(int i=0;i<points.Count;i++)Pin(points[i],"#"+(i+1));
            Handles.color=saved;Handles.EndGUI();
        }
        private void OnDisable(){
            Undo.undoRedoPerformed-=Refresh;EditorApplication.projectChanged-=Refresh;
            if(doc!=null){doc.Changed-=Changed;doc.Dispose();doc=null;}
            projection?.Dispose();projection=null;
        }
    }
}
