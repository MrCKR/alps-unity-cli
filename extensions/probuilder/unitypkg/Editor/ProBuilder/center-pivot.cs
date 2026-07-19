#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.center-pivot — 将轴心点居中到包围盒中心。
    /// </summary>
    public class CenterPivotTool : IUnityCliTool<CenterPivotTool.Request, CenterPivotTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
        }

        public sealed class Result
        {
            public Vec3 PivotOffset { get; set; }
            public Vec3 NewPosition { get; set; }
        }

        public sealed class Vec3
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.center-pivot",
            Summary = "Center a ProBuilder mesh pivot to its renderer bounds center.",
            Description = "Center the pivot point of a ProBuilder mesh to its bounds center. Requires a Renderer component on the target to compute bounds.",
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
                new ToolExample { Description = "居中轴心", Input = new Request { Target = "MyCube" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("center-pivot", "No ProBuilderMesh on target.");

            var oldPivot = go.transform.position;
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                throw ProBuilderErrors.OperationFailed("center-pivot", "Target has no Renderer to determine bounds center.");
            var expectedPivot = renderer.bounds.center;
            if (float.IsNaN(expectedPivot.x) || float.IsInfinity(expectedPivot.x) || float.IsNaN(expectedPivot.y) || float.IsInfinity(expectedPivot.y) || float.IsNaN(expectedPivot.z) || float.IsInfinity(expectedPivot.z))
                throw ProBuilderErrors.SchemaInvalid("Target bounds center must be finite.");
            var positionsProperty = ProBuilderReflection.GetProBuilderMeshType()?.GetProperty("positions", BindingFlags.Instance | BindingFlags.Public);
            var localPositions = positionsProperty?.GetValue(mesh) as IList<Vector3>;
            if (localPositions == null || !positionsProperty.CanWrite)
                throw ProBuilderErrors.OperationFailed("center-pivot", "ProBuilder positions are not readable and writable for world-geometry preservation.");
            var worldPositions = new List<Vector3>(localPositions.Count);
            foreach (var position in localPositions) worldPositions.Add(go.transform.TransformPoint(position));

            ctx.CommitEffect();
            Undo.RecordObject(go.transform, "ProBuilder Center Pivot");
            Undo.RecordObject(go, "ProBuilder Center Pivot Geometry");
            go.transform.position = expectedPivot;
            var recenteredLocalPositions = new List<Vector3>(worldPositions.Count);
            foreach (var worldPosition in worldPositions) recenteredLocalPositions.Add(go.transform.InverseTransformPoint(worldPosition));
            positionsProperty.SetValue(mesh, recenteredLocalPositions);
            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var newPivot = go.transform.position;
            var offset = newPivot - oldPivot;
            var freshGo = ProBuilderInput.ResolveTarget(request.Target);
            var freshMesh = ProBuilderReflection.GetProBuilderMesh(freshGo);
            if (!ProBuilderRequestProof.CenteredPivotPreservesWorldGeometry(freshGo, freshMesh, expectedPivot, worldPositions))
                throw ProBuilderErrors.OperationFailed("center-pivot", "Pivot was not centered while preserving every readable world-space vertex.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                PivotOffset = new Vec3 { X = offset.x, Y = offset.y, Z = offset.z },
                NewPosition = new Vec3 { X = newPivot.x, Y = newPivot.y, Z = newPivot.z }
            });
        }
    }
}
#endif
