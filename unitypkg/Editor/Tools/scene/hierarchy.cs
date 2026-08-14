#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.hierarchy — 读取激活场景的轻量分页层级:parent/深度/名称过滤/组件预览。
    /// </summary>
    public class SceneHierarchyTool : IUnityCliTool<SceneHierarchyTool.Request, SceneHierarchyTool.Result>
    {
        public sealed class Request
        {
            public int? PageSize { get; set; }
            public int? Cursor { get; set; }
            public string Parent { get; set; }
            public int? MaxDepth { get; set; }
            public string Query { get; set; }
            public string NameFilter { get; set; }
            public string ComponentType { get; set; }
            public bool IncludeComponents { get; set; }
            public bool IncludeInactive { get; set; }
        }

        public sealed class Result
        {
            public SceneSummary Scene { get; set; }
            public List<HierarchyItem> Items { get; set; }
            public PageInfo PageInfo { get; set; }
        }

        public sealed class SceneSummary
        {
            public string Name { get; set; }
            public string Path { get; set; }
        }

        public sealed class HierarchyItem
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
            public long Depth { get; set; }
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
            public long Total { get; set; }
            public long TotalCount { get; set; }
            public long ReturnedCount { get; set; }
            public long PageSize { get; set; }
            public long Cursor { get; set; }
            public long? NextCursor { get; set; }
            public bool HasMore { get; set; }
            public bool Truncated { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.hierarchy",
            Summary = "Read a lightweight, paged hierarchy of active scene GameObjects.",
            Description = "Read a lightweight, paged hierarchy of active scene GameObjects. Use parent to list a subtree, maxDepth to bound recursion, nameFilter/filter to narrow results, and includeComponents only when component previews are needed. For deep object or component fields, use go.inspect, object.get-data, or component.get after selecting a stable path/id.",
            Group = "scene",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List a bounded hierarchy page", Input = new Request { PageSize = 25, MaxDepth = 1 } },
                new ToolExample { Description = "Find camera nodes by component type", Input = new Request { ComponentType = "Camera", PageSize = 10 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var scene = SceneManager.GetActiveScene();
            var parent = request.Parent;
            var includeInactive = request.IncludeInactive;
            var includeComponents = request.IncludeComponents;
            var query = request.Query ?? request.NameFilter;
            var componentType = request.ComponentType;
            var maxDepth = Math.Max(0, Math.Min(request.MaxDepth ?? 1, 16));
            var cursor = Math.Max(0, request.Cursor ?? 0);
            var pageSize = Math.Max(1, Math.Min(request.PageSize ?? 50, 500));

            var roots = new List<GameObject>();
            if (!string.IsNullOrEmpty(parent))
            {
                var parentObject = SceneToolSupport.ResolveGameObjectByPath(parent);
                if (parentObject == null)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_SCENE_HIERARCHY_PARENT_NOT_FOUND", "Parent GameObject not found: " + parent, "Run go.find or scene.hierarchy without parent, then pass a stable hierarchy path or instance id.");
                }
                for (var i = 0; i < parentObject.transform.childCount; i++)
                {
                    roots.Add(parentObject.transform.GetChild(i).gameObject);
                }
            }
            else
            {
                roots.AddRange(scene.GetRootGameObjects());
            }

            var allItems = new List<HierarchyItem>();
            foreach (var root in roots.OrderBy(g => HierarchyPath(g), StringComparer.Ordinal))
            {
                CollectHierarchy(root.transform, allItems, 0, maxDepth, query, includeInactive, includeComponents, componentType);
            }

            var total = allItems.Count;
            var page = allItems.Skip(cursor).Take(pageSize).ToList();
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Scene = new SceneSummary { Name = scene.name, Path = scene.path },
                Items = page,
                PageInfo = BuildPageInfo(pageSize, cursor, total, page.Count)
            });
        }

        // 按深度/名称/inactive 策略收集层级项;分页在调用方统一处理。
        private static void CollectHierarchy(Transform transform, List<HierarchyItem> items, int depth, int maxDepth, string query, bool includeInactive, bool includeComponents, string componentType)
        {
            var gameObject = transform.gameObject;
            if (includeInactive || gameObject.activeInHierarchy)
            {
                if (TextMatches(query, gameObject.name, HierarchyPath(gameObject)) && ComponentMatches(gameObject, componentType))
                {
                    items.Add(BuildHierarchyItem(transform, depth, includeComponents));
                }
            }
            if (depth >= maxDepth)
            {
                return;
            }
            foreach (Transform child in transform)
            {
                CollectHierarchy(child, items, depth + 1, maxDepth, query, includeInactive, includeComponents, componentType);
            }
        }

        // 生成轻量 GameObject 层级项;componentTypes 始终返回,components 仅在 includeComponents 时返回。
        private static HierarchyItem BuildHierarchyItem(Transform transform, int depth, bool includeComponents)
        {
            var componentNames = new List<string>();
            foreach (var component in transform.GetComponents<Component>())
            {
                componentNames.Add(component != null ? component.GetType().Name : "MissingScript");
            }
            var gameObject = transform.gameObject;
            var item = new HierarchyItem
            {
                Id = gameObject.GetInstanceID(),
                Name = gameObject.name,
                Path = HierarchyPath(gameObject),
                Depth = depth,
                Active = gameObject.activeSelf,
                ActiveInHierarchy = gameObject.activeInHierarchy,
                Tag = gameObject.tag,
                Layer = gameObject.layer,
                ChildCount = transform.childCount,
                ChildrenTruncated = transform.childCount > 0,
                ComponentCount = componentNames.Count,
                ComponentTypes = componentNames
            };
            if (includeComponents)
            {
                item.Components = componentNames;
            }
            return item;
        }

        private static string HierarchyPath(GameObject gameObject)
        {
            var names = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names.ToArray());
        }

        // 通用文本筛选;query 为空时视为匹配。
        private static bool TextMatches(string query, params string[] values)
        {
            if (string.IsNullOrEmpty(query)) return true;
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        // 判断 GameObject 是否拥有指定 Component 类型;空 componentType 表示不过滤(字符串匹配,不解析程序集)。
        private static bool ComponentMatches(GameObject gameObject, string componentType)
        {
            if (gameObject == null || string.IsNullOrEmpty(componentType)) return true;
            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (ComponentTypeMatches(component, componentType)) return true;
            }
            return false;
        }

        private static bool ComponentTypeMatches(Component component, string componentType)
        {
            if (string.IsNullOrEmpty(componentType)) return true;
            if (component == null) return string.Equals(componentType, "MissingScript", StringComparison.OrdinalIgnoreCase);
            var type = component.GetType();
            return string.Equals(type.Name, componentType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type.FullName, componentType, StringComparison.OrdinalIgnoreCase)
                || type.FullName.IndexOf(componentType, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static PageInfo BuildPageInfo(int pageSize, int cursor, int totalCount, int returnedCount)
        {
            long? next = cursor + returnedCount < totalCount ? (long?)(cursor + returnedCount) : null;
            return new PageInfo
            {
                Total = totalCount,
                TotalCount = totalCount,
                ReturnedCount = returnedCount,
                PageSize = pageSize,
                Cursor = cursor,
                NextCursor = next,
                HasMore = next != null,
                Truncated = next != null
            };
        }
    }
}
#endif
