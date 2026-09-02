#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>scene.hierarchy — 以 next raw GameObject 为游标的有界前序层级读取。</summary>
    public class SceneHierarchyTool : IUnityCliTool<SceneHierarchyTool.Request, SceneHierarchyTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRange(1L, 500L)] public int? PageSize { get; set; }
            public string Cursor { get; set; }
            public string Parent { get; set; }
            [UnityCliRange(0L, 16L)] public int? MaxDepth { get; set; }
            public string Query { get; set; }
            public string NameFilter { get; set; }
            public string ComponentType { get; set; }
            public bool IncludeComponents { get; set; }
            public bool IncludeInactive { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public SceneSummary Scene { get; set; }
            public List<HierarchyItem> Items { get; set; }
            public PageInfo PageInfo { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public sealed class SceneSummary { public string Name { get; set; } public string Path { get; set; } }

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
            public long? ComponentCount { get; set; }
            public List<string> ComponentTypes { get; set; }
            public List<string> Components { get; set; }
        }

        public sealed class PageInfo
        {
            public long ReturnedCount { get; set; }
            public long PageSize { get; set; }
            public string Cursor { get; set; }
            public string NextCursor { get; set; }
            public bool HasMore { get; set; }
            public bool Complete { get; set; }
            public bool Truncated { get; set; }
            public string Generation { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.hierarchy",
            Summary = "Page the active scene hierarchy from stable raw GameObject positions without materializing the tree.",
            Description = "Read active-scene GameObjects in Unity hierarchy order. pageSize is 1..500 (default 50), maxDepth is 0..16 (default 1), and parent is an exact instance id or full case-sensitive hierarchy path. Each page scans only forward from nextCursor and shares budget defaults of 10000 scans, 4096 nodes, 2048 getters, 4096 reflection calls, 250 main-thread ms and 1 MiB UTF-8; maxima are 100000/20000/10000/20000/2000 ms/8 MiB. pageInfo never fabricates a filtered total: complete=false/truncated=true and nextCursor identify the next raw GameObject. Old generations return E_CONTINUATION_STALE. Read-only in Edit and Play Mode.",
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
            request = request ?? new Request();
            var budget = ctx.UseReadBudget(request.Budget);
            var scene = SceneManager.GetActiveScene();
            var parent = ResolveParent(request.Parent, budget);
            if (parent != null && parent.scene.handle != scene.handle)
                throw new UnityCliBridge.BridgeCommandException("E_SCENE_HIERARCHY_PARENT_NOT_FOUND", "Parent is not in the active scene: " + request.Parent, "Pass an exact active-scene hierarchy path or instance id.");
            var maxDepth = request.MaxDepth ?? 1;
            var pageSize = request.PageSize ?? 50;
            var query = request.Query ?? request.NameFilter;
            var selector = (parent == null ? "root" : parent.GetInstanceID().ToString(CultureInfo.InvariantCulture)) + "\0" + maxDepth.ToString(CultureInfo.InvariantCulture)
                + "\0" + query + "\0" + request.ComponentType + "\0" + request.IncludeInactive + "\0" + request.IncludeComponents;
            var generation = UnityCliContinuationToken.Fingerprint(UnityCliReadGeneration.Hierarchy(scene) + "\0" + selector);
            GameObject current;
            if (string.IsNullOrEmpty(request.Cursor)) current = UnityCliHierarchyPager.First(scene, parent);
            else
            {
                current = UnityCliHierarchyPager.ResolveCursor(request.Cursor, "scene.hierarchy", generation, out var phase);
                if (phase != "scene" || current.scene.handle != scene.handle || !WithinBoundary(current.transform, parent == null ? null : parent.transform, maxDepth))
                    throw UnityCliHierarchyPager.Stale();
            }

            var items = new List<HierarchyItem>();
            while (current != null && items.Count < pageSize)
            {
                var candidate = current;
                if (!budget.TryConsumeScannedRecord()) break;
                current = UnityCliHierarchyPager.Next(candidate, scene, parent, maxDepth);
                try
                {
                    if (!Matches(candidate, query, request.ComponentType, request.IncludeInactive, budget)) continue;
                    items.Add(BuildItem(candidate.transform, parent, request.IncludeComponents, budget));
                }
                catch (UnityCliRequestBudgetExceededException)
                {
                    current = candidate;
                    break;
                }
            }

            var nextCursor = UnityCliHierarchyPager.Cursor("scene.hierarchy", generation, "scene", current);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Scene = new SceneSummary { Name = scene.name, Path = scene.path },
                Items = items,
                PageInfo = new PageInfo
                {
                    ReturnedCount = items.Count,
                    PageSize = pageSize,
                    Cursor = request.Cursor,
                    NextCursor = nextCursor,
                    HasMore = nextCursor != null,
                    Complete = nextCursor == null,
                    Truncated = nextCursor != null,
                    Generation = generation
                },
                Budget = budget.Usage()
            });
        }

        private static GameObject ResolveParent(string parent, UnityCliRequestBudget budget)
        {
            if (string.IsNullOrEmpty(parent)) return null;
            int instanceId;
            GameObject value = null;
            if (int.TryParse(parent, NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId)) value = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            else value = UnityCliBridge.ObjectRefResolver.FindGameObjectByExactPath(parent, budget);
            if (value == null) throw new UnityCliBridge.BridgeCommandException("E_SCENE_HIERARCHY_PARENT_NOT_FOUND", "Parent GameObject not found: " + parent, "Pass an exact hierarchy path or instance id from a fresh scene.hierarchy page.");
            return value;
        }

        private static bool WithinBoundary(Transform value, Transform boundary, int maxDepth)
        {
            if (value == null) return false;
            var current = value;
            while (current != null && current != boundary) current = current.parent;
            if (boundary != null && current != boundary) return false;
            return UnityCliHierarchyPager.Depth(value, boundary) <= maxDepth;
        }

        private static bool Matches(GameObject gameObject, string query, string componentType, bool includeInactive, UnityCliRequestBudget budget)
        {
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            if (!includeInactive && !gameObject.activeInHierarchy) return false;
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var name = gameObject.name;
            if (!string.IsNullOrEmpty(query))
            {
                var path = UnityCliHierarchyPager.Path(gameObject, budget);
                if (name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }
            if (string.IsNullOrEmpty(componentType)) return true;
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (!budget.TryConsumeScannedRecord()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                if (component == null && string.Equals(componentType, "MissingScript", StringComparison.OrdinalIgnoreCase)) return true;
                if (component == null) continue;
                var type = component.GetType();
                if (string.Equals(type.Name, componentType, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type.FullName, componentType, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static HierarchyItem BuildItem(Transform transform, GameObject parent, bool includeComponents, UnityCliRequestBudget budget)
        {
            if (!budget.TryConsumeNode()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var gameObject = transform.gameObject;
            var path = UnityCliHierarchyPager.Path(gameObject, budget);
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var name = gameObject.name;
            if (!budget.TryReserveSerializedUtf8(name) || !budget.TryReserveSerializedUtf8(path)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            List<string> componentNames = null;
            if (includeComponents)
            {
                componentNames = new List<string>();
                foreach (var component in gameObject.GetComponents<Component>())
                {
                    if (!budget.TryConsumeScannedRecord()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    componentNames.Add(component == null ? "MissingScript" : component.GetType().FullName);
                }
            }
            return new HierarchyItem
            {
                Id = gameObject.GetInstanceID(), Name = name, Path = path,
                Depth = UnityCliHierarchyPager.Depth(transform, parent == null ? null : parent.transform),
                Active = gameObject.activeSelf, ActiveInHierarchy = gameObject.activeInHierarchy,
                Tag = gameObject.tag, Layer = gameObject.layer, ChildCount = transform.childCount,
                ChildrenTruncated = transform.childCount > 0,
                ComponentCount = componentNames == null ? (long?)null : componentNames.Count,
                ComponentTypes = componentNames,
                Components = componentNames
            };
        }
    }
}
#endif
