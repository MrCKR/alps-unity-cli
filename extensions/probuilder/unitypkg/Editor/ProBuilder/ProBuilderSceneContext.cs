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
    public static class ProBuilderSceneContext
    {
        /// <summary>
        /// 读取当前场景中的 ProBuilderMesh 对象摘要和 Editor 选择状态；不修改场景、对象或资源。
        /// </summary>
        [UnityCliContext(
            "probuilder.scene",
            Description = "Read ProBuilder mesh objects, face/edge/vertex selection context, and editable mesh state in the current scene without modifying Unity state.",
            Category = "probuilder",
            InputSchema = "{\"type\":\"object\",\"properties\":{\"namePattern\":{\"type\":\"string\",\"description\":\"Optional case-insensitive substring filter for ProBuilder GameObject names or hierarchy paths.\"},\"pageSize\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200,\"default\":50}}}",
            OutputSchema = "{\"type\":\"object\",\"properties\":{\"installed\":{\"type\":\"boolean\"},\"sceneCount\":{\"type\":\"integer\"},\"objectCount\":{\"type\":\"integer\"},\"selectedObjectCount\":{\"type\":\"integer\"},\"truncated\":{\"type\":\"boolean\"},\"objects\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"selection\":{\"type\":\"object\"}}}",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            RefreshBehavior = "onDemand",
            RequiresPackages = "com.unity.probuilder")]
        public static string Provide(UnityCliContextRequest request)
        {
            var pageSize = Clamp(ProBuilderInput.IntArg(request.InputJson, "pageSize", 50), 1, 200);
            var namePattern = ProBuilderInput.StringArg(request.InputJson, "namePattern");
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
            var writer = new UnityCliJsonWriter().BeginObject()
                .Prop("installed", ProBuilderReflection.IsAvailable)
                .Prop("sceneCount", SceneManager.sceneCount)
                .Prop("objectCount", objectCount)
                .Prop("selectedObjectCount", selectedProBuilderNames.Count)
                .Prop("pageSize", pageSize)
                .Prop("truncated", objectCount > visibleCount)
                .PropArray("objects", arrayWriter =>
                {
                    for (var i = 0; i < visibleCount; i++)
                    {
                        WriteRow(arrayWriter, rows[i]);
                    }
                })
                .PropObject("selection", selectionWriter =>
                {
                    selectionWriter
                        .Prop("objectCount", selectedProBuilderNames.Count)
                        .PropArray("objectNames", namesWriter =>
                        {
                            foreach (var name in selectedProBuilderNames) namesWriter.Value(name);
                        });
                });

            return writer.EndObject().ToString();
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
                    rows.Add(new ProBuilderSceneRow
                    {
                        Name = go.name,
                        Path = path,
                        Scene = sceneName,
                        InstanceId = go.GetInstanceID(),
                        Active = go.activeInHierarchy,
                        Selected = selected,
                        FaceCount = ProBuilderReflection.GetFaceCountProperty(mesh),
                        VertexCount = ProBuilderReflection.GetVertexCountProperty(mesh)
                    });
                }
            }

            for (var i = 0; i < transform.childCount; i++)
            {
                CollectRows(transform.GetChild(i), sceneName, namePattern, selectedIds, rows, selectedNames);
            }
        }

        private static void WriteRow(UnityCliJsonWriter writer, ProBuilderSceneRow row)
        {
            writer.BeginObject()
                .Prop("name", row.Name)
                .Prop("path", row.Path)
                .Prop("scene", row.Scene)
                .Prop("instanceId", row.InstanceId)
                .Prop("active", row.Active)
                .Prop("selected", row.Selected)
                .Prop("faceCount", row.FaceCount)
                .Prop("vertexCount", row.VertexCount)
                .Prop("edgeCount", Math.Max(0, row.FaceCount * 3 / 2))
                .EndObject();
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

        private sealed class ProBuilderSceneRow
        {
            public string Name;
            public string Path;
            public string Scene;
            public int InstanceId;
            public bool Active;
            public bool Selected;
            public int FaceCount;
            public int VertexCount;
        }
    }
}
#endif
