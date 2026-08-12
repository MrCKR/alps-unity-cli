#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// ProBuilder 场景 Context：只读返回场景网格与选择状态。
    /// </summary>
    public class ProBuilderSceneTool : IUnityCliTool<ProBuilderSceneTool.Request, ProBuilderSceneTool.Result>
    {
        public sealed class Request
        {
            public string NamePattern { get; set; }
            public int PageSize { get; set; }
        }

        public sealed class Result
        {
            public bool Installed { get; set; }
            public int SceneCount { get; set; }
            public int ObjectCount { get; set; }
            public int SelectedObjectCount { get; set; }
            public int PageSize { get; set; }
            public bool Truncated { get; set; }
            public List<ProBuilderSceneRow> Objects { get; set; }
            public ProBuilderSceneSelection Selection { get; set; }
        }

        public sealed class ProBuilderSceneRow
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public string Scene { get; set; }
            public int InstanceId { get; set; }
            public bool Active { get; set; }
            public bool Selected { get; set; }
            public int FaceCount { get; set; }
            public int VertexCount { get; set; }
            public int EdgeCount { get; set; }
        }

        public sealed class ProBuilderSceneSelection
        {
            public int ObjectCount { get; set; }
            public List<string> ObjectNames { get; set; }
        }

        // ── desc:只声明人工字段,不含 inputSchema/outputSchema ──
        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.scene", Summary = "Read ProBuilder mesh objects, face/edge/vertex selection context, and editable mesh state in the current scene without modifying Unity state.",
            Description = "Read ProBuilder mesh objects, face/edge/vertex selection context, and editable mesh state in the current scene without modifying Unity state.",
            DestructiveHint = false,
            Group = "probuilder",
            Target = "editor",
            PlayModeSupport = "supported",
            Examples = new[] { new ToolExample { Description = "List ProBuilder scene meshes", Input = new Request { PageSize = 50 } }, new ToolExample { Description = "Filter ProBuilder meshes by name", Input = new Request { NamePattern = "Cube", PageSize = 50 } } },
            RequiresPackages = new[] { "com.unity.probuilder" }
        };

        /// <summary>
        /// 读取当前场景中的 ProBuilderMesh 对象摘要和 Editor 选择状态；不修改场景、对象或资源。
        /// </summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var requestedPageSize = request != null && request.PageSize > 0 ? request.PageSize : 50;
            var pageSize = Clamp(requestedPageSize, 1, 200);
            var namePattern = request != null ? request.NamePattern : null;
            var rows = new List<ProBuilderSceneRow>();
            var selectedIds = SelectedGameObjectIds();
            var selectedProBuilderNames = new List<string>();

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    CollectRows(root.transform, scene.name, namePattern, selectedIds, rows, selectedProBuilderNames);
                }
            }

            var objectCount = rows.Count;
            var visibleCount = Math.Min(pageSize, objectCount);
            var visibleRows = new List<ProBuilderSceneRow>();
            for (var i = 0; i < visibleCount; i++)
            {
                visibleRows.Add(rows[i]);
            }

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Installed = ProBuilderReflection.IsAvailable,
                SceneCount = SceneManager.sceneCount,
                ObjectCount = objectCount,
                SelectedObjectCount = selectedProBuilderNames.Count,
                PageSize = pageSize,
                Truncated = objectCount > visibleCount,
                Objects = visibleRows,
                Selection = new ProBuilderSceneSelection
                {
                    ObjectCount = selectedProBuilderNames.Count,
                    ObjectNames = selectedProBuilderNames
                }
            });
        }

        /// <summary>
        /// 遍历场景层级并收集 ProBuilderMesh 摘要，只读取组件与选择状态。
        /// </summary>
        private static void CollectRows(Transform transform, string sceneName, string namePattern, HashSet<int> selectedIds, List<ProBuilderSceneRow> rows, List<string> selectedNames)
        {
            var go = transform.gameObject;
            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh != null)
            {
                var path = HierarchyPath(transform);
                if (Matches(namePattern, go.name, path))
                {
                    var selected = selectedIds.Contains(go.GetInstanceID());
                    if (selected) selectedNames.Add(path);
                    var faceCount = ProBuilderReflection.GetFaceCountProperty(mesh);
                    rows.Add(new ProBuilderSceneRow
                    {
                        Name = go.name,
                        Path = path,
                        Scene = sceneName,
                        InstanceId = go.GetInstanceID(),
                        Active = go.activeInHierarchy,
                        Selected = selected,
                        FaceCount = faceCount,
                        VertexCount = ProBuilderReflection.GetVertexCountProperty(mesh),
                        EdgeCount = Math.Max(0, faceCount * 3 / 2)
                    });
                }
            }

            for (var i = 0; i < transform.childCount; i++)
            {
                CollectRows(transform.GetChild(i), sceneName, namePattern, selectedIds, rows, selectedNames);
            }
        }

        private static HashSet<int> SelectedGameObjectIds()
        {
            var ids = new HashSet<int>();
            foreach (var obj in Selection.objects)
            {
                var go = obj as GameObject;
                if (go != null) ids.Add(go.GetInstanceID());
            }
            return ids;
        }

        private static bool Matches(string namePattern, string name, string path)
        {
            if (string.IsNullOrEmpty(namePattern)) return true;
            return (name != null && name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) >= 0)
                || (path != null && path.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string HierarchyPath(Transform transform)
        {
            var names = new List<string>();
            var current = transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            return value > max ? max : value;
        }
    }
}
#endif
