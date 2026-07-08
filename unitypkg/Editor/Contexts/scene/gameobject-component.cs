#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// scene.gameobject.component 内置 Context：读取目标 GameObject 上的一个组件预览。
        /// </summary>
        public sealed class SceneGameObjectComponentContext : IUnityCliContext<SceneGameObjectComponentContext.Request, SceneGameObjectComponentContext.Result>, IUnityCliBuiltInRawBridgeContext
        {
            public sealed class Request
            {
                public string Target { get; set; }
                public int Id { get; set; }
                public string Path { get; set; }
                public string Component { get; set; }
            }

            public sealed class Result
            {
                public GameObjectRef GameObject { get; set; }
                public ComponentInfo Component { get; set; }
            }

            public sealed class GameObjectRef
            {
                public long Id { get; set; }
                public string Name { get; set; }
                public string Path { get; set; }
            }

            public sealed class ComponentInfo
            {
                public string Type { get; set; }
                public string FullName { get; set; }
                public bool Enabled { get; set; }
                public object Properties { get; set; }
            }

            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "scene.gameobject.component",
                Summary = "Read one component preview attached to a GameObject by component type name.",
                Description = "Lightweight preview of one component attached to a GameObject. Use scene.gameobject.components first when unsure which component name exists. Does not replace deep reads; use component.get for complete fields/props.",
                Category = "scene",
                Version = "1.0.0",
                Source = "built-in",
                Target = "editor",
                AvailabilityScope = "editorSession",
                PlayModeSupport = "supported",
                RefreshBehavior = "on-demand"
            };

            /// <summary>
            /// 同步 fallback 读取；生产 Bridge 注册路径会优先调用 ReadRaw 保留 legacy JSON shape。
            /// </summary>
            public Result Read(Request request, UnityCliProjectToolContext ctx)
            {
                var id = request.Id == 0 ? (int?)null : request.Id;
                var gameObject = ResolveSceneContextGameObject(request.Target, id, request.Path);
                if (string.IsNullOrEmpty(request.Component))
                {
                    throw new BridgeCommandException("E_COMPONENT_TYPE_REQUIRED", "scene.gameobject.component requires component.", "Pass input component with a Unity component type name.");
                }
                var component = ResolveComponent(gameObject, request.Component);
                if (component == null)
                {
                    throw new BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + request.Component, "Run context read scene.gameobject.components with the same target/path/id to list available component names.");
                }
                var json = SceneGameObjectComponentPayloadJson(gameObject, component);
                return UnityCliSerializer.Deserialize(json, typeof(Result)) as Result;
            }

            /// <summary>
            /// 返回 legacy raw payload，保持 component/name alias、错误码和输出 shape 不变。
            /// </summary>
            public string ReadRaw(BridgeRequest request, UnityCliProjectToolContext ctx)
            {
                GameObject gameObject;
                try
                {
                    gameObject = ResolveSceneContextGameObject(request);
                }
                catch (BridgeCommandException exception)
                {
                    return BridgeExceptionErrorJson(exception);
                }
                // context.read 顶层 name 是 Context 名；组件 name alias 只能从原始 input 对象读取。
                var componentName = request.StringArg("component")
                    ?? SceneContextInputStringArg(request.RawJsonArg("input"), "name");
                if (string.IsNullOrEmpty(componentName))
                {
                    return ErrorJson("E_COMPONENT_TYPE_REQUIRED", "scene.gameobject.component requires component.", "Pass --input '{\"target\":\"Player\",\"component\":\"Transform\"}'.");
                }
                Component component;
                try
                {
                    component = ResolveComponent(gameObject, componentName);
                }
                catch (BridgeCommandException exception)
                {
                    return BridgeExceptionErrorJson(exception);
                }
                if (component == null)
                {
                    return ErrorJson("E_COMPONENT_NOT_FOUND", "Component not found on target: " + componentName, "Run context read scene.gameobject.components with the same target/path/id to list available component names.");
                }
                return SuccessJson(SceneGameObjectComponentPayloadJson(gameObject, component));
            }
        }

        // 生成 scene.gameobject.component legacy payload；组件详情包含可读属性预览。
        private static string SceneGameObjectComponentPayloadJson(GameObject gameObject, Component component)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("gameObject", GameObjectTargetJson(gameObject))
                .PropRaw("component", SceneContextComponentDetailJson(component))
                .EndObject()
                .ToString();
        }
    }
}
#endif
