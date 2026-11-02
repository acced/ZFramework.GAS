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
        private static Metric Measure(string name, Action<int> action, int count = 40)
        {
            var samples = new double[count]; long allocation = 0;
            bool canMeasure = AllocationMeter.Works();
            for (int i = 0; i < count; i++)
            {
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
                GameplayEffectAsset first = null;
                AssetDatabase.StartAssetEditing();
                try
                {
                    for (int i = 1; i < 500; i++)
                    {
                        var skill = ScriptableObject.CreateInstance<GameplayAbilityAsset>(); skill.displayName = "Scale " + i;
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
                string directory = Path.Combine(Directory.GetCurrentDirectory(), "GASValidation~"); Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "skill-scale.json"), JsonUtility.ToJson(report, true));
                UnityEngine.Debug.Log(JsonUtility.ToJson(report, true));
                Assert.LessOrEqual(searchUi.p95Milliseconds, 100, "Library search budget"); Assert.LessOrEqual(search.p95Milliseconds, 100, "Search budget"); Assert.LessOrEqual(field.p95Milliseconds, 50, "Field budget"); Assert.LessOrEqual(drag.p95Milliseconds, 33, "Drag CPU budget");
            }
            finally { window?.Close(); AssetDatabase.DeleteAsset(Folder); Undo.ClearAll(); SkillIndex.Invalidate(); }
        }
    }
}
