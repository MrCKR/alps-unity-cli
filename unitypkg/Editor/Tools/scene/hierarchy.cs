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
            [UnityCliItemsRange(0, 500)] public List<HierarchyItem> Items { get; set; }
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
            [UnityCliItemsRange(0, ObjectRefResolver.AbsoluteMaxBatchItems)] public List<string> ComponentTypes { get; set; }
            [UnityCliItemsRange(0, ObjectRefResolver.AbsoluteMaxBatchItems)] public List<string> Components { get; set; }
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
            Summary = "Read a lightweight, paged active scene hierarchy from stable raw GameObject positions.",
            Description = "Read a lightweight active-scene hierarchy in Edit or Play mode. Use parent for one subtree, maxDepth to limit recursion, query/name/component filters to narrow results, and includeComponents only when component previews are needed. Read one selected object with scene.gameobject; use object.get-data or component.get for deep fields. pageSize is 1..500 (default 50), maxDepth is 0..16 (default 1), each page scans only forward from nextCursor, pageInfo never fabricates a filtered total, and old generations return E_CONTINUATION_STALE. All pages share scan/node/getter/reflection/time/UTF-8 budgets.",
            Group = "scene",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List one hierarchy page", Input = new Request { PageSize = 25, MaxDepth = 1 } },
                new ToolExample { Description = "Find camera nodes by component type", Input = new Request { ComponentType = "Camera", PageSize = 10 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            request = request ?? new Request();
            var budget = ctx.UseReadBudget(request.Budget);
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var scene = SceneManager.GetActiveScene();
            if (!budget.TryConsumeGetter() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var sceneName = scene.name;
            var scenePath = scene.path;
            if (!budget.TryReserveSerializedUtf8(sceneName) || !budget.TryReserveSerializedUtf8(scenePath)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var parent = ResolveParent(request.Parent, budget);
            if (parent != null)
            {
                if (!budget.TryConsumeGetters(2)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                if (parent.scene.handle != scene.handle)
                    throw new UnityCliBridge.BridgeCommandException("E_SCENE_HIERARCHY_PARENT_NOT_FOUND", "Parent is not in the active scene: " + request.Parent, "Pass an exact active-scene hierarchy path or instance id.");
            }
            var maxDepth = request.MaxDepth ?? 1;
            var pageSize = request.PageSize ?? 50;
            var query = request.Query ?? request.NameFilter;
            if (parent != null && !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var selector = (parent == null ? "root" : parent.GetInstanceID().ToString(CultureInfo.InvariantCulture)) + "\0" + maxDepth.ToString(CultureInfo.InvariantCulture)
                + "\0" + query + "\0" + request.ComponentType + "\0" + request.IncludeInactive + "\0" + request.IncludeComponents;
            var generation = UnityCliContinuationToken.Fingerprint(UnityCliReadGeneration.Hierarchy(scene) + "\0" + selector);
            var traversal = new UnityCliHierarchyPager.Traversal(scene, parent, maxDepth, budget);
            GameObject current;
            if (string.IsNullOrEmpty(request.Cursor)) current = traversal.First(budget);
            else
            {
                current = UnityCliHierarchyPager.ResolveCursor(request.Cursor, "scene.hierarchy", generation, budget, out var phase);
                if (!budget.TryConsumeGetters(parent == null ? 2 : 3)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                if (phase != "scene" || current.scene.handle != scene.handle || !WithinBoundary(current.transform, parent == null ? null : parent.transform, maxDepth, budget))
                    throw UnityCliHierarchyPager.Stale();
            }

            var items = new List<HierarchyItem>();
            while (current != null && items.Count < pageSize)
            {
                var candidate = current;
                if (!budget.TryConsumeScannedRecord()) break;
                try
                {
                    var next = traversal.Next(candidate, budget);
                    if (Matches(candidate, query, request.ComponentType, request.IncludeInactive, budget))
                        items.Add(BuildItem(candidate.transform, parent, request.IncludeComponents, budget));
                    current = next;
                }
                catch (UnityCliRequestBudgetExceededException)
                {
                    current = candidate;
                    break;
                }
            }

            if (items.Count == 0 && current != null) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension ?? "scannedRecords");
            var nextCursor = UnityCliHierarchyPager.Cursor("scene.hierarchy", generation, "scene", current, budget);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Scene = new SceneSummary { Name = sceneName, Path = scenePath },
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
            if (int.TryParse(parent, NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId))
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                value = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
            }
            else value = UnityCliBridge.ObjectRefResolver.FindGameObjectByExactPath(parent, budget);
            if (value == null) throw new UnityCliBridge.BridgeCommandException("E_SCENE_HIERARCHY_PARENT_NOT_FOUND", "Parent GameObject not found: " + parent, "Pass an exact hierarchy path or instance id from a fresh scene.hierarchy page.");
            return value;
        }

        private static bool WithinBoundary(Transform value, Transform boundary, int maxDepth, UnityCliRequestBudget budget)
        {
            if (value == null) return false;
            var current = value;
            var depth = 0;
            while (current != null && current != boundary)
            {
                if (depth > maxDepth) return false;
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                current = current.parent;
                depth++;
            }
            return (boundary == null || current == boundary) && depth - 1 <= maxDepth;
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
            foreach (var component in UnityCliBridge.ObjectRefResolver.BoundedComponents(gameObject, budget))
            {
                if (component == null && string.Equals(componentType, "MissingScript", StringComparison.OrdinalIgnoreCase)) return true;
                if (component == null) continue;
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var type = component.GetType();
                if (string.Equals(type.Name, componentType, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type.FullName, componentType, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static HierarchyItem BuildItem(Transform transform, GameObject parent, bool includeComponents, UnityCliRequestBudget budget)
        {
            if (!budget.TryConsumeNode() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var gameObject = transform.gameObject;
            var path = UnityCliHierarchyPager.Path(gameObject, budget);
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var name = gameObject.name;
            if (!budget.TryReserveSerializedUtf8(name) || !budget.TryReserveSerializedUtf8(path)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            List<string> componentNames = null;
            if (includeComponents)
            {
                componentNames = new List<string>();
                foreach (var component in UnityCliBridge.ObjectRefResolver.BoundedComponents(gameObject, budget))
                {
                    if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var componentName = component == null ? "MissingScript" : component.GetType().FullName;
                    if (!budget.TryReserveSerializedUtf8(componentName)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    componentNames.Add(componentName);
                }
            }
            if (!budget.TryConsumeGetter() || !budget.TryConsumeGetter() || !budget.TryConsumeGetter()
                || !budget.TryConsumeGetter() || !budget.TryConsumeGetter() || !budget.TryConsumeGetter())
                throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var childCount = transform.childCount;
            var tag = gameObject.tag;
            if (!budget.TryReserveSerializedUtf8(tag)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            return new HierarchyItem
            {
                Id = gameObject.GetInstanceID(), Name = name, Path = path,
                Depth = UnityCliHierarchyPager.Depth(transform, parent == null ? null : parent.transform, budget),
                Active = gameObject.activeSelf, ActiveInHierarchy = gameObject.activeInHierarchy,
                Tag = tag, Layer = gameObject.layer, ChildCount = childCount,
                ChildrenTruncated = childCount > 0,
                ComponentCount = componentNames == null ? (long?)null : componentNames.Count,
                ComponentTypes = componentNames,
                Components = componentNames
            };
        }
    }
}
#endif
