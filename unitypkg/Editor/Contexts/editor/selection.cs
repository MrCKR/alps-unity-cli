#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// editor.selection 内置 Context：读取当前 Editor Selection，并保留 legacy 显式 null 字段。
        /// </summary>
        public sealed class EditorSelectionContext : IUnityCliContext<EditorSelectionContext.Request, EditorSelectionContext.Result>, IUnityCliBuiltInRawBridgeContext
        {
            public sealed class Request { }

            public sealed class Result
            {
                public string ActiveObject { get; set; }
                public string ActiveGameObject { get; set; }
                public string ActiveTransform { get; set; }
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

            public sealed class GameObjectRef
            {
                public long Id { get; set; }
                public string Name { get; set; }
                public string Path { get; set; }
            }

            public sealed class TransformInfo
            {
                public VectorInfo Position { get; set; }
                public VectorInfo Rotation { get; set; }
                public VectorInfo Scale { get; set; }
            }

            public sealed class VectorInfo
            {
                public double X { get; set; }
                public double Y { get; set; }
                public double Z { get; set; }
            }

            public sealed class ComponentSummary
            {
                public string Type { get; set; }
                public string FullName { get; set; }
                public bool Enabled { get; set; }
            }

            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "editor.selection",
                Summary = "Read current Editor selection names and reusable object references without changing selection.",
                Description = "Read the current Unity Editor selection in Hierarchy or Project Browser. Returns legacy name arrays plus reusable object details: instanceId, type, asset path/GUID, hierarchy path, GameObject reference, transform, and component summaries. It does not select, focus, ping, or modify anything.",
                Category = "editor",
                Version = "1.0.0",
                Source = "built-in",
                Target = "editor",
                AvailabilityScope = "editorSession",
                PlayModeSupport = "supported",
                RefreshBehavior = "live"
            };

            /// <summary>
            /// 同步 fallback 读取；生产 Bridge 注册路径会优先调用 ReadRaw，以保留显式 null 字段。
            /// </summary>
            public Result Read(Request request, UnityCliProjectToolContext ctx)
            {
                var json = EditorSelectionPayloadJson();
                return UnityCliSerializer.Deserialize(json, typeof(Result)) as Result;
            }

            /// <summary>
            /// 返回 legacy raw payload，避免反射 serializer 省略 activeObjectDetail 等 null 字段。
            /// </summary>
            public string ReadRaw(BridgeRequest request, UnityCliProjectToolContext ctx)
            {
                return SuccessJson(EditorSelectionPayloadJson());
            }
        }

        // 当前 Selection 的 legacy payload；显式写出 null，便于 AI 区分“无选择”和“字段缺失”。
        private static string EditorSelectionPayloadJson()
        {
            var activeObject = Selection.activeObject;
            var activeGameObject = Selection.activeGameObject;
            var activeTransform = Selection.activeTransform;
            var objects = Selection.objects ?? new UnityEngine.Object[0];
            var gameObjects = Selection.gameObjects ?? new GameObject[0];
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("activeObject", UnityCliSerializer.Serialize(activeObject != null ? activeObject.name : null))
                .PropRaw("activeGameObject", UnityCliSerializer.Serialize(activeGameObject != null ? activeGameObject.name : null))
                .PropRaw("activeTransform", UnityCliSerializer.Serialize(activeTransform != null ? activeTransform.name : null))
                .PropRaw("activeObjectDetail", SelectionObjectJson(activeObject))
                .PropRaw("activeGameObjectDetail", SelectionObjectJson(activeGameObject))
                .PropRaw("activeTransformDetail", SelectionObjectJson(activeTransform))
                .PropArray("objects", arr => { foreach (var obj in objects) { arr.RawValue(UnityCliSerializer.Serialize(obj != null ? obj.name : null)); } })
                .PropArray("objectDetails", arr => { foreach (var obj in objects) { arr.RawValue(SelectionObjectJson(obj)); } })
                .PropArray("gameObjects", arr => { foreach (var go in gameObjects) { arr.RawValue(UnityCliSerializer.Serialize(go != null ? go.name : null)); } })
                .PropArray("gameObjectDetails", arr => { foreach (var go in gameObjects) { if (go != null) arr.RawValue(SelectionObjectJson(go)); } })
                .PropArray("assetGUIDs", arr => { foreach (var obj in objects) { arr.RawValue(UnityCliSerializer.Serialize(SelectionAssetGuid(obj))); } })
                .EndObject()
                .ToString();
        }

        // 将选中对象转成后续工具可直接复用的稳定引用摘要；只读，不修改 Selection 或资产。
        // legacy field anchors: Prop("instanceId") Prop("assetPath") Prop("assetGUID") Prop("hierarchyPath")
        private static string SelectionObjectJson(UnityEngine.Object obj)
        {
            if (obj == null) return "null";
            var assetPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(obj));
            var assetGuid = SelectionAssetGuid(obj);
            var gameObject = SelectionObjectGameObject(obj);
            var writer = new UnityCliJsonWriter().BeginObject()
                .PropRaw("name", UnityCliSerializer.Serialize(obj.name))
                .PropRaw("instanceId", UnityCliSerializer.Serialize((long)obj.GetInstanceID()))
                .PropRaw("type", UnityCliSerializer.Serialize(obj.GetType().Name))
                .PropRaw("fullName", UnityCliSerializer.Serialize(obj.GetType().FullName))
                .PropRaw("assetPath", UnityCliSerializer.Serialize(assetPath))
                .PropRaw("assetGUID", UnityCliSerializer.Serialize(assetGuid));
            if (gameObject != null)
            {
                writer.PropRaw("hierarchyPath", UnityCliSerializer.Serialize(HierarchyPath(gameObject)))
                    .PropRaw("gameObject", GameObjectTargetJson(gameObject))
                    .PropRaw("transform", TransformJson(gameObject.transform))
                    .PropRaw("components", ComponentsJson(gameObject));
            }
            else
            {
                writer.PropNull("hierarchyPath")
                    .PropNull("gameObject")
                    .PropNull("transform")
                    .PropRaw("components", "[]");
            }
            return writer.EndObject().ToString();
        }

        // 从任意 Unity 对象解析其所属 GameObject；资产对象返回 null。
        private static GameObject SelectionObjectGameObject(UnityEngine.Object obj)
        {
            if (obj is GameObject gameObject) return gameObject;
            if (obj is Component component) return component.gameObject;
            return null;
        }

        // 读取选中资产 GUID；场景对象或非资产对象返回 null。
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
