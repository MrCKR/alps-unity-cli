#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// go.find — 按名称/标签/层/组件/层级路径/实例ID 搜索 GameObject,可选优先 Prefab Stage。
    /// </summary>
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
            public string SearchMethod { get; set; }
            public bool IncludeInactive { get; set; }
            public bool PreferPrefabStage { get; set; }
            public int? PageSize { get; set; }
            public int? Cursor { get; set; }
        }

        public sealed class Result
        {
            public string Query { get; set; }
            public string SearchMethod { get; set; }
            public bool IncludeInactive { get; set; }
            public bool PreferPrefabStage { get; set; }
            public bool PrefabStageOpen { get; set; }
            public long MatchCount { get; set; }
            public List<GoFindItem> Items { get; set; }
            public GoPageInfo PageInfo { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "go.find",
            Summary = "Search GameObjects by name, tag, layer, component, hierarchy path, or instance id; optionally prefer an open Prefab Stage.",
            Description = "Search GameObjects by name, tag, layer, component, hierarchy path, or instance id. Results are lightweight and paged. Pass preferPrefabStage=true to first match inside an open Prefab Stage (prefabContentsRoot), then merge scene matches.",
            Group = "go",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Find by name", Input = new Request { SearchTerm = "Player", SearchMethod = "by_name", PageSize = 25 } },
                new ToolExample { Description = "Find objects with Camera component", Input = new Request { SearchTerm = "Camera", SearchMethod = "by_component", PageSize = 10 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var searchTerm = request.SearchTerm ?? request.Query ?? request.Target ?? request.Name ?? request.Tag ?? request.Layer ?? request.Component ?? request.Path ?? request.Id;
            if (string.IsNullOrEmpty(searchTerm))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject find requires a search term.", "Pass --input '{\"searchTerm\":\"Player\",\"searchMethod\":\"by_name\"}'.");
            }
            var searchMethod = request.SearchMethod ?? InferSearchMethod(request);
            var includeInactive = request.IncludeInactive;
            var preferPrefabStage = request.PreferPrefabStage;
            var pageSize = Math.Max(1, Math.Min(request.PageSize ?? 50, 500));
            var cursor = Math.Max(0, request.Cursor ?? 0);

            // Prefab Stage 优先:preferPrefabStage=true 且有打开的 Prefab Stage 时,先在 prefab 内容里匹配,再合并场景匹配
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            var prefabStageMatches = new List<GameObject>();
            var prefabStageOpen = preferPrefabStage && prefabStage != null && prefabStage.prefabContentsRoot != null;
            if (prefabStageOpen)
            {
                foreach (var t in prefabStage.prefabContentsRoot.GetComponentsInChildren<Transform>(includeInactive))
                {
                    if (MatchesGameObject(t.gameObject, searchMethod, searchTerm)) prefabStageMatches.Add(t.gameObject);
                }
            }
            var sceneMatches = SearchGameObjects(searchMethod, searchTerm, includeInactive);
            var matches = prefabStageOpen ? prefabStageMatches.Concat(sceneMatches).ToList() : sceneMatches;
            var total = matches.Count;
            var page = matches.Skip(cursor).Take(pageSize).ToList();
            long? nextCursor = cursor + page.Count < total ? (long?)(cursor + page.Count) : null;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Query = searchTerm,
                SearchMethod = searchMethod,
                IncludeInactive = includeInactive,
                PreferPrefabStage = preferPrefabStage,
                PrefabStageOpen = prefabStageOpen,
                MatchCount = total,
                Items = page.ConvertAll(BuildItem),
                PageInfo = new GoPageInfo { PageSize = pageSize, Cursor = cursor, NextCursor = nextCursor, Total = total }
            });
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

        private static List<GameObject> SearchGameObjects(string searchMethod, string searchTerm, bool includeInactive)
        {
            var allObjects = UnityEngine.Object.FindObjectsOfType<GameObject>(includeInactive)
                .OrderBy(GameObjectToolSupport.HierarchyPath, StringComparer.Ordinal).ToList();
            return allObjects.Where(go => MatchesGameObject(go, searchMethod, searchTerm)).ToList();
        }

        private static bool MatchesGameObject(GameObject gameObject, string searchMethod, string searchTerm)
        {
            var normalized = (searchMethod ?? "by_name").Trim().ToLowerInvariant();
            if (normalized == "by_id") return gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == searchTerm;
            if (normalized == "by_path") return string.Equals(GameObjectToolSupport.HierarchyPath(gameObject), searchTerm, StringComparison.Ordinal);
            if (normalized == "by_tag") return string.Equals(gameObject.tag, searchTerm, StringComparison.Ordinal);
            if (normalized == "by_layer") return gameObject.layer == GameObjectToolSupport.ParseLayer(searchTerm);
            if (normalized == "by_component") { var type = GameObjectToolSupport.ResolveComponentType(searchTerm); return type != null && gameObject.GetComponent(type) != null; }
            if (normalized != "by_name") throw new UnityCliBridge.BridgeCommandException("E_SEARCH_METHOD_INVALID", "Unsupported GameObject searchMethod: " + searchMethod, "Use by_name, by_tag, by_layer, by_component, by_path, or by_id.");
            return string.Equals(gameObject.name, searchTerm, StringComparison.Ordinal);
        }

        private static GoFindItem BuildItem(GameObject gameObject)
        {
            var componentNames = new List<string>();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                componentNames.Add(component != null ? component.GetType().Name : "MissingScript");
            }
            return new GoFindItem
            {
                Id = gameObject.GetInstanceID(),
                Name = gameObject.name,
                Path = GameObjectToolSupport.HierarchyPath(gameObject),
                Active = gameObject.activeSelf,
                ActiveInHierarchy = gameObject.activeInHierarchy,
                Tag = gameObject.tag,
                Layer = gameObject.layer,
                ChildCount = gameObject.transform.childCount,
                ChildrenTruncated = gameObject.transform.childCount > 0,
                ComponentCount = componentNames.Count,
                ComponentTypes = componentNames,
                Components = componentNames
            };
        }
    }
}
#endif
