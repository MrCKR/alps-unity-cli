#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>go.find — exact selector 直达，其他 selector 按 raw hierarchy position 有界续页。</summary>
    public class GameObjectFindTool : IUnityCliTool<GameObjectFindTool.Request, GameObjectFindTool.Result>
    {
        public sealed class Request
        {
            public string SearchTerm { get; set; }
            public string Query { get; set; }
            public string Target { get; set; }
            public string Name { get; set; }
            public string Tag { get; set; }
            public string Layer { get; set; }
            public string Component { get; set; }
            public string Path { get; set; }
            public string Id { get; set; }
            [UnityCliStringEnum("by_name", "by_tag", "by_layer", "by_component", "by_path", "by_id")]
            public string SearchMethod { get; set; }
            public bool IncludeInactive { get; set; }
            public bool PreferPrefabStage { get; set; }
            [UnityCliRange(1L, 500L)] public int? PageSize { get; set; }
            public string Cursor { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public bool PrefabStageOpen { get; set; }
            public long ReturnedCount { get; set; }
            [UnityCliItemsRange(0, 500)] public List<Item> Items { get; set; }
            public PageInfo PageInfo { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public sealed class Item
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
        }

        public sealed class PageInfo
        {
            public long PageSize { get; set; }
            public string Cursor { get; set; }
            public string NextCursor { get; set; }
            public bool Complete { get; set; }
            public bool Truncated { get; set; }
            public string Generation { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "go.find",
            Summary = "Resolve exact GameObject ids/paths directly or page filtered hierarchy matches from stable raw positions.",
            Description = "Search loaded GameObjects by exact name, tag, layer or component. by_id uses direct instance resolution and by_path walks only the exact case-sensitive path; neither materializes the scene tree. Other selectors stream Unity hierarchy order with pageSize 1..500 (default 50). All work shares budget defaults of 10000 scans, 4096 nodes, 2048 getters, 4096 reflection calls, 250 main-thread ms and 1 MiB UTF-8; maxima are 100000/20000/10000/20000/2000 ms/8 MiB. No filtered total is fabricated: pageInfo.complete/truncated and nextCursor identify the next raw object. preferPrefabStage scans the open Prefab Stage first. Read-only in Edit and Play Mode.",
            Group = "go",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Find an exact object name", Input = new Request { SearchTerm = "Player", SearchMethod = "by_name", PageSize = 25 } },
                new ToolExample { Description = "Resolve a full hierarchy path directly", Input = new Request { SearchTerm = "World/Player", SearchMethod = "by_path" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            request = request ?? new Request();
            var searchTerm = request.SearchTerm ?? request.Query ?? request.Target ?? request.Name ?? request.Tag ?? request.Layer ?? request.Component ?? request.Path ?? request.Id;
            if (string.IsNullOrEmpty(searchTerm)) throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject find requires a search term.", "Pass searchTerm and one documented searchMethod.");
            var method = request.SearchMethod ?? InferSearchMethod(request);
            var budget = ctx.UseReadBudget(request.Budget);
            var pageSize = request.PageSize ?? 50;
            if ((method == "by_id" || method == "by_path") && !string.IsNullOrEmpty(request.Cursor))
                throw new UnityCliBridge.BridgeCommandException("E_CONTINUATION_INVALID", "Exact by_id/by_path resolution does not accept cursor.", "Remove cursor for exact resolution.");

            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            var prefabOpen = request.PreferPrefabStage && prefabStage != null && prefabStage.prefabContentsRoot != null;
            var generation = UnityCliContinuationToken.Fingerprint(
                UnityCliReadGeneration.Hierarchy(SceneManager.GetActiveScene()) + "\0" + method + "\0" + searchTerm + "\0" + request.IncludeInactive + "\0" + prefabOpen);
            if (method == "by_id" || method == "by_path")
            {
                var exact = ResolveExact(method, searchTerm, budget);
                var exactItems = new List<Item>();
                if (exact != null)
                {
                    if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    if (request.IncludeInactive || exact.activeInHierarchy) exactItems.Add(BuildItem(exact, budget));
                }
                return new System.Threading.Tasks.ValueTask<Result>(ResultPage(exactItems, pageSize, request.Cursor, null, generation, prefabOpen, budget));
            }

            Type componentType = null;
            int? layer = null;
            if (method == "by_component") componentType = GameObjectToolSupport.ResolveComponentType(searchTerm, budget);
            if (method == "by_layer") layer = GameObjectToolSupport.ParseLayer(searchTerm);
            var prefabTraversal = prefabOpen ? new UnityCliHierarchyPager.Traversal(prefabStage.scene, prefabStage.prefabContentsRoot, int.MaxValue, budget) : null;
            var sceneTraversal = new UnityCliHierarchyPager.LoadedSceneTraversal(budget, prefabOpen ? prefabStage.scene.handle : 0);
            GameObject current;
            var phase = prefabOpen ? "prefab" : "scene";
            if (string.IsNullOrEmpty(request.Cursor)) current = prefabOpen ? prefabStage.prefabContentsRoot : sceneTraversal.First(budget);
            else
            {
                current = UnityCliHierarchyPager.ResolveCursor(request.Cursor, "go.find", generation, budget, out phase);
                if (phase != "scene" && phase != "prefab") throw UnityCliHierarchyPager.Stale();
                if (phase == "prefab" && (!prefabOpen || !IsDescendantOrSelf(current.transform, prefabStage.prefabContentsRoot.transform, budget))) throw UnityCliHierarchyPager.Stale();
                if (phase == "scene" && prefabOpen && current.scene.handle == prefabStage.scene.handle) throw UnityCliHierarchyPager.Stale();
            }

            var items = new List<Item>();
            while (current != null && items.Count < pageSize)
            {
                var candidate = current;
                if (!budget.TryConsumeScannedRecord()) break;
                try
                {
                    var nextPhase = phase;
                    var next = phase == "prefab" ? prefabTraversal.Next(candidate, budget) : sceneTraversal.Next(candidate, budget);
                    if (phase == "prefab" && next == null) { nextPhase = "scene"; next = sceneTraversal.First(budget); }
                    if (Matches(candidate, method, searchTerm, request.IncludeInactive, componentType, layer, budget))
                        items.Add(BuildItem(candidate, budget));
                    phase = nextPhase;
                    current = next;
                }
                catch (UnityCliRequestBudgetExceededException)
                {
                    current = candidate;
                    break;
                }
            }

            if (items.Count == 0 && current != null) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension ?? "scannedRecords");
            var nextCursor = UnityCliHierarchyPager.Cursor("go.find", generation, phase, current, budget);
            return new System.Threading.Tasks.ValueTask<Result>(ResultPage(items, pageSize, request.Cursor, nextCursor, generation, prefabOpen, budget));
        }

        private static Result ResultPage(List<Item> items, int pageSize, string cursor, string nextCursor, string generation, bool prefabOpen, UnityCliRequestBudget budget)
        {
            return new Result
            {
                PrefabStageOpen = prefabOpen,
                ReturnedCount = items.Count,
                Items = items,
                PageInfo = new PageInfo
                {
                    PageSize = pageSize, Cursor = cursor, NextCursor = nextCursor,
                    Complete = nextCursor == null, Truncated = nextCursor != null, Generation = generation
                },
                Budget = budget.Usage()
            };
        }

        private static GameObject ResolveExact(string method, string value, UnityCliRequestBudget budget)
        {
            if (method == "by_path") return UnityCliBridge.ObjectRefResolver.FindGameObjectByExactPath(value, budget);
            int instanceId;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId)) return null;
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            return EditorUtility.InstanceIDToObject(instanceId) as GameObject;
        }

        private static string InferSearchMethod(Request request)
        {
            if (!string.IsNullOrEmpty(request.Tag)) return "by_tag";
            if (!string.IsNullOrEmpty(request.Layer)) return "by_layer";
            if (!string.IsNullOrEmpty(request.Component)) return "by_component";
            if (!string.IsNullOrEmpty(request.Path)) return "by_path";
            if (!string.IsNullOrEmpty(request.Id)) return "by_id";
            return "by_name";
        }

        private static bool Matches(GameObject gameObject, string method, string value, bool includeInactive, Type componentType, int? layer, UnityCliRequestBudget budget)
        {
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            if (!includeInactive && !gameObject.activeInHierarchy) return false;
            if (method == "by_component")
            {
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                return gameObject.GetComponent(componentType) != null;
            }
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            if (method == "by_tag") return string.Equals(gameObject.tag, value, StringComparison.Ordinal);
            if (method == "by_layer") return gameObject.layer == layer;
            if (method != "by_name") throw new UnityCliBridge.BridgeCommandException("E_SEARCH_METHOD_INVALID", "Unsupported GameObject searchMethod: " + method, "Use by_name, by_tag, by_layer, by_component, by_path, or by_id.");
            return string.Equals(gameObject.name, value, StringComparison.Ordinal);
        }

        private static Item BuildItem(GameObject gameObject, UnityCliRequestBudget budget)
        {
            if (!budget.TryConsumeNode() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var path = UnityCliHierarchyPager.Path(gameObject, budget);
            var name = gameObject.name;
            if (!budget.TryConsumeGetter() || !budget.TryConsumeGetter() || !budget.TryConsumeGetter()
                || !budget.TryConsumeGetter() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var tag = gameObject.tag;
            var childCount = gameObject.transform.childCount;
            if (!budget.TryReserveSerializedUtf8(name) || !budget.TryReserveSerializedUtf8(path) || !budget.TryReserveSerializedUtf8(tag))
                throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            return new Item
            {
                Id = gameObject.GetInstanceID(), Name = name, Path = path,
                Active = gameObject.activeSelf, ActiveInHierarchy = gameObject.activeInHierarchy,
                Tag = tag, Layer = gameObject.layer,
                ChildCount = childCount, ChildrenTruncated = childCount > 0
            };
        }

        private static bool IsDescendantOrSelf(Transform value, Transform root, UnityCliRequestBudget budget)
        {
            while (value != null)
            {
                if (value == root) return true;
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                value = value.parent;
            }
            return false;
        }
    }
}
#endif
