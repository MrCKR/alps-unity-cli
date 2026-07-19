#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
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
            Description = "Combine multiple ProBuilder meshes into exactly one mesh with the ProBuilder 5.x CombineMeshes API. Requires at least 2 targets, each with a ProBuilderMesh component, and fails if the result would require multiple meshes.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var targets = ProBuilderInput.ResolveTargets(request.Targets);
            if (request.Targets == null || targets == null || targets.Length != request.Targets.Count || targets.Length < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 unique, unambiguous targets required for combine-meshes.");

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
            var sourceIds = targets.Select(target => target.GetInstanceID()).ToArray();
            var minimumFaceCount = pbMeshes.Sum(ProBuilderReflection.GetFaceCount);
            if (minimumFaceCount < 2) throw ProBuilderErrors.OperationFailed("combine-meshes", "Source geometry is unreadable.");
            ctx.CommitEffect();
            Undo.RecordObject(targets[0], "ProBuilder Combine Meshes");

            var combined = ProBuilderReflection.CombineMeshesViaReflection(pbMeshes.ToArray(), pbMeshes[0]);
            if (!combined)
                throw ProBuilderErrors.OperationFailed("combine-meshes", "ProBuilder 5.x CombineMeshes.Combine did not return exactly one mesh.");

            ProBuilderReflection.RefreshMesh(pbMeshes[0]);
            var firstGo = targets[0];
            if (!string.IsNullOrEmpty(name)) firstGo.name = name;
            EditorSceneManager.MarkSceneDirty(firstGo.scene);
            var freshGo = ProBuilderInput.ResolveTarget(sourceIds[0].ToString());
            var freshMesh = ProBuilderReflection.GetProBuilderMesh(freshGo);
            if (freshGo == null || freshGo.name != name || !ProBuilderRequestProof.SourcesWereRemovedAndGeometryIncluded(freshGo, freshMesh, sourceIds, minimumFaceCount))
                throw ProBuilderErrors.OperationFailed("combine-meshes", "Sources did not disappear into a result containing their geometry.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                CombinedObject = new ObjectRef
                {
                    Id = firstGo.GetInstanceID(),
                    Name = firstGo.name,
                    Path = HierarchyPath(firstGo)
                },
                SourceCount = pbMeshes.Count
            });
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
