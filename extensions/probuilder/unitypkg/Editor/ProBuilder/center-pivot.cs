#if UNITY_EDITOR
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("center-pivot", "No ProBuilderMesh on target.");

            var oldPivot = go.transform.position;
            Undo.RecordObject(go.transform, "ProBuilder Center Pivot");

            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                var center = renderer.bounds.center;
                go.transform.position = center;
            }

            EditorSceneManager.MarkSceneDirty(go.scene);

            var newPivot = go.transform.position;
            var offset = newPivot - oldPivot;

            return new Result
            {
                PivotOffset = new Vec3 { X = offset.x, Y = offset.y, Z = offset.z },
                NewPosition = new Vec3 { X = newPivot.x, Y = newPivot.y, Z = newPivot.z }
            };
        }
    }
}
#endif
