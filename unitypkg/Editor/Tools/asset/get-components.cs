#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// asset.get-components — 读取 Prefab/GameObject 资产的组件摘要,避免深读整个对象图。分页+过滤。
    /// </summary>
    public class AssetGetComponentsTool : IUnityCliTool<AssetGetComponentsTool.Request, AssetGetComponentsTool.Result>
    {
        public sealed class Request
        {
            public string AssetPath { get; set; }
            public string Path { get; set; }
            public string Target { get; set; }
            public bool? IncludeInactive { get; set; }
            public string Query { get; set; }
            public string NameFilter { get; set; }
            public string PathFilter { get; set; }
            public string ComponentType { get; set; }
            public int? PageSize { get; set; }
            public int? Cursor { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string AssetPath { get; set; }
            public string Guid { get; set; }
            public string MainObjectType { get; set; }
            public AssetImporterInfo Importer { get; set; }
            public bool IsGameObjectAsset { get; set; }
            public bool IncludeInactive { get; set; }
            public string Query { get; set; }
            public string ComponentType { get; set; }
            public long ComponentCount { get; set; }
            public List<AssetComponentRow> Components { get; set; }
            public AssetPageInfo PageInfo { get; set; }
        }

        public sealed class AssetImporterInfo
        {
            public string Type { get; set; }
            public string AssetBundleName { get; set; }
            public string UserData { get; set; }
        }

        public sealed class AssetComponentRow
        {
            public string GameObject { get; set; }
            public string Path { get; set; }
            public bool Active { get; set; }
            public long ComponentIndex { get; set; }
            public string Type { get; set; }
            public string FullName { get; set; }
            public bool Enabled { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "asset.get-components",
            Summary = "List Prefab/GameObject asset component summaries with paging and name/path/component-type filters.",
            Description = "List component summaries for a Prefab/GameObject asset without deep-reading the whole object graph. Filters: query/nameFilter/pathFilter match GameObject name or relative path; componentType matches a component type name. Results are paged via cursor/pageSize.",
            Group = "asset",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List components in a prefab", Input = new Request { AssetPath = "Assets/Prefabs/Player.prefab", PageSize = 50 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var path = AssetToolSupport.RequireExistingAssetPath(request.AssetPath ?? request.Path ?? request.Target);
            var includeInactive = request.IncludeInactive ?? true;
            var pageSize = Math.Max(1, Math.Min(request.PageSize ?? 50, 500));
            var cursor = Math.Max(0, request.Cursor ?? 0);
            var query = request.Query ?? request.NameFilter ?? request.PathFilter;
            var componentType = request.ComponentType;
            var asset = AssetDatabase.LoadMainAssetAtPath(path);
            var importer = AssetImporter.GetAtPath(path);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var rows = new List<AssetComponentRow>();
            if (root != null)
            {
                foreach (var transform in root.GetComponentsInChildren<Transform>(includeInactive))
                {
                    var gameObject = transform.gameObject;
                    var relativePath = PrefabRelativePath(root.transform, transform);
                    if (!TextMatches(query, gameObject.name, relativePath)) continue;
                    var components = transform.GetComponents<Component>();
                    for (var index = 0; index < components.Length; index++)
                    {
                        if (!ComponentTypeMatches(components[index], componentType)) continue;
                        rows.Add(BuildRow(gameObject, components[index], index, relativePath));
                    }
                }
            }
            var page = new List<AssetComponentRow>();
            for (var i = cursor; i < rows.Count && page.Count < pageSize; i++)
            {
                page.Add(rows[i]);
            }
            long? nextCursor = cursor + page.Count < rows.Count ? (long?)(cursor + page.Count) : null;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "asset.get-components",
                AssetPath = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                MainObjectType = asset == null ? null : asset.GetType().FullName,
                Importer = new AssetImporterInfo
                {
                    Type = importer == null ? null : importer.GetType().FullName,
                    AssetBundleName = importer == null ? null : importer.assetBundleName,
                    UserData = importer == null ? null : importer.userData
                },
                IsGameObjectAsset = root != null,
                IncludeInactive = includeInactive,
                Query = query,
                ComponentType = componentType,
                ComponentCount = rows.Count,
                Components = page,
                PageInfo = new AssetPageInfo
                {
                    PageSize = pageSize,
                    Cursor = cursor,
                    NextCursor = nextCursor,
                    Total = rows.Count,
                    Truncated = nextCursor != null
                }
            });
        }

        // 构造 GameObject 在 Prefab root 下的相对路径
        private static string PrefabRelativePath(Transform root, Transform transform)
        {
            var stack = new Stack<string>();
            var current = transform;
            while (current != null)
            {
                stack.Push(current.name);
                if (current == root) break;
                current = current.parent;
            }
            return string.Join("/", stack.ToArray());
        }

        private static AssetComponentRow BuildRow(GameObject gameObject, Component component, int componentIndex, string relativePath)
        {
            return new AssetComponentRow
            {
                GameObject = gameObject.name,
                Path = relativePath,
                Active = gameObject.activeSelf,
                ComponentIndex = componentIndex,
                Type = component == null ? "MissingScript" : component.GetType().Name,
                FullName = component == null ? null : component.GetType().FullName,
                Enabled = component != null && GameObjectToolSupport.ComponentEnabled(component)
            };
        }

        // query 为空时匹配全部;否则对 name 或 relativePath 做大小写不敏感包含匹配
        private static bool TextMatches(string query, string name, string relativePath)
        {
            if (string.IsNullOrEmpty(query)) return true;
            return (name != null && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                || (relativePath != null && relativePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // componentType 为空时匹配全部;否则按类型名大小写不敏感匹配(缺失脚本视作 MissingScript)
        private static bool ComponentTypeMatches(Component component, string componentType)
        {
            if (string.IsNullOrEmpty(componentType)) return true;
            var name = component == null ? "MissingScript" : component.GetType().Name;
            return string.Equals(name, componentType, StringComparison.OrdinalIgnoreCase);
        }
    }
}
#endif
