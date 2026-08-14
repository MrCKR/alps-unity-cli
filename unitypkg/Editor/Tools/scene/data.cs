#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.data — 参数化 Scene 深读:maxDepth/nameFilter/includeComponents/maxResults。
    /// </summary>
    public class SceneDataTool : IUnityCliTool<SceneDataTool.Request, SceneDataTool.Result>
    {
        public sealed class Request
        {
            public int? MaxDepth { get; set; }
            public int? MaxResults { get; set; }
            public string NameFilter { get; set; }
            public bool IncludeComponents { get; set; }
        }

        public sealed class Result
        {
            public SceneInfo Scene { get; set; }
            public long Count { get; set; }
            public List<DataItem> Items { get; set; }
        }

        public sealed class DataItem
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public long Depth { get; set; }
            public long ChildCount { get; set; }
            public List<string> Components { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.data",
            Summary = "Deep-read active scene hierarchy with maxDepth/nameFilter; prefer scene.hierarchy for paged navigation.",
            Description = "Parameterized deep read of the active scene hierarchy: maxDepth, nameFilter, includeComponents, maxResults. Read-only; for paged lightweight state use scene.hierarchy or tools run scene.hierarchy.",
            Group = "scene",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Read scene data with filter", Input = new Request { NameFilter = "Camera", MaxDepth = 2 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var maxDepth = Math.Max(1, Math.Min(request.MaxDepth ?? 2, 6));
            var maxResults = Math.Max(1, Math.Min(request.MaxResults ?? 200, 2000));
            var nameFilter = request.NameFilter;
            var includeComponents = request.IncludeComponents;
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects();
            var items = new List<DataItem>();
            var count = 0;
            foreach (var root in roots)
            {
                if (count >= maxResults) break;
                CollectSceneData(root.transform, 0, maxDepth, nameFilter, includeComponents, items, ref count, maxResults);
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Scene = SceneToolSupport.BuildSceneInfo(scene),
                Count = count,
                Items = items
            });
        }

        // 递归收集场景节点摘要,受 maxDepth/maxResults/nameFilter 约束。
        private static void CollectSceneData(Transform t, int depth, int maxDepth, string nameFilter, bool includeComponents, List<DataItem> items, ref int count, int maxResults)
        {
            if (count >= maxResults) return;
            if (string.IsNullOrEmpty(nameFilter) || t.name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var item = new DataItem
                {
                    Name = t.name,
                    Path = TransformPath(t),
                    Depth = depth,
                    ChildCount = t.childCount
                };
                if (includeComponents)
                {
                    var componentNames = new List<string>();
                    foreach (var c in t.GetComponents<Component>())
                    {
                        if (c != null) componentNames.Add(c.GetType().Name);
                    }
                    item.Components = componentNames;
                }
                items.Add(item);
                count++;
            }
            if (depth < maxDepth)
            {
                for (var i = 0; i < t.childCount; i++)
                {
                    CollectSceneData(t.GetChild(i), depth + 1, maxDepth, nameFilter, includeComponents, items, ref count, maxResults);
                }
            }
        }

        private static string TransformPath(Transform t)
        {
            var path = t.name;
            var p = t.parent;
            while (p != null)
            {
                path = p.name + "/" + path;
                p = p.parent;
            }
            return path;
        }
    }
}
#endif
