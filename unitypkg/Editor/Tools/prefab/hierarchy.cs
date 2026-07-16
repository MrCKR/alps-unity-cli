#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// prefab.hierarchy — 读取 Prefab 子级/组件摘要,支持 parent/query/componentType 过滤与分页。
    /// </summary>
    public class PrefabHierarchyTool : IUnityCliTool<PrefabHierarchyTool.Request, PrefabHierarchyTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Parent { get; set; }
            public string Query { get; set; }
            public string NameFilter { get; set; }
            public string Filter { get; set; }
            public string ComponentType { get; set; }
            public bool? IncludeSummaryDetails { get; set; }
            public int? PageSize { get; set; }
            public int? Cursor { get; set; }
        }

        public sealed class Result
        {
            public PrefabInfo Prefab { get; set; }
            public string Parent { get; set; }
            public string Query { get; set; }
            public string ComponentType { get; set; }
            public List<PrefabHierarchyItem> Items { get; set; }
            public List<PrefabHierarchyItem> Children { get; set; }
            public AssetPageInfo PageInfo { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "prefab.hierarchy",
            Summary = "List prefab child/component summaries with parent, query and componentType filters, paged.",
            Description = "List prefab child/component summaries. With no parent, returns the root; with parent, returns that parent's children. Filter by query/nameFilter on name or path, and componentType on a component type name. Results are paged via cursor/pageSize.",
            Group = "prefab",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List root hierarchy", Input = new Request { Path = "Assets/Prefabs/Player.prefab", PageSize = 25 } },
                new ToolExample { Description = "List children of a parent", Input = new Request { Path = "Assets/Prefabs/Player.prefab", Parent = "Rig", PageSize = 25 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = PrefabToolSupport.RequirePrefabPath(request.Path);
            PrefabToolSupport.LoadPrefabAsset(path);
            var pageSize = Math.Max(1, Math.Min(request.PageSize ?? 25, 500));
            var cursor = Math.Max(0, request.Cursor ?? 0);
            var parent = request.Parent;
            var query = request.Query ?? request.NameFilter ?? request.Filter;
            var componentType = request.ComponentType;
            var includeSummaryDetails = request.IncludeSummaryDetails ?? false;
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var parentTransform = ResolveParent(root.transform, parent);
                var candidates = new List<GameObject>();
                if (string.IsNullOrEmpty(parent))
                {
                    candidates.Add(root);
                }
                else
                {
                    foreach (Transform child in parentTransform)
                    {
                        candidates.Add(child.gameObject);
                    }
                }
                var matches = new List<GameObject>();
                foreach (var go in candidates)
                {
                    if (TextMatches(query, go.name, GameObjectToolSupport.HierarchyPath(go))
                        && ComponentMatches(go, componentType))
                    {
                        matches.Add(go);
                    }
                }
                matches.Sort((a, b) => StringComparer.Ordinal.Compare(GameObjectToolSupport.HierarchyPath(a), GameObjectToolSupport.HierarchyPath(b)));
                var page = new List<PrefabHierarchyItem>();
                for (var i = cursor; i < matches.Count && page.Count < pageSize; i++)
                {
                    page.Add(PrefabToolSupport.BuildHierarchyItem(matches[i]));
                }
                long? nextCursor = cursor + page.Count < matches.Count ? (long?)(cursor + page.Count) : null;
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Prefab = PrefabToolSupport.BuildPrefabInfo(path, root, root, includeSummaryDetails),
                    Parent = parent,
                    Query = query,
                    ComponentType = componentType,
                    Items = page,
                    Children = page,
                    PageInfo = new AssetPageInfo
                    {
                        PageSize = pageSize,
                        Cursor = cursor,
                        NextCursor = nextCursor,
                        Total = matches.Count,
                        Truncated = nextCursor != null
                    }
                });
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // 解析 parent:空返回 root;名称/路径匹配 root 或其子孙
        private static Transform ResolveParent(Transform root, string parent)
        {
            if (string.IsNullOrEmpty(parent)) return root;
            if (string.Equals(parent, root.name, StringComparison.Ordinal)
                || string.Equals(parent, GameObjectToolSupport.HierarchyPath(root.gameObject), StringComparison.Ordinal))
            {
                return root;
            }
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (transform == null) continue;
                if (string.Equals(transform.name, parent, StringComparison.Ordinal)
                    || string.Equals(GameObjectToolSupport.HierarchyPath(transform.gameObject), parent, StringComparison.Ordinal))
                {
                    return transform;
                }
            }
            throw new UnityCliBridge.BridgeCommandException("E_PREFAB_HIERARCHY_PARENT_NOT_FOUND", "Prefab hierarchy parent not found: " + parent, "Run prefab.hierarchy without parent, then pass a returned path as parent.");
        }

        private static bool TextMatches(string query, string name, string path)
        {
            if (string.IsNullOrEmpty(query)) return true;
            return (name != null && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                || (path != null && path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool ComponentMatches(GameObject go, string componentType)
        {
            if (string.IsNullOrEmpty(componentType)) return true;
            foreach (var component in go.GetComponents<Component>())
            {
                var name = component == null ? "MissingScript" : component.GetType().Name;
                if (string.Equals(name, componentType, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
#endif
