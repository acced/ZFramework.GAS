using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor.Tests
{
    public sealed class SkillScaleTests
    {
        private const string Folder = "Assets/GASScaleRegression";
        [Serializable] public sealed class Metric { public string name; public double p95Milliseconds; public long allocationCallsPerOperation; }
        [Serializable] public sealed class Report
        {
            public string utc, unity, cpu, os, gpu, note;
            public int nodes = 100, skills = 500;
            public List<Metric> metrics = new();
        }
        private static Metric Measure(string name, Action<int> action, int count = 40,Action<int> before=null)
        {
            var samples = new double[count]; long allocation = 0;
            bool canMeasure = AllocationMeter.Works();
            for (int i = 0; i < count; i++)
            {
                before?.Invoke(i);
                using var meter = canMeasure ? new AllocationMeter() : null;
                long start = Stopwatch.GetTimestamp(); action(i);
                samples[i] = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
                long bytes = meter?.Stop() ?? -1;
                if (bytes < 0) canMeasure = false; else allocation += bytes;
            }
            Array.Sort(samples); return new Metric { name = name, p95Milliseconds = samples[(int)Math.Ceiling(count * .95) - 1], allocationCallsPerOperation = canMeasure ? allocation / count : -1 };
        }
        [UnityTest, Explicit("Creates 500 assets. Run explicitly on the fixed validation machine."), Category("Performance")]
        public IEnumerator MeasureFiveHundredSkillsAndOneHundredNodes()
        {
            TestTags.Initialize(); AssetDatabase.CreateFolder("Assets", "GASScaleRegression");
            SkillEditorWindow window = null;
            var report = new Report { utc = DateTime.UtcNow.ToString("O"), unity = Application.unityVersion, cpu = SystemInfo.processorType,
                os = SystemInfo.operatingSystem, gpu = SystemInfo.graphicsDeviceName,
                note = "Cold means first index/window access in this editor process. Drag measures position change plus synchronous GUI repaint CPU; OS input latency and GPU presentation are not included." };
            try
            {
                var root = ScriptableObject.CreateInstance<GameplayAbilityAsset>(); root.displayName = "Scale 0";
                AssetDatabase.CreateAsset(root, Folder + "/Skill000.asset");
                var module=ScriptableObject.CreateInstance<SkillActionModuleAsset>();module.actions=new SkillAction[]{new ConditionalAction()};
                AssetDatabase.CreateAsset(module,Folder+"/SharedAction.asset");
                GameplayEffectAsset first = null;
                AssetDatabase.StartAssetEditing();
                try
                {
                    for (int i = 1; i < 500; i++)
                    {
                        var skill = ScriptableObject.CreateInstance<GameplayAbilityAsset>(); skill.displayName = "Scale " + i;
                        SkillAction call=new CallModuleAction{module=module};
                        if(i%3==1)call=new ConditionalAction{whenFalse=new[]{call}};
                        if(i%3==2)call=new WaitSkillAction{onCancel=new SkillAction[]{new ConditionalAction{whenTrue=new[]{call}}}};
                        skill.activationActions=new[]{call};
                        AssetDatabase.CreateAsset(skill, Folder + "/Skill" + i.ToString("D3") + ".asset");
                    }
                    var actions = new List<SkillAction>();
                    for (int i = 0; i < 99; i++)
                    {
                        var effect = SkillAssetFactory.AddEmbedded<GameplayEffectAsset>(root, "Effect " + i); if (i == 0) first = effect;
                        actions.Add(new ApplyEffectAction { effect = effect, targets = new TargetQuery { kind = TargetQueryKind.Self } });
                    }
                    root.activationActions = actions.ToArray(); EditorUtility.SetDirty(root);
                }
                finally { AssetDatabase.StopAssetEditing(); }
                AssetDatabase.SaveAssets(); SkillIndex.Invalidate();
                var matches = new List<GameplayAbilityAsset>(512);
                report.metrics.Add(Measure("cold search 500", _ => SkillIndex.Search("Scale", matches), 1));
                Assert.GreaterOrEqual(matches.Count, 500);
                var search = Measure("warm search 500", i => SkillIndex.Search("Scale " + (i % 10), matches)); report.metrics.Add(search);
                var hits=new List<SkillContentIndex.Hit>();
                report.metrics.Add(Measure("cold global content search 500",_=>SkillContentIndex.Search("Scale",hits),1));
                var global=Measure("warm global content search 500",i=>SkillContentIndex.Search("Scale "+(i%10),hits));report.metrics.Add(global);
                Assert.Less(global.p95Milliseconds,200);
                var touched=Folder+"/Skill001.asset";var single=AssetDatabase.LoadAssetAtPath<GameplayAbilityAsset>(touched);
                IEnumerator AfterChange(string name,bool undo)
                {
                    var times=new double[20];
                    for(int i=0;i<times.Length;i++){
                        Undo.IncrementCurrentGroup();Undo.RecordObject(single,"Search invalidation");
                        string token=(undo?"UndoToken":"ImportToken")+i.ToString("D3");single.displayName=token;EditorUtility.SetDirty(single);Undo.FlushUndoRecordObjects();
                        AssetDatabase.SaveAssetIfDirty(single);AssetDatabase.ImportAsset(touched,ImportAssetOptions.ForceUpdate|ImportAssetOptions.ForceSynchronousImport);
                        yield return null; // Let the actual postprocessor deliver its batched path notification.
                        if(undo){SkillContentIndex.Search(token,hits);Assert.IsTrue(hits.Any(h=>h.Asset==single),$"Saved token {token}, actual asset value {single.displayName}, matching hits {hits.Count}.");Undo.PerformUndo();}
                        var start=Stopwatch.GetTimestamp();SkillContentIndex.Search(token,hits);
                        times[i]=(Stopwatch.GetTimestamp()-start)*1000d/Stopwatch.Frequency;
                        Assert.AreEqual(!undo,hits.Any(h=>h.Asset==single),"Search must see a saved edit and its Undo immediately.");
                        if(!undo)Undo.PerformUndo();
                    }
                    Array.Sort(times);report.metrics.Add(new Metric{name=name,p95Milliseconds=times[18],allocationCallsPerOperation=-1});
                    Assert.Less(times[18],200);
                }
                yield return AfterChange("global search after one skill reimport",false);
                yield return AfterChange("global search after one skill undo",true);
                report.metrics.Add(Measure("cold window open 100 nodes", _ => { window = SkillEditorWindow.Open(); window.Show(root); }, 1));
                for (int i = 0; i < 35; i++) yield return null;
                var graph = window.rootVisualElement.Q<SkillGraphView>(); Assert.AreEqual(100, graph.NodeCount);
                var library = window.rootVisualElement.Q<LibraryPanel>();
                var searchUi = Measure("warm library search and visible rows 500", i => library.Search("Scale " + (i % 10))); report.metrics.Add(searchUi);
                var document = window.Document;
                var field = Measure("warm scalar edit 100 nodes", i => EditCommand.Run(document, "Scale edit", first, so => so.FindProperty("duration").floatValue = i + .5f, false, "duration")); report.metrics.Add(field);
                var node = graph.nodes.ToList().First(); var bounds = node.GetPosition();
                var parent = typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
                var repaint = typeof(EditorWindow).Assembly.GetType("UnityEditor.GUIView").GetMethod("RepaintImmediately", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.IsNotNull(repaint);
                var drag = Measure("warm drag update and repaint 100 nodes", i => { var rect = bounds; rect.position += new Vector2(i % 20, i % 10); node.SetPosition(rect); repaint.Invoke(parent, null); }); report.metrics.Add(drag);
                report.metrics.Add(Measure("Undo and Redo 100 nodes", _ => { Undo.PerformUndo(); Undo.PerformRedo(); }, 10));
                window.Show(single);document=window.Document;document.Selection.Select(module);document.OpenFlow();
                using(var moduleData=new SerializedObject(module))
                    report.metrics.Add(Measure("module structure check (499 callers in library)",_=>SkillFlowCommands.ValidateOwner(moduleData,document),1));
                int revision=0;
                void FlowEdit(int _) =>EditCommand.Run(document,"修改模块动作",module,so=>{
                    so.FindProperty("actions.Array.data[0].displayName").stringValue="edit "+(++revision);
                    so.ApplyModifiedProperties();SkillFlowCommands.ValidateOwner(so,document);
                },true);
                var validation=Measure("warm flow edit with Undo and document notification (499 mixed callers)",FlowEdit);
                report.metrics.Add(validation);Assert.LessOrEqual(validation.p95Milliseconds,50,"Shared module edit budget");
                report.metrics.Add(Measure("first flow edit after Undo (499 mixed callers)",FlowEdit,20,_=>Undo.PerformUndo()));
                // Measure the explicit GAS-only reference check independently of normal edits.
                string unusedPath=Folder+"/UnusedModule.asset";var unused=ScriptableObject.CreateInstance<SkillActionModuleAsset>();
                AssetDatabase.CreateAsset(unused,unusedPath);
                report.metrics.Add(Measure("first exact reference check for unreferenced module (500 skills)",
                    _=>Assert.IsNull(SkillReferenceGuard.Find(unusedPath,new[]{unused})),1));
                report.metrics.Add(Measure("warm exact reference check for unreferenced module (500 skills)",
                    _=>Assert.IsNull(SkillReferenceGuard.Find(unusedPath,new[]{unused})),12));

                string directory = Path.Combine(Directory.GetCurrentDirectory(), "GASValidation~"); Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "skill-scale.json"), JsonUtility.ToJson(report, true));
                UnityEngine.Debug.Log(JsonUtility.ToJson(report, true));
                Assert.LessOrEqual(searchUi.p95Milliseconds, 100, "Library search budget"); Assert.LessOrEqual(search.p95Milliseconds, 100, "Search budget"); Assert.LessOrEqual(field.p95Milliseconds, 50, "Field budget"); Assert.LessOrEqual(drag.p95Milliseconds, 33, "Drag CPU budget");
            }
            finally { window?.Close(); AssetDatabase.DeleteAsset(Folder); Undo.ClearAll(); SkillIndex.Invalidate(); }
        }
    }
}
