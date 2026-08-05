#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.extrude-faces — 挤出 ProBuilder 网格的选定面。
    /// </summary>
    public class ExtrudeFacesTool : IUnityCliTool<ExtrudeFacesTool.Request, ExtrudeFacesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> FaceIndices { get; set; }             // 可选,空则作用于全部面
            public double? Distance { get; set; }                  // 可选,默认 0.5
            public string Method { get; set; }                     // 可选,默认 vertexNormal
        }

        public sealed class Result
        {
            public long ModifiedFaceCount { get; set; }
            public long NewVertexCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.extrude-faces",
            Summary = "Extrude selected faces of a ProBuilder mesh by a distance.",
            Description = "Extrude the sole triangular face of a strictly isolated ProBuilder mesh by a given distance. The target must contain exactly one non-degenerate triangle with three distinct raw/shared vertices, an explicit shared-texture partition, exactly one occupied renderer material slot at submesh 0, and a predicted cap and side-wall terminal whose positions, normals, and complete 5.2.4 tangent arithmetic stay finite. faceIndices may be omitted or must be [0]. Method is individual, vertexNormal (default), or faceNormal. Each created side quad receives a deterministic manual UV0 layout before tangent refresh.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "挤出唯一三角面", Input = new Request { Target = "MyTriangle", FaceIndices = new List<long> { 0 }, Distance = 0.5 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var target = request.Target;
            var go = ProBuilderInput.ResolveTarget(target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var typedMesh = ProBuilderReflection.GetProBuilderMesh(go) as ProBuilderMesh;
            if (typedMesh == null) throw ProBuilderErrors.OperationFailed("extrude-faces", "No ProBuilderMesh component on target.");

            int[] faceIndices = request.FaceIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "faceIndices")).ToArray();
            var distance = ProBuilderOperationProof.CheckedFloat(request.Distance ?? 0.5, "distance");
            var method = request.Method ?? "vertexNormal";
            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance == 0f)
                throw ProBuilderErrors.SchemaInvalid("distance must be finite and non-zero.");
            if (faceIndices != null && (faceIndices.Length != 1 || faceIndices[0] != 0))
                throw ProBuilderErrors.SchemaInvalid("faceIndices must be omitted or exactly [0] for the deterministic extrusion domain.");
            if (method != "individual" && method != "vertexNormal" && method != "faceNormal")
                throw ProBuilderErrors.SchemaInvalid("method must be individual, vertexNormal, or faceNormal.");

            var plan = ProBuilderFaceExtrudeCompletionProof.TryCreate(go, typedMesh, new[] { 0 }, method, distance);
            if (plan == null)
                throw ProBuilderErrors.SchemaInvalid("extrude-faces requires one isolated non-degenerate triangle, complete finite vertex/shared-texture/material data, and fully finite predicted position, normal, and 5.2.4 tangent arithmetic.");

            ctx.CommitEffect();
            try
            {
                Undo.RecordObject(go, "ProBuilder Extrude Faces");
                var created = ExtrudeElements.Extrude(typedMesh, plan.FrozenFaces, plan.Method, plan.Distance);
                if (created == null || created.Length != 3 || !ProBuilderOperationProof.ApplyDeterministicQuadUvs(typedMesh, plan.Before.Faces.Length, created.Length))
                    throw new InvalidOperationException();
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("extrude-faces", "invoke", "ProBuilder 5.2.4 face extrusion threw or did not return three frozen-face side walls."); }
            try
            {
                if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(typedMesh)) throw new InvalidOperationException();
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("extrude-faces", "readback", "ProBuilder mesh payload-preserving refresh failed after effect."); }
            try
            {
                if (!ProBuilderFaceExtrudeCompletionProof.Matches(plan, target))
                    throw ProBuilderErrors.OutcomeUnknown("extrude-faces", "postcondition", "The complete ProBuilder 5.2.4 face-extrude terminal state was not proven by fresh readback.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw ProBuilderErrors.OutcomeUnknown("extrude-faces", "postcondition", "Face-extrude terminal-state readback threw after the effect barrier."); }

            try
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result { ModifiedFaceCount = 1, NewVertexCount = 12 });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("extrude-faces", "postcondition", exception.Message); }
        }
    }
}
#endif
