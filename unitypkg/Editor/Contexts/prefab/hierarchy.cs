#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// prefab.hierarchy 内置 Context：分页读取 Prefab 资产层级，不打开或修改 Prefab Stage。
        /// </summary>
        public sealed class PrefabHierarchyContext : IUnityCliContext<PrefabHierarchyContext.Request, PrefabHierarchyContext.Result>, IUnityCliBuiltInRawBridgeContext
        {
            public sealed class Request
            {
                [UnityCliRequired]
                public string Path { get; set; }
                public int PageSize { get; set; }
                public int Cursor { get; set; }
            }

            public sealed class Result
            {
                public PrefabInfoContext.PrefabSummary Prefab { get; set; }
                public List<HierarchyItem> Items { get; set; }
                public PageInfo PageInfo { get; set; }
            }

            public sealed class HierarchyItem
            {
                public long Id { get; set; }
                public string Name { get; set; }
                public string Path { get; set; }
                public bool Active { get; set; }
                public bool ActiveInHierarchy { get; set; }
                public string Tag { get; set; }
                public long Layer { get; set; }
                public long ChildCount { get; set; }
                public bool ChildrenTruncated { get; set; }
                public long ComponentCount { get; set; }
                public List<string> ComponentTypes { get; set; }
                public List<string> Components { get; set; }
            }

            public sealed class PageInfo
            {
                public long PageSize { get; set; }
                public long Cursor { get; set; }
                public string NextCursor { get; set; }
                public long Total { get; set; }
            }

            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "prefab.hierarchy",
                Summary = "Read a paged Prefab asset hierarchy without opening or modifying Prefab Stage.",
                Description = "Read-only paged hierarchy for one Assets/*.prefab path. Loads the Prefab contents only for inspection, returns the Prefab summary, hierarchy item page, and pageInfo cursor data, then unloads the temporary contents. Does not open Prefab Stage, save assets, or modify the Prefab.",
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
                LoadPrefabAsset(path);
                var pageSize = Math.Max(1, request == null ? 100 : request.PageSize == 0 ? 100 : request.PageSize);
                var cursor = Math.Max(0, request == null ? 0 : request.Cursor);
                var payload = PrefabHierarchyPayloadJson(path, pageSize, cursor);
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
                    LoadPrefabAsset(path);
                    var pageSize = Math.Max(1, request.IntArg("pageSize", 100));
                    var cursor = Math.Max(0, request.IntArg("cursor", 0));
                    return SuccessJson(PrefabHierarchyPayloadJson(path, pageSize, cursor));
                }
                catch (BridgeCommandException exception)
                {
                    return BridgeExceptionErrorJson(exception);
                }
            }
        }

        // 临时加载 Prefab contents 读取 hierarchy；finally 保证卸载，避免污染 Prefab Stage。
        private static string PrefabHierarchyPayloadJson(string path, int pageSize, int cursor)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var items = new List<string>();
                var total = CountHierarchy(root.transform);
                CollectHierarchy(root.transform, items, cursor, pageSize, new Counter());
                var nextCursor = cursor + items.Count < total ? (cursor + items.Count).ToString(CultureInfo.InvariantCulture) : null;
                return new UnityCliJsonWriter().BeginObject()
                    .PropRaw("prefab", PrefabAssetJson(path, root, root))
                    .PropArray("items", arr => { foreach (var item in items) { arr.RawValue(item); } })
                    .PropObject("pageInfo", pageInfo => pageInfo
                        .Prop("pageSize", (long)pageSize)
                        .Prop("cursor", (long)cursor)
                        .Prop("nextCursor", nextCursor)
                        .Prop("total", (long)total))
                    .EndObject()
                    .ToString();
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
#endif
