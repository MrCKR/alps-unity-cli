#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// scene.volumes 内置 Context：读取当前场景中的 Volume 组件摘要。
        /// </summary>
        public sealed class SceneVolumesContext : IUnityCliContext<SceneVolumesContext.Request, SceneVolumesContext.Result>, IUnityCliBuiltInRawBridgeContext
        {
            public sealed class Request
            {
                public string Target { get; set; }
                public int PageSize { get; set; }
                public string Cursor { get; set; }
            }

            public sealed class Result
            {
                public List<VolumeInfo> Volumes { get; set; }
                public long Count { get; set; }
                public PageInfo PageInfo { get; set; }
            }

            public sealed class VolumeInfo
            {
                public GameObjectRef Target { get; set; }
                public bool Enabled { get; set; }
                public bool IsGlobal { get; set; }
                public double Priority { get; set; }
                public double Weight { get; set; }
                public AssetRef Profile { get; set; }
            }

            public sealed class GameObjectRef
            {
                public long Id { get; set; }
                public string Name { get; set; }
                public string Path { get; set; }
            }

            public sealed class AssetRef
            {
                public string Name { get; set; }
                public string Path { get; set; }
                public string Guid { get; set; }
            }

            public sealed class PageInfo
            {
                public bool HasMore { get; set; }
                public long PageSize { get; set; }
            }

            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "scene.volumes",
                Summary = "List Volume components in the active scene with target object, profile reference, and pagination metadata.",
                Description = "Read Volume components in the active scene with target GameObject reference, enabled state, global flag, priority, weight, profile reference, count, and pageInfo. Optional target filters by GameObject name or hierarchy path. Returns an empty list when UnityEngine.Rendering.Volume is unavailable. This context is read-only and does not modify render settings or Volume profiles.",
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
                var pageSize = request == null || request.PageSize == 0 ? 100 : request.PageSize;
                var volumesJson = GraphicsVolumesJson(request == null ? null : request.Target);
                var payload = SceneVolumesPayloadJson(volumesJson, pageSize);
                return UnityCliSerializer.Deserialize(payload, typeof(Result)) as Result;
            }

            /// <summary>
            /// 返回 legacy raw payload，保持 volumes/count/pageInfo 输出 shape 与 target/pageSize 输入语义。
            /// </summary>
            public string ReadRaw(BridgeRequest request, UnityCliProjectToolContext ctx)
            {
                var target = request.StringArg("target");
                var pageSize = request.IntArg("pageSize", 100);
                var volumesJson = GraphicsVolumesJson(target);
                return SuccessJson(SceneVolumesPayloadJson(volumesJson, pageSize));
            }
        }

        // 包装 GraphicsVolumesJson 的 legacy 数组输出，并附加 count/pageInfo。
        // legacy shape anchor: PropRaw("volumes") + PropObject("pageInfo")
        private static string SceneVolumesPayloadJson(string volumesJson, int pageSize)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("volumes", volumesJson)
                .PropRaw("count", UnityCliSerializer.Serialize((long)CountTopLevelJsonArrayItems(volumesJson)))
                .PropObject("pageInfo", obj => obj
                    .PropRaw("hasMore", UnityCliSerializer.Serialize(false))
                    .PropRaw("pageSize", UnityCliSerializer.Serialize((long)pageSize)))
                .EndObject()
                .ToString();
        }
    }
}
#endif
