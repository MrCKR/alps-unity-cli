#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// scene.gameobject.components 内置 Context：读取目标 GameObject 的组件列表。
        /// </summary>
        public sealed class SceneGameObjectComponentsContext : IUnityCliContext<SceneGameObjectComponentsContext.Request, SceneGameObjectComponentsContext.Result>, IUnityCliBuiltInRawBridgeContext
        {
            public sealed class Request
            {
                public string Target { get; set; }
                public int Id { get; set; }
                public string Path { get; set; }
                public bool IncludeProperties { get; set; } = true;
            }

            public sealed class Result
            {
                public GameObjectRef GameObject { get; set; }
                public List<ComponentInfo> Components { get; set; }
                public long Count { get; set; }
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
                Name = "scene.gameobject.components",
                Summary = "List components attached to one GameObject, optionally including readable property previews.",
                Description = "Lightweight list of components attached to a GameObject: type names, enabled state, and optional readable property preview. Use this before component.get/component.modify to choose component name or index. Does not return every serialized field; use component.get for deep component data.",
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
                var json = SceneGameObjectComponentsPayloadJson(gameObject, request.IncludeProperties);
                return UnityCliSerializer.Deserialize(json, typeof(Result)) as Result;
            }

            /// <summary>
            /// 返回 legacy raw payload，保持 includeProperties 与目标解析语义不变。
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
                var includeProperties = request.BoolArg("includeProperties", true);
                return SuccessJson(SceneGameObjectComponentsPayloadJson(gameObject, includeProperties));
            }
        }

        // 生成 scene.gameobject.components legacy payload；includeProperties=false 时只返回组件摘要。
        private static string SceneGameObjectComponentsPayloadJson(GameObject gameObject, bool includeProperties)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("gameObject", GameObjectTargetJson(gameObject))
                .PropRaw("components", includeProperties ? SceneContextComponentDetailsJson(gameObject) : ComponentsJson(gameObject))
                .Prop("count", (long)gameObject.GetComponents<Component>().Length)
                .EndObject()
                .ToString();
        }
    }
}
#endif
