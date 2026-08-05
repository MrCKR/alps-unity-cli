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
        /// <summary>prefab.hierarchy 内置 Context：分页只读 Prefab 资产层级。</summary>
        public sealed class PrefabHierarchyContext : IUnityCliContext<PrefabHierarchyContext.Request, PrefabHierarchyContext.Result>
        {
            public sealed class Request { [UnityCliRequired] public string Path { get; set; } public int PageSize { get; set; } public int Cursor { get; set; } }
            public sealed class Result { public PrefabInfoContext.PrefabSummary Prefab { get; set; } public List<HierarchyItem> Items { get; set; } public PageInfo PageInfo { get; set; } }
            public sealed class HierarchyItem
            {
                public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } public bool Active { get; set; } public bool ActiveInHierarchy { get; set; }
                public string Tag { get; set; } public long Layer { get; set; } public long ChildCount { get; set; } public bool ChildrenTruncated { get; set; }
                public long ComponentCount { get; set; } public List<string> ComponentTypes { get; set; } public List<string> Components { get; set; }
            }
            public sealed class PageInfo { public long PageSize { get; set; } public long Cursor { get; set; } public bool HasNextCursor { get; set; } public long? NextCursor { get; set; } public long Total { get; set; } }

            // 返回 Context 的发现元数据。
            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "prefab.hierarchy", Summary = "Read a paged Prefab asset hierarchy without opening or modifying Prefab Stage.",
                Description = "Read-only paged hierarchy for one Assets/*.prefab path. Loads the Prefab contents only for inspection, returns the Prefab summary, hierarchy item page, and pageInfo cursor data, then unloads the temporary contents. Does not open Prefab Stage, save assets, or modify the Prefab.",
                Category = "prefab",Source = "built-in", Target = "editor", AvailabilityScope = "editorSession", PlayModeSupport = "supported",
                Examples = new[] { new ContextExample { Description = "Read the first Prefab hierarchy page", Input = new Request { Path = "Assets/Prefabs/Player.prefab", PageSize = 50 } }, new ContextExample { Description = "Read a later Prefab hierarchy page", Input = new Request { Path = "Assets/Prefabs/Player.prefab", PageSize = 50, Cursor = 50 } } }
            };

            /// <summary>临时加载 Prefab contents 读取层级，并保证卸载。</summary>
            public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
            {
                var path = RequirePrefabPath(request == null ? null : request.Path);
                LoadPrefabAsset(path);
                var pageSize = Math.Max(1, request == null || request.PageSize == 0 ? 100 : request.PageSize);
                var cursor = Math.Max(0, request == null ? 0 : request.Cursor);
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var items = new List<HierarchyItem>();
                    var total = CountPrefabHierarchy(root.transform);
                    var visited = 0;
                    CollectPrefabHierarchy(root.transform, items, cursor, pageSize, ref visited);
                    var hasNext = cursor + items.Count < total;
                    var result = new Result
                    {
                        Prefab = PrefabSummaryFrom(path, root, root, true), Items = items,
                        PageInfo = new PageInfo { PageSize = pageSize, Cursor = cursor, HasNextCursor = hasNext, NextCursor = hasNext ? (long?)(cursor + items.Count) : null, Total = total }
                    };
                    return new System.Threading.Tasks.ValueTask<Result>(result);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        // 统计完整 Prefab 层级节点数。
        private static int CountPrefabHierarchy(Transform transform)
        {
            var count = 1;
            foreach (Transform child in transform) count += CountPrefabHierarchy(child);
            return count;
        }

        // 深度优先收集指定游标页，达到页大小后停止。
        private static void CollectPrefabHierarchy(Transform transform, List<PrefabHierarchyContext.HierarchyItem> items, int cursor, int pageSize, ref int visited)
        {
            if (visited >= cursor && items.Count < pageSize) items.Add(PrefabHierarchyItemFrom(transform));
            visited++;
            foreach (Transform child in transform)
            {
                if (items.Count >= pageSize) return;
                CollectPrefabHierarchy(child, items, cursor, pageSize, ref visited);
            }
        }

        // 将单个 Transform 映射为层级项，不递归展开子项。
        private static PrefabHierarchyContext.HierarchyItem PrefabHierarchyItemFrom(Transform transform)
        {
            var componentTypes = new List<string>();
            foreach (var component in transform.GetComponents<Component>()) componentTypes.Add(component == null ? "MissingScript" : component.GetType().Name);
            var gameObject = transform.gameObject;
            return new PrefabHierarchyContext.HierarchyItem
            {
                Id = gameObject.GetInstanceID(), Name = gameObject.name, Path = HierarchyPath(gameObject), Active = gameObject.activeSelf, ActiveInHierarchy = gameObject.activeInHierarchy,
                Tag = gameObject.tag, Layer = gameObject.layer, ChildCount = transform.childCount, ChildrenTruncated = transform.childCount > 0,
                ComponentCount = componentTypes.Count, ComponentTypes = componentTypes, Components = new List<string>(componentTypes)
            };
        }
    }
}
#endif
