#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.combine-meshes — 将多个 ProBuilder 网格合并为一个。
    /// </summary>
    public class CombineMeshesTool : IUnityCliTool<CombineMeshesTool.Request, CombineMeshesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public List<string> Targets { get; set; }
            public string Name { get; set; }                         // 可选,默认 ProBuilder_Combined
        }

        public sealed class Result
        {
            public ObjectRef CombinedObject { get; set; }
            public long SourceCount { get; set; }
        }

        public sealed class ObjectRef
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.combine-meshes",
            Summary = "Combine multiple ProBuilder meshes into a single mesh.",
            Description = "Combine multiple ProBuilder meshes into a single mesh. Requires at least 2 targets, each with a ProBuilderMesh component. Falls back to Unity Mesh.CombineMeshes when the ProBuilder CombineMeshes API is unavailable.",
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
                new ToolExample { Description = "合并两个网格", Input = new Request { Targets = new List<string> { "Cube1", "Cube2" }, Name = "Combined" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var targets = ProBuilderInput.ResolveTargets(ctx.InputJson);
            if (targets == null || targets.Length < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 targets required for combine-meshes.");

            var name = request.Name ?? "ProBuilder_Combined";

            // 收集 ProBuilder 网格
            var pbMeshes = new List<object>();
            foreach (var go in targets)
            {
                var pbMesh = ProBuilderReflection.GetProBuilderMesh(go);
                if (pbMesh == null)
                    throw ProBuilderErrors.OperationFailed("combine-meshes", $"GameObject '{go.name}' does not have a ProBuilderMesh component.");
                pbMeshes.Add(pbMesh);
            }

            if (pbMeshes.Count < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 ProBuilder meshes required for combine-meshes.");

            Undo.RecordObject(targets[0], "ProBuilder Combine Meshes");

            var combined = ProBuilderReflection.CombineMeshesViaReflection(pbMeshes.ToArray(), pbMeshes[0]);
            if (!combined)
            {
                // 回退到 Unity API
                var combinedGo = new GameObject(name);
                var meshFilter = combinedGo.AddComponent<MeshFilter>();
                var meshRenderer = combinedGo.AddComponent<MeshRenderer>();
                var combineInstances = new List<CombineInstance>();
                var sources = new List<GameObject>();
                foreach (var go in targets)
                {
                    var mf = go.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null)
                    {
                        combineInstances.Add(new CombineInstance { mesh = mf.sharedMesh, transform = go.transform.localToWorldMatrix });
                        sources.Add(go);
                    }
                }
                if (combineInstances.Count > 0)
                {
                    var mesh = new Mesh();
                    mesh.CombineMeshes(combineInstances.ToArray());
                    meshFilter.sharedMesh = mesh;
                }
                ProBuilderReflection.AddProBuilderMesh(combinedGo);
                Undo.RegisterCreatedObjectUndo(combinedGo, "ProBuilder Combine Meshes");
                EditorSceneManager.MarkSceneDirty(combinedGo.scene);

                return new Result
                {
                    CombinedObject = new ObjectRef
                    {
                        Id = combinedGo.GetInstanceID(),
                        Name = combinedGo.name,
                        Path = HierarchyPath(combinedGo)
                    },
                    SourceCount = sources.Count
                };
            }

            // ProBuilder CombineMeshes 成功
            ProBuilderReflection.RefreshMesh(pbMeshes[0]);
            var firstGo = targets[0];
            if (!string.IsNullOrEmpty(name)) firstGo.name = name;
            EditorSceneManager.MarkSceneDirty(firstGo.scene);

            return new Result
            {
                CombinedObject = new ObjectRef
                {
                    Id = firstGo.GetInstanceID(),
                    Name = firstGo.name,
                    Path = HierarchyPath(firstGo)
                },
                SourceCount = pbMeshes.Count
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
