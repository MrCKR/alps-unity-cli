#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.auto-smooth — 按角度阈值自动平滑选定面。
    /// </summary>
    public class AutoSmoothTool : IUnityCliTool<AutoSmoothTool.Request, AutoSmoothTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public double? AngleThreshold { get; set; }             // 可选,默认 30
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
        }

        public sealed class Result
        {
            public long SmoothedFaceCount { get; set; }
            public bool Changed { get; set; }
            public string NoOpReason { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.auto-smooth",
            Summary = "Auto-apply smoothing groups to ProBuilder faces by an angle threshold.",
            Description = "Apply ProBuilder 5.2.4 smoothing groups to selected faces. angleThreshold defaults to 30 and is clamped to 0..89.999 degrees; omitted faceIndices selects all faces. Isolated or hard-disconnected selected faces end in smoothing group 0. changed=false means every selected face already had its exact final group.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "以 30 度阈值自动平滑全部面", Input = new Request { Target = "MyCube", AngleThreshold = 30 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var target = request.Target;
            var go = ProBuilderInput.ResolveTarget(target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("auto-smooth", "No ProBuilderMesh on target.");
            // Canonical snapshot owns the pre-effect identity claim.

            var angleThreshold = ProBuilderOperationProof.CheckedFloat(request.AngleThreshold ?? 30, "angleThreshold");
            // Smoothing.ApplySmoothingGroups clamps the threshold before calculating abs(cos).
            // Keep the request and both proof paths on the exact 5.2.4 domain.
            angleThreshold = Mathf.Clamp(angleThreshold, 0f, 89.999f);
            var faceCount = ProBuilderReflection.GetFaceCount(mesh);
            if (faceCount < 1)
                throw ProBuilderErrors.OperationFailed("auto-smooth", "Target has no readable faces.");
            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).ToArray();
            if (faceIndices == null || faceIndices.Length == 0)
                faceIndices = Enumerable.Range(0, faceCount).ToArray();
            ProBuilderOperationProof.ValidateFaceIndices(mesh, faceIndices);
            var plan = ProBuilderFaceCompletionProof.CaptureAutoSmooth(go, faceIndices, angleThreshold);
            if (plan == null) throw ProBuilderErrors.OperationFailed("auto-smooth", "Requested faces do not have an unambiguous readable 5.2.4 smoothing plan.");
            if (ProBuilderFaceCompletionProof.AutoSmoothAlreadyMatches(plan))
                return new System.Threading.Tasks.ValueTask<Result>(new Result { SmoothedFaceCount = 0, Changed = false, NoOpReason = "already-matches-angle-threshold" });
            var before = plan.Before;
            ctx.CommitEffect();
            try
            {
            Undo.RecordObject(go, "ProBuilder Auto Smooth");
            if (!ProBuilderReflection.ApplySmoothingGroups(mesh, faceIndices, angleThreshold))
                throw ProBuilderErrors.OutcomeUnknown("auto-smooth", "invoke", "ProBuilder 5.x Smoothing.ApplySmoothingGroups failed.");

            if (!ProBuilderReflection.RefreshMesh(mesh))
                throw ProBuilderErrors.OutcomeUnknown("auto-smooth", "refresh", "ProBuilder mesh refresh failed.");
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshMesh = CanonicalMeshSnapshot.ResolveFreshMesh(target, before.Identity);
            if (freshMesh == null)
                throw ProBuilderErrors.OutcomeUnknown("auto-smooth", "readback", "Fresh target identity could not be resolved.");
            var after = CanonicalMeshSnapshot.Capture(freshMesh.gameObject, Array.Empty<int>());
            if (after == null)
                throw ProBuilderErrors.OutcomeUnknown("auto-smooth", "readback", "Fresh canonical face geometry could not be read.");
            if (!ProBuilderFaceCompletionProof.AutoSmoothMatches(plan, after))
                throw ProBuilderErrors.OutcomeUnknown("auto-smooth", "postcondition", "Requested smoothing compatibility was not proven by fresh readback.");

            var smoothed = ProBuilderFaceCompletionProof.ExpectedSmoothedFaceCount(plan);
                return new System.Threading.Tasks.ValueTask<Result>(new Result { SmoothedFaceCount = smoothed, Changed = true });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("auto-smooth", "postcondition", exception.Message); }
        }
    }
}
#endif
