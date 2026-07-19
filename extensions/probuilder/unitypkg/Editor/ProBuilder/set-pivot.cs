#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.set-pivot — 将 ProBuilder 网格轴心设到指定位置。
    /// </summary>
    public class SetPivotTool : IUnityCliTool<SetPivotTool.Request, SetPivotTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public double? X { get; set; }                           // 可选,默认当前 position.x
            public double? Y { get; set; }                           // 可选,默认当前 position.y
            public double? Z { get; set; }                           // 可选,默认当前 position.z
        }

        public sealed class Result
        {
            public Vec3 NewPivotPosition { get; set; }
        }

        public sealed class Vec3
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.set-pivot",
            Summary = "Set the pivot point of a ProBuilder mesh to a specific position.",
            Description = "Set the pivot point of a ProBuilder mesh to a specific X/Y/Z position. When a coordinate is omitted, the current transform position component is used.",
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
                new ToolExample { Description = "将轴心设到原点", Input = new Request { Target = "MyCube", X = 0, Y = 0, Z = 0 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("set-pivot", "No ProBuilderMesh on target.");

            var pos = go.transform.position;
            var x = request.X.HasValue ? ProBuilderOperationProof.CheckedFloat(request.X.Value, "x") : pos.x;
            var y = request.Y.HasValue ? ProBuilderOperationProof.CheckedFloat(request.Y.Value, "y") : pos.y;
            var z = request.Z.HasValue ? ProBuilderOperationProof.CheckedFloat(request.Z.Value, "z") : pos.z;
            var newPivot = new Vector3(x, y, z);
            if (go.transform.position == newPivot)
                throw ProBuilderErrors.OperationFailed("set-pivot", "Requested pivot already matches the readable transform position.");
            ctx.CommitEffect();
            Undo.RecordObject(go.transform, "ProBuilder Set Pivot");
            go.transform.position = newPivot;
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshGo = ProBuilderInput.ResolveTarget(request.Target);
            if (!ProBuilderRequestProof.TransformMatchesRequestedPivot(freshGo, newPivot))
                throw ProBuilderErrors.OperationFailed("set-pivot", "Pivot did not match the requested transform position.");

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                NewPivotPosition = new Vec3 { X = x, Y = y, Z = z }
            });
        }
    }
}
#endif
