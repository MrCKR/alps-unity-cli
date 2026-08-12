#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>editor.selection 只读 Tool：只读当前 Editor Selection。</summary>
        public sealed class EditorSelectionTool : IUnityCliTool<EditorSelectionTool.Request, EditorSelectionTool.Result>
        {
            public sealed class Request { }

            public sealed class Result
            {
                public string ActiveObject { get; set; }
                public bool HasActiveObject { get; set; }
                public string ActiveGameObject { get; set; }
                public bool HasActiveGameObject { get; set; }
                public string ActiveTransform { get; set; }
                public bool HasActiveTransform { get; set; }
                public SelectionObjectInfo ActiveObjectDetail { get; set; }
                public SelectionObjectInfo ActiveGameObjectDetail { get; set; }
                public SelectionObjectInfo ActiveTransformDetail { get; set; }
                public List<string> Objects { get; set; }
                public List<SelectionObjectInfo> ObjectDetails { get; set; }
                public List<string> GameObjects { get; set; }
                public List<SelectionObjectInfo> GameObjectDetails { get; set; }
                public List<string> AssetGUIDs { get; set; }
            }

            public sealed class SelectionObjectInfo
            {
                public string Name { get; set; }
                public long InstanceId { get; set; }
                public string Type { get; set; }
                public string FullName { get; set; }
                public string AssetPath { get; set; }
                public string AssetGUID { get; set; }
                public string HierarchyPath { get; set; }
                public GameObjectRef GameObject { get; set; }
                public TransformInfo Transform { get; set; }
                public List<ComponentSummary> Components { get; set; }
            }

            public sealed class GameObjectRef { public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } }
            public sealed class TransformInfo { public VectorInfo Position { get; set; } public VectorInfo Rotation { get; set; } public VectorInfo Scale { get; set; } }
            public sealed class VectorInfo { public double X { get; set; } public double Y { get; set; } public double Z { get; set; } }
            public sealed class ComponentSummary { public string Type { get; set; } public string FullName { get; set; } public bool Enabled { get; set; } }

            // 返回只读 Tool 的发现元数据。
            public ToolDescriptor Describe() => new()
            {
                Name = "editor.selection", Summary = "Read current Editor selection names and reusable object references without changing selection.",
                Description = "Read the current Unity Editor selection in Hierarchy or Project Browser. Returns name arrays plus reusable object details: instanceId, type, asset path/GUID, hierarchy path, GameObject reference, transform, and component summaries. It does not select, focus, ping, or modify anything.",
                DestructiveHint = false,
                Group = "editor", Target = "editor", PlayModeSupport = "supported",
                Examples = new[] { new ToolExample { Description = "Read the current selection", Input = new Request { } } }
            };

            /// <summary>读取当前选择，不修改 Selection 或资产。</summary>
            public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
            {
                var activeObject = Selection.activeObject;
                var activeGameObject = Selection.activeGameObject;
                var activeTransform = Selection.activeTransform;
                var objects = Selection.objects ?? new UnityEngine.Object[0];
                var gameObjects = Selection.gameObjects ?? new GameObject[0];
                var result = new Result
                {
                    ActiveObject = activeObject == null ? null : activeObject.name, HasActiveObject = activeObject != null,
                    ActiveGameObject = activeGameObject == null ? null : activeGameObject.name, HasActiveGameObject = activeGameObject != null,
                    ActiveTransform = activeTransform == null ? null : activeTransform.name, HasActiveTransform = activeTransform != null,
                    ActiveObjectDetail = SelectionObjectInfoFrom(activeObject), ActiveGameObjectDetail = SelectionObjectInfoFrom(activeGameObject), ActiveTransformDetail = SelectionObjectInfoFrom(activeTransform),
                    Objects = new List<string>(), ObjectDetails = new List<SelectionObjectInfo>(), GameObjects = new List<string>(), GameObjectDetails = new List<SelectionObjectInfo>(), AssetGUIDs = new List<string>()
                };
                foreach (var obj in objects)
                {
                    if (obj == null) continue;
                    result.Objects.Add(obj.name);
                    result.ObjectDetails.Add(SelectionObjectInfoFrom(obj));
                    var assetGuid = SelectionAssetGuid(obj);
                    if (!string.IsNullOrEmpty(assetGuid)) result.AssetGUIDs.Add(assetGuid);
                }
                foreach (var gameObject in gameObjects)
                {
                    result.GameObjects.Add(gameObject == null ? null : gameObject.name);
                    if (gameObject != null) result.GameObjectDetails.Add(SelectionObjectInfoFrom(gameObject));
                }
                return new System.Threading.Tasks.ValueTask<Result>(result);
            }
        }

        // 将选中对象映射为可复用引用；仅查询对象与 AssetDatabase 元数据。
        private static EditorSelectionTool.SelectionObjectInfo SelectionObjectInfoFrom(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            var gameObject = SelectionObjectGameObject(obj);
            var info = new EditorSelectionTool.SelectionObjectInfo
            {
                Name = obj.name, InstanceId = obj.GetInstanceID(), Type = obj.GetType().Name, FullName = obj.GetType().FullName,
                AssetPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(obj)), AssetGUID = SelectionAssetGuid(obj),
                Components = new List<EditorSelectionTool.ComponentSummary>()
            };
            if (gameObject == null) return info;
            info.HierarchyPath = HierarchyPath(gameObject);
            info.GameObject = new EditorSelectionTool.GameObjectRef { Id = gameObject.GetInstanceID(), Name = gameObject.name, Path = info.HierarchyPath };
            info.Transform = SelectionTransformInfoFrom(gameObject.transform);
            foreach (var component in gameObject.GetComponents<Component>())
            {
                info.Components.Add(new EditorSelectionTool.ComponentSummary
                {
                    Type = component == null ? "MissingScript" : component.GetType().Name,
                    FullName = component == null ? null : component.GetType().FullName,
                    Enabled = component != null && ComponentEnabled(component)
                });
            }
            return info;
        }

        // 读取 Transform 世界位置、欧拉角和本地缩放。
        private static EditorSelectionTool.TransformInfo SelectionTransformInfoFrom(Transform transform)
        {
            return new EditorSelectionTool.TransformInfo { Position = SelectionVectorInfoFrom(transform.position), Rotation = SelectionVectorInfoFrom(transform.eulerAngles), Scale = SelectionVectorInfoFrom(transform.localScale) };
        }

        // 将 Vector3 转成协议 DTO。
        private static EditorSelectionTool.VectorInfo SelectionVectorInfoFrom(Vector3 value)
        {
            return new EditorSelectionTool.VectorInfo { X = value.x, Y = value.y, Z = value.z };
        }

        // 从任意 Unity 对象解析所属 GameObject；资产对象无所属对象。
        private static GameObject SelectionObjectGameObject(UnityEngine.Object obj)
        {
            if (obj is GameObject gameObject) return gameObject;
            if (obj is Component component) return component.gameObject;
            return null;
        }

        // 读取选中资产 GUID；场景对象或非资产对象无 GUID。
        private static string SelectionAssetGuid(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            var assetPath = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(assetPath)) return null;
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            return string.IsNullOrEmpty(guid) ? null : guid;
        }
    }
}
#endif
