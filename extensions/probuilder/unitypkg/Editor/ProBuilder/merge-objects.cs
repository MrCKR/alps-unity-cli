#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.merge-objects — 将多个 GameObject 合并为单个 ProBuilder 对象。
    /// </summary>
    public class MergeObjectsTool : IUnityCliTool<MergeObjectsTool.Request, MergeObjectsTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public List<string> Targets { get; set; }
            public string Name { get; set; }                         // 可选,默认 ProBuilder_Merged
            public bool? AutoConvert { get; set; }                  // 可选,默认 true
        }

        public sealed class Result
        {
            public ObjectRef MergedObject { get; set; }
        }

        public sealed class ObjectRef
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.merge-objects",
            Summary = "Merge multiple GameObjects into a single ProBuilder mesh object.",
            Description = "Merge multiple GameObjects into a single ProBuilder object. Requires at least 2 targets. Optionally auto-convert non-ProBuilder meshes before merging. Falls back to Unity Mesh.CombineMeshes when the ProBuilder CombineMeshes API is unavailable.",
            Group = "probuilder",
            DestructiveHint = true,
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 15000,
            MaxTimeoutMs = 60000,
            SupportsJob = true,
            ArtifactKinds = new[] { "json" },
            Examples = new[]
            {
                new ToolExample { Description = "合并两个对象", Input = new Request { Targets = new List<string> { "Cube1", "Cube2" }, Name = "Merged" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var targets = ProBuilderInput.ResolveTargets(ctx.InputJson);
            if (targets == null || targets.Length < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 targets required for merge-objects.");

            var name = request.Name ?? "ProBuilder_Merged";
            var autoConvert = request.AutoConvert ?? true;

            // 收集或转换 ProBuilder 网格
            var pbMeshes = new List<object>();
            var nonPbObjects = new List<GameObject>();

            foreach (var go in targets)
            {
                var pbMesh = ProBuilderReflection.GetProBuilderMesh(go);
                if (pbMesh != null)
                {
                    pbMeshes.Add(pbMesh);
                }
                else if (autoConvert)
                {
                    nonPbObjects.Add(go);
                }
                else
                {
                    throw ProBuilderErrors.OperationFailed("merge-objects", $"GameObject '{go.name}' does not have a ProBuilderMesh component and autoConvert is disabled.");
                }
            }

            // 转换非 ProBuilder 对象
            foreach (var go in nonPbObjects)
            {
                var converted = ProBuilderReflection.ConvertToProBuilderMesh(go);
                if (converted == null)
                    throw ProBuilderErrors.OperationFailed("merge-objects", $"Failed to convert '{go.name}' to ProBuilder mesh.");
                pbMeshes.Add(converted);
            }

            if (pbMeshes.Count < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 ProBuilder meshes required after conversion.");

            Undo.RecordObject(targets[0], "ProBuilder Merge Objects");

            // 用 ProBuilder CombineMeshes.Combine 反射调用
            var merged = ProBuilderReflection.CombineMeshesViaReflection(pbMeshes.ToArray(), pbMeshes[0]);
            if (!merged)
            {
                // 回退到 Unity Mesh.CombineMeshes
                var mergedGo = new GameObject(name);
                var mf = mergedGo.AddComponent<MeshFilter>();
                var mr = mergedGo.AddComponent<MeshRenderer>();
                var instances = new List<CombineInstance>();
                foreach (var go in targets)
                {
                    var filter = go.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null)
                        instances.Add(new CombineInstance { mesh = filter.sharedMesh, transform = go.transform.localToWorldMatrix });
                }
                if (instances.Count > 0)
                {
                    var mesh = new Mesh();
                    mesh.CombineMeshes(instances.ToArray());
                    mf.sharedMesh = mesh;
                }
                ProBuilderReflection.AddProBuilderMesh(mergedGo);
                Undo.RegisterCreatedObjectUndo(mergedGo, "ProBuilder Merge Objects");
                EditorSceneManager.MarkSceneDirty(mergedGo.scene);

                return new Result
                {
                    MergedObject = new ObjectRef
                    {
                        Id = mergedGo.GetInstanceID(),
                        Name = mergedGo.name,
                        Path = HierarchyPath(mergedGo)
                    }
                };
            }

            ProBuilderReflection.RefreshMesh(pbMeshes[0]);
            var firstGo = targets[0];
            if (!string.IsNullOrEmpty(name)) firstGo.name = name;
            EditorSceneManager.MarkSceneDirty(firstGo.scene);

            return new Result
            {
                MergedObject = new ObjectRef
                {
                    Id = firstGo.GetInstanceID(),
                    Name = firstGo.name,
                    Path = HierarchyPath(firstGo)
                }
            };
        }

        private static string HierarchyPath(GameObject go)
        {
            var names = new Stack<string>();
            var cur = go.transform;
            while (cur != null) { names.Push(cur.name); cur = cur.parent; }
            return string.Join("/", names.ToArray());
        }
    }
}
#endif
