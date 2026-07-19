#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.repair-mesh — 修复 ProBuilder 网格(移除退化三角形与未用顶点)。
    /// </summary>
    public class RepairMeshTool : IUnityCliTool<RepairMeshTool.Request, RepairMeshTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
        }

        public sealed class Result
        {
            public long RemovedDegenerateIndexCount { get; set; }
            public long RemovedUnusedVertexCount { get; set; }
            public long RemainingDegenerateFaceCount { get; set; }
            public long RemainingUnusedVertexCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.repair-mesh",
            Summary = "Repair a ProBuilder mesh by removing degenerate triangles and unused vertices.",
            Description = "Attempt to repair a ProBuilder mesh by removing degenerate triangles and unused vertices. Reports counts of removed issues and any remaining issues after repair.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            SupportsJob = true,
            ArtifactKinds = new[] { "json" },
            Examples = new[]
            {
                new ToolExample { Description = "修复网格", Input = new Request { Target = "MyCube" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("repair-mesh", "No ProBuilderMesh on target.");
            var beforeDegenerateFaceCount = ProBuilderReflection.CountDegenerateFaces(mesh);
            var beforeUnusedVertexCount = ProBuilderReflection.CountUnusedVertices(mesh);
            if (beforeDegenerateFaceCount < 0 || beforeUnusedVertexCount < 0)
                throw ProBuilderErrors.OperationFailed("repair-mesh", "Failed to read repairable mesh state.");
            if (beforeDegenerateFaceCount == 0 && beforeUnusedVertexCount == 0)
                throw ProBuilderErrors.OperationFailed("repair-mesh", "Mesh has no readable degenerate triangles or unused vertices to repair.");
            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Repair Mesh");

            var removedDegenerateIndexCount = ProBuilderReflection.RemoveDegenerateTriangles(mesh);
            var removedUnusedVertexCount = ProBuilderReflection.RemoveUnusedVertices(mesh);
            if (removedDegenerateIndexCount < 0 || removedUnusedVertexCount < 0)
                throw ProBuilderErrors.OperationFailed("repair-mesh", "ProBuilder 5.x MeshValidation repair APIs failed.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var freshGo = ProBuilderInput.ResolveTarget(request.Target);
            var freshMesh = ProBuilderReflection.GetProBuilderMesh(freshGo);
            var remainingDegenerateFaceCount = ProBuilderReflection.CountDegenerateFaces(freshMesh);
            var remainingUnusedVertexCount = ProBuilderReflection.CountUnusedVertices(freshMesh);
            if (!ProBuilderRequestProof.RepairReducedInvalidGeometry(freshMesh, beforeDegenerateFaceCount, beforeUnusedVertexCount))
                throw ProBuilderErrors.OperationFailed("repair-mesh", "Repair did not reduce readable invalid geometry.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                RemovedDegenerateIndexCount = removedDegenerateIndexCount,
                RemovedUnusedVertexCount = removedUnusedVertexCount,
                RemainingDegenerateFaceCount = remainingDegenerateFaceCount,
                RemainingUnusedVertexCount = remainingUnusedVertexCount
            });
        }
    }
}
#endif
