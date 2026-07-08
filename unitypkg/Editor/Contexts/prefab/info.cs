#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// prefab.info 内置 Context：读取 Prefab 资产摘要、依赖、变体和嵌套 Prefab 引用。
        /// </summary>
        public sealed class PrefabInfoContext : IUnityCliContext<PrefabInfoContext.Request, PrefabInfoContext.Result>, IUnityCliBuiltInRawBridgeContext
        {
            public sealed class Request
            {
                [UnityCliRequired]
                public string Path { get; set; }
            }

            public sealed class Result
            {
                public PrefabSummary Prefab { get; set; }
            }

            public sealed class PrefabSummary
            {
                public string Path { get; set; }
                public string Guid { get; set; }
                public string RootName { get; set; }
                public string AssetType { get; set; }
                public string InstanceStatus { get; set; }
                public bool IsVariant { get; set; }
                public PrefabVariantInfo Variant { get; set; }
                public long DependencyCount { get; set; }
                public SummaryList<DependencyInfo> Dependencies { get; set; }
                public SummaryList<NestedPrefabInfo> NestedPrefabs { get; set; }
            }

            public sealed class PrefabVariantInfo
            {
                public bool IsVariant { get; set; }
                public string BasePath { get; set; }
                public string BaseGuid { get; set; }
                public string SourcePath { get; set; }
                public string SourceGuid { get; set; }
            }

            public sealed class SummaryList<T>
            {
                public long Count { get; set; }
                public bool ItemsIncluded { get; set; }
                public List<T> Items { get; set; }
            }

            public sealed class DependencyInfo
            {
                public string Path { get; set; }
                public string Guid { get; set; }
                public string Type { get; set; }
            }

            public sealed class NestedPrefabInfo
            {
                public string Name { get; set; }
                public string Path { get; set; }
                public string SourcePath { get; set; }
                public string SourceGuid { get; set; }
                public string AssetType { get; set; }
                public string InstanceStatus { get; set; }
            }

            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "prefab.info",
                Summary = "Read Prefab asset metadata, dependencies, variant data, and nested Prefab references.",
                Description = "Read-only Prefab asset metadata for one Assets/*.prefab path: GUID, root name, Prefab asset type, instance status, variant source, dependency summary, and nested Prefab references. Does not open Prefab Stage, save assets, or modify the Prefab.",
                Category = "prefab",
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
                var path = RequirePrefabPath(request == null ? null : request.Path);
                var prefab = LoadPrefabAsset(path);
                var payload = PrefabInfoPayloadJson(path, prefab);
                return UnityCliSerializer.Deserialize(payload, typeof(Result)) as Result;
            }

            /// <summary>
            /// 返回 legacy raw payload，并透传 Prefab path/asset 校验的结构化错误。
            /// </summary>
            public string ReadRaw(BridgeRequest request, UnityCliProjectToolContext ctx)
            {
                try
                {
                    var path = RequirePrefabPath(request.StringArg("path"));
                    var prefab = LoadPrefabAsset(path);
                    return SuccessJson(PrefabInfoPayloadJson(path, prefab));
                }
                catch (BridgeCommandException exception)
                {
                    return BridgeExceptionErrorJson(exception);
                }
            }
        }

        // 生成 prefab.info legacy payload；PrefabAssetJson 内部读取 dependencies/variant/nestedPrefabs。
        private static string PrefabInfoPayloadJson(string path, GameObject prefab)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("prefab", PrefabAssetJson(path, prefab))
                .EndObject()
                .ToString();
        }
    }
}
#endif
