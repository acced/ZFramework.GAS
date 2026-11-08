using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Experimental.GraphView;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ZFramework.GAS.Unity;

namespace ZFramework.GAS.Editor
{
    // Paths are transient handles for one rebuild. Layout, navigation and breakpoints use asset and action IDs.
    internal sealed class SkillFlowView : VisualElement, IDisposable
    {
        private readonly SkillDocument _doc;
        private readonly ValidationService _validation;
        private sealed class Canvas : GraphView
        {
            public override List<Port> GetCompatiblePorts(Port start, NodeAdapter adapter) => ports.ToList().Where(p=>p.direction!=start.direction && p.node!=start.node).ToList();
        }
        private sealed class FlowPort : Port, IEdgeConnectorListener
        {
            private readonly SkillFlowView _view;
            public FlowPort(Direction direction, SkillFlowView view) : base(Orientation.Horizontal, direction, Capacity.Single, typeof(bool))
            {
                _view = view;
                m_EdgeConnector = new EdgeConnector<Edge>(this);
                this.AddManipulator(m_EdgeConnector);
            }
            public void OnDrop(GraphView _, Edge edge)
            {
                if (edge.output?.userData is ValueTuple<string, int> dest && edge.input?.node is FlowNode src)
                    _view.TryEdit(() => SkillFlowCommands.Move(_view._doc, _view._owner, src.Path, dest.Item1, dest.Item2));
            }
            public void OnDropOutsidePort(Edge edge, Vector2 position)
            {
                if (edge.output?.userData is not ValueTuple<string, int> dest) return;
                var owner = _view._owner;
                var entry = _view._layoutKey;
                SkillActionSearch.Open(GUIUtility.GUIToScreenPoint(position), type => {
                    if (owner == _view._owner && entry == _view._layoutKey) _view.Insert(dest.Item1, type, dest.Item2);
                });
            }
        }
        private readonly Canvas _graph = new Canvas();
        private readonly ToolbarMenu _entries = new ToolbarMenu { text = "入口" };
        private readonly Label _crumb = new Label();
        private readonly Stack<(ScriptableObject owner, string marker)> _parents = new();
        private ScriptableObject _owner;
        private string _marker, _layoutKey, _rootPath, _selected;
        private GameplayAbilityAsset _root;
        private readonly Dictionary<string, Node> _nodes = new();
        private readonly List<(string ownerPath,string field,bool detached)> _branchModes = new();
        private Node _entryNode;
        private readonly Dictionary<string, float> _liveStrength = new();
        public SkillFlowView(SkillDocument doc, ValidationService validation)
        {
            _doc=doc; _validation=validation; style.flexGrow=1;
            var bar=new Toolbar(); Add(bar); bar.Add(_entries);
            bar.Add(new ToolbarButton(Back){text="上一级"}); bar.Add(_crumb);
            bar.Add(new ToolbarButton(() => {
                if (_rootPath == null) return;
                _doc.Layout.ResetFlowPositions(_layoutKey); _doc.Layout.Save(); Rebuild();
                _graph.schedule.Execute(() => _graph.FrameAll());
            }) { text="自动整理" });
            bar.Add(new ToolbarButton(()=>_graph.FrameAll()){text="显示全部"}); Add(_graph); _graph.style.flexGrow=1;
            _graph.SetupZoom(.15f,1.5f); _graph.AddManipulator(new ContentDragger()); _graph.AddManipulator(new SelectionDragger()); _graph.AddManipulator(new RectangleSelector());
            _graph.graphViewChanged = change =>
            {
                if (change.elementsToRemove!=null) change.elementsToRemove.Clear();
                if (change.movedElements != null) foreach (var el in change.movedElements)
                    if (el is Node n && n.userData is string id) _doc.Layout.SetFlowPosition(_layoutKey,id,n.GetPosition().position);
                if(change.movedElements?.Count>0)_doc.Layout.Save();
                return change;
            };
            _graph.RegisterCallback<AttachToPanelEvent>(_=>SkillEventRecorder.Acquire());
            _graph.RegisterCallback<DetachFromPanelEvent>(_=>SkillEventRecorder.Release());
            _graph.RegisterCallback<KeyDownEvent>(e=>
            {
                if (e.keyCode==KeyCode.Space && _owner!=null && _rootPath!=null) { SkillActionSearch.Open(GUIUtility.GUIToScreenPoint(_graph.worldBound.center),t=>Insert(_rootPath,t)); e.StopPropagation(); }
            });
            RuntimeDebugSession.Changed+=Live;
            _doc.Changed+=Changed; _validation.Changed+=RefreshIssues;
        }
        public void OpenSelection()
        {
            _parents.Clear(); _root=_doc.Root;
            _owner=_doc.Selection.Asset as ScriptableObject; _marker=null;
            if (_owner==null) _owner=_root;
            if (SkillEditorExtensions.Supports(_owner))
            {
                if (_doc.Selection.Track == SkillEditorExtensions.Timeline.EntryTrack) _marker = _doc.Selection.ElementId;
                else if (_doc.Selection.Track == "@field")
                    foreach (var entry in SkillEditorExtensions.Entries(_doc.Serialized(_owner)))
                        if ((_doc.Selection.ElementId + ".").StartsWith(entry.PropertyPath + ".", StringComparison.Ordinal)) _marker = entry.Id;
                if (_marker == null) _owner = _root;
            }
            else if (_owner is not SkillActionModuleAsset) _owner = _root;
            Rebuild();
            if (_doc.Selection.Track=="@field") foreach(var n in _nodes.Values)
                if (n is FlowNode f && _doc.Selection.ElementId==f.Path) { _graph.AddToSelection(n); _selected=(string)n.userData; }
        }
        public void FocusSelection(){if(_graph.selection.Count>0)_graph.FrameSelection();else _graph.FrameAll();}
        public void ToggleBreakpoint(){if(_selected!=null){SkillActionDebugger.Toggle(_selected);Rebuild();}}
        public void DuplicateSelection()
        {
            if(_selected==null || !_nodes.TryGetValue(_selected,out var node) || node is not FlowNode f)return;
            string path=f.Path; int split=path.LastIndexOf(".Array.data[",StringComparison.Ordinal);string list=path.Substring(0,split);
            Edit("复制动作",so=>SkillActionClipboard.Duplicate(so.FindProperty(list),SkillFlowCommands.IndexIn(list,path)+1,
                (SkillAction)so.FindProperty(path).managedReferenceValue));
        }
        private void Changed(ChangeSet change)
        {
            if (style.display.value==DisplayStyle.None) return;
            if (_root!=_doc.Root) { OpenSelection(); return; }
            if (_owner==null || !_doc.Assets.Contains(_owner))
            {
                while (_parents.Count>0) { (_owner,_marker)=_parents.Pop(); if (_owner!=null && _doc.Assets.Contains(_owner)) break; }
                if (_owner==null || !_doc.Assets.Contains(_owner)) { _owner=_root; _marker=null; }
                Rebuild(); return;
            }
            if(change.Structure){Rebuild();return;}
            if(change.Asset == SkillEditorExtensions.Asset(_doc.Root))
            {
                RefreshEntries(out string entry);
                if(_entryNode?.parent!=null){_entryNode.title=entry;_crumb.text=Breadcrumb(entry);}
            }
            if (change.Asset != _owner) return;
            if (change.Paths.Any(p=>p.EndsWith(".displayName"))) { Rebuild(); return; }
            foreach (string path in change.Paths)
            foreach (var branch in _branchModes)
            {
                if (!path.StartsWith(branch.ownerPath,StringComparison.Ordinal)) continue;
                var action = _doc.Serialized(_owner).FindProperty(branch.ownerPath)?.managedReferenceValue as SkillAction;
                if (action != null && action.IsDetachedBranch(branch.field) != branch.detached) { Rebuild(); return; }
            }
        }
        private void RefreshEntries(out string entry)
        {
            _entries.menu.MenuItems().Clear();
            _entries.menu.AppendAction("激活动作",_=>Choose(_doc.Root,null));
            var presentation = SkillEditorExtensions.Asset(_doc.Root);
            entry = "激活动作";
            foreach (var item in SkillEditorExtensions.Entries(_doc.Serialized(presentation)))
            {
                _entries.menu.AppendAction(item.Label, _ => Choose(presentation, item.Id));
                if (_owner == presentation && item.Id == _marker) { _rootPath = item.Actions.propertyPath; entry = item.Label; }
            }
            if (_owner is SkillActionModuleAsset) { _rootPath="actions"; entry=_owner.name; }
            else if (_rootPath==null) { _owner=_doc.Root; _rootPath="activationActions"; }
        }
        private string Breadcrumb(string entry)=>_doc.Root.name+" › "+string.Join(" › ",_parents.Reverse().Where(p=>p.owner!=null).Select(p=>p.owner.name).Append(entry));
        private void Back()
        {
            if (_parents.Count==0) { Choose(_doc.Root,null); return; }
            (_owner,_marker)=_parents.Pop(); Rebuild();
        }
        private void Choose(ScriptableObject owner,string marker)
        { _parents.Clear(); _owner=owner; _marker=marker; _selected=null; Rebuild(); }
        private void Rebuild()
        {
            _graph.ClearSelection();
            foreach(var e in _graph.graphElements.ToList()) _graph.RemoveElement(e);
            _nodes.Clear(); _branchModes.Clear(); _entries.menu.MenuItems().Clear(); _rootPath=null; _crumb.text="";
            if (_doc.Root==null) return;
            RefreshEntries(out string entry);
            _layoutKey=AssetReferences.Key(_owner)+"#"+(_marker ?? _rootPath);
            _crumb.text=Breadcrumb(entry);
            var root=_entryNode=new Node{title=entry,userData="$entry"}; AddNode(root,"$entry",0,0);
            int lane=0;
            void Walk(SerializedProperty list,Node previous, string branch,int col,int row, int depth,bool detached=false)
            {
                if (depth>8 || list==null) return;
                Port output=Output(previous,detached ? branch+" · 独立回调" : branch,list.propertyPath,0);
                for(int i=0;i<list.arraySize;i++)
                {
                    var p=list.GetArrayElementAtIndex(i); var action=p.managedReferenceValue as SkillAction;
                    var node=new FlowNode { Path=p.propertyPath, userData=action?.Id, title=ActionTitle(action) };
                    var input=new FlowPort(Direction.Input,this); input.portName="执行"; node.inputContainer.Add(input);
                    string path=p.propertyPath, listPath=list.propertyPath; int index=i;
                    node.RegisterCallback<PointerDownEvent>(e=>
                    {
                        _selected=action?.Id; _doc.Selection.Select(_owner,"@field",path);
                        if(e.clickCount==2 && action is CallModuleAction call && call.module!=null && _parents.Count<8)
                        { _parents.Push((_owner,_marker)); _owner=call.module; _marker=null; Rebuild(); e.StopPropagation(); }
                    });
                    node.AddManipulator(new ContextualMenuManipulator(e=>
                    {
                        if(action!=null)e.menu.AppendAction("收藏 / 取消收藏此类型",_=>SkillActionSearch.Favorite(action.GetType()));
                        e.menu.AppendAction("切换断点 (F9)",_=>{SkillActionDebugger.Toggle(action?.Id); Rebuild();});
                        e.menu.AppendAction("复制到剪贴板",_=>SkillActionClipboard.Copy(action));
                        e.menu.AppendAction("粘贴到后面",_=>Edit("粘贴动作",so=>SkillActionClipboard.Paste(so.FindProperty(listPath),index+1)),_=>SkillActionClipboard.Available?DropdownMenuAction.Status.Normal:DropdownMenuAction.Status.Disabled);
                        e.menu.AppendAction("上移",_=>Edit("上移动作",so=>so.FindProperty(listPath).MoveArrayElement(index,index-1)),_=>index>0?DropdownMenuAction.Status.Normal:DropdownMenuAction.Status.Disabled);
                        e.menu.AppendAction("下移",_=>Edit("下移动作",so=>so.FindProperty(listPath).MoveArrayElement(index,index+1)),_=>index+1<_doc.Serialized(_owner).FindProperty(listPath).arraySize?DropdownMenuAction.Status.Normal:DropdownMenuAction.Status.Disabled);
                        e.menu.AppendAction("提取为共享模块…",_=>Extract(listPath,index));
                        if(action is CallModuleAction) e.menu.AppendAction("内联模块",_=>TryEdit(()=>SkillFlowCommands.Inline(_doc,_owner,listPath,index)));
                        e.menu.AppendAction("删除",_=>Edit("删除动作",so=>so.FindProperty(listPath).DeleteArrayElementAtIndex(index)));
                    }));
                    string id=action?.Id ?? path; AddNode(node,id,col,row); if(action!=null) _nodes[id]=node;
                    var edge=output.ConnectTo(input);_graph.AddElement(edge);
                    if(detached && i==0)MarkDetached(edge);
                    var fields = action == null ? null : SkillActionFields.For(action.GetType());
                    bool useCurrentRow=i==list.arraySize-1;
                    if (fields != null) foreach(var field in fields.Branches)
                    {
                        var children=p.FindPropertyRelative(field.Name); if(children==null) continue;
                        string label=field.GetCustomAttribute<FlowBranchAttribute>()?.Label??field.Name;
                        bool childDetached=action.IsDetachedBranch(field.Name);
                        _branchModes.Add((path,field.Name,childDetached));
                        Walk(children,node,label,col+1,useCurrentRow?row:++lane,depth+1,childDetached);useCurrentRow=false;
                    }
                    if (fields != null) AddInlineFields(node,p,fields);
                    if(action!=null)
                    {
                        var mark=new Button(()=>{SkillActionDebugger.Toggle(id);Rebuild();}){text=SkillActionDebugger.Has(id)?"●":"○",tooltip="切换动作断点"};
                        mark.AddToClassList("flow-breakpoint");node.titleContainer.Insert(0,mark);
                    }
                    output=Output(node,"然后",listPath,index+1); col++; previous=node;
                    node.RefreshExpandedState(); node.RefreshPorts();
                }
            }
            Walk(_doc.Serialized(_owner).FindProperty(_rootPath),root,"执行",1,0,0);
            root.RefreshExpandedState(); root.RefreshPorts(); RefreshIssues();
            if(_selected!=null && _nodes.TryGetValue(_selected,out var selected))_graph.AddToSelection(selected);
            _doc.Layout.PruneFlow(_layoutKey,_nodes.Keys);
        }
        private sealed class FlowNode:Node { public string Path; }
        internal static string ActionTitle(SkillAction action) => action == null ? "未配置动作" :
            string.IsNullOrEmpty(action.displayName) ? SkillActionFields.For(action.GetType()).Title : action.displayName;
        private static void MarkDetached(Edge edge)
        {
            edge.userData="detached";edge.tooltip="独立回调：不允许等待或技能归属任务";
            var control=edge.edgeControl;control.inputColor=control.outputColor=Color.clear;
            control.generateVisualContent+=ctx=>{
                if(edge.input?.panel==null || edge.output?.panel==null)return;
                var a=control.WorldToLocal(edge.output.Q("connector").worldBound.center);
                var b=control.WorldToLocal(edge.input.Q("connector").worldBound.center);
                float dx=Mathf.Max(35,Mathf.Abs(b.x-a.x)*.5f);
                Vector2 At(float t){float u=1-t;return a*(u*u*u)+(a+Vector2.right*dx)*(3*u*u*t)+(b-Vector2.right*dx)*(3*u*t*t)+b*(t*t*t);}
                var p=ctx.painter2D;p.strokeColor=edge.ClassListContains("flow-path")?edge.resolvedStyle.color:Color.gray;p.lineWidth=2;p.BeginPath();
                int count=Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(a,b)/8),4,120);
                for(int i=0;i<count;i+=2){p.MoveTo(At((float)i/count));p.LineTo(At((i+1f)/count));}p.Stroke();
            };
        }
        private void AddInlineFields(Node node,SerializedProperty item,SkillActionFields fields)
        {
            var owner=_owner;
            foreach (var (path, label) in fields.Inline)
            {
                var p=item.FindPropertyRelative(path); if(p==null)continue;
                var f=new PropertyField(p,label);f.AddToClassList("flow-field");node.extensionContainer.Add(f);f.BindProperty(p);
                f.RegisterCallback<SerializedPropertyChangeEvent>(e=>{
                    string changed=e.changedProperty.propertyPath;
                    schedule.Execute(()=>{if(owner!=null)_doc.NotifySerializedChange(owner,changed);});
                });
            }
        }
        private void AddNode(Node node,string id,int col,int row)
        {
            node.AddToClassList("skill-flow-node");
            node.capabilities &= ~(Capabilities.Deletable|Capabilities.Copiable);
            node.style.width=242; node.SetPosition(new Rect(_doc.Layout.FlowPosition(_layoutKey,id,new Vector2(20+270*col,20+row*180)),new Vector2(208,88)));
            _graph.AddElement(node);
        }
        private Port Output(Node node,string name,string list,int index=-1)
        {
            var port=new FlowPort(Direction.Output,this);
            port.userData=(list,index); port.portName=name; node.outputContainer.Add(port);
            port.AddManipulator(new ContextualMenuManipulator(e=> {foreach(var type in SkillActionSearch.Types) e.menu.AppendAction("添加/"+SkillActionSearch.MenuName(type),_=>Insert(list,type,index));}));
            return port;
        }
        private void Insert(string path, Type type,int index=-1) => Edit("添加动作",so=>
        {
            var list=so.FindProperty(path); var n=list.arraySize++; list.GetArrayElementAtIndex(n).managedReferenceValue=Activator.CreateInstance(type);
            if (index>=0 && index<n) list.MoveArrayElement(n,index);
        });
        private void Edit(string name,Action<SerializedObject> edit) => TryEdit(()=>EditCommand.Run(_doc,name,_owner,so=>{edit(so); so.ApplyModifiedProperties(); SkillFlowCommands.ValidateOwner(so,_doc);},true));
        private void TryEdit(Action command) {try {command();} catch(OperationCanceledException){} catch(Exception e){_doc.Report(e.Message);} }
        private void Extract(string list,int fallback)
        {
            var paths=_graph.selection.OfType<FlowNode>().Select(n=>n.Path).ToArray();
            int[] indices=paths.Length==0?new[]{fallback}:paths.Select(p=>SkillFlowCommands.IndexIn(list,p)).OrderBy(i=>i).ToArray();
            if(indices[0]<0 || indices.Last()-indices[0]+1!=indices.Length){_doc.Report("请选择同一分支内连续的动作。");return;}
            var path=EditorUtility.SaveFilePanelInProject("提取为模块","动作模块","asset","选择独立模块的位置");
            if(!string.IsNullOrEmpty(path)) TryEdit(()=>SkillFlowCommands.Extract(_doc,_owner,list,indices[0],indices.Length,path,ActionEffectOwnership.Confirm));
        }
        private void Live(RuntimeSnapshot snapshot)
        {
            if(style.display.value==DisplayStyle.None) return;
            var history=SkillEventRecorder.Of(snapshot?.Owner);
            _liveStrength.Clear(); int? cast=null;
            var now = EditorApplication.timeSinceStartup;
            if(history!=null && EditorApplication.isPlaying) for(int i=history.Count-1;i>=0;i--)
            {
                var ev=history[i]; if(!EditorApplication.isPaused && now-ev.Time>=2) break;
                if(ev.Skill!=_doc.Root || ev.Kind!=TraceKind.动作开始) continue;
                if(cast.HasValue && cast!=ev.Cast) break;
                cast=ev.Cast;
                float weight = EditorApplication.isPaused ? 1f : Mathf.Clamp01((float)(2 - (now - ev.Time)));
                if (ev.RawId != null) _liveStrength.TryAdd(ev.RawId, weight);
                if (ev.NavigationId != null) _liveStrength.TryAdd(ev.NavigationId, weight);
            }
            var active = new Color(.4f,.85f,.56f);
            foreach(var pair in _nodes)
            {
                bool live = _liveStrength.TryGetValue(pair.Key, out float weight);
                pair.Value.EnableInClassList("flow-live",live);
                pair.Value.style.borderTopColor = live ? new StyleColor(new Color(active.r,active.g,active.b,weight)) : new StyleColor(StyleKeyword.Null);
            }
            if(SkillActionDebugger.Frame is { } frame && EditorApplication.isPaused && frame.Owner==snapshot?.Owner &&
                frame.Asset==_doc.Root && _nodes.TryGetValue(frame.RawId,out var paused))paused.tooltip=frame.Summary;
            foreach(var edge in _graph.edges)
            {
                if (edge.input?.node?.userData is not string target || edge.output?.node?.userData is not string from) continue;
                bool live=_liveStrength.TryGetValue(target,out float weight) && (_liveStrength.ContainsKey(from) || from=="$entry");
                edge.EnableInClassList("flow-path",live);
                var color = live ? Color.Lerp(Color.gray,active,weight) : Color.gray;
                edge.style.color = color;
                edge.edgeControl.inputColor=edge.edgeControl.outputColor=edge.userData as string=="detached"?Color.clear:color;
                edge.edgeControl.MarkDirtyRepaint();
            }
        }
        private void RefreshIssues()
        {
            foreach(var n in _nodes.Values.OfType<FlowNode>())
            {
                var issue=_validation.Issues.FirstOrDefault(i=>i.Asset==_owner && (i.PropertyPath+".").StartsWith(n.Path+"."));
                n.tooltip=issue?.Message??n.Path; n.EnableInClassList("flow-error",issue!=null);
            }
        }
        public void Dispose() { RuntimeDebugSession.Changed-=Live; _doc.Changed-=Changed; _validation.Changed-=RefreshIssues; }
    }
}
