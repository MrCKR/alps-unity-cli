#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.merge-vertices — 合并 ProBuilder 网格的选定顶点。
    /// </summary>
    public class MergeVerticesTool : IUnityCliTool<MergeVerticesTool.Request, MergeVerticesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            [UnityCliRequired] public List<long> VertexIndices { get; set; }
            public bool CollapseToFirst { get; set; }               // 默认 false
        }

        public sealed class Result
        {
            public bool Changed { get; set; }
            public string NoOpReason { get; set; }
            public long MergedToIndex { get; set; }
            public long SourceVertexCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.merge-vertices",
            Summary = "Merge selected vertices of a ProBuilder mesh into a single vertex.",
            Description = "Merge at least two selected vertices into one shared vertex. When collapseToFirst is true, the first selected vertex supplies the terminal position and payload; otherwise ProBuilder 5.2.4 averages the selected payload. An effectful merge must have initialized sharedTextures and no UV1 because MergeVertices dereferences the texture lookup and ToMesh clears UV1; an exact no-op returns before those APIs. The complete predicted raw, channel, world-position and triangle terminal must remain finite or the request is rejected before mutation.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "合并顶点 0 和 1", Input = new Request { Target = "MyCube", VertexIndices = new List<long> { 0, 1 } } }
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
            if (mesh == null) throw ProBuilderErrors.OperationFailed("merge-vertices", "No ProBuilderMesh on target.");
            var identity = ProBuilderOperationProof.CaptureIdentity(go);
            if (identity == null) throw ProBuilderErrors.SchemaInvalid("Target must belong to a valid loaded scene.");

            int[] vertexIndices = request.VertexIndices?.Select(i => ProBuilderOperationProof.CheckedInt(i, "vertexIndices")).ToArray();
            var vertexCount = ProBuilderReflection.GetVertexCount(mesh);
            if (vertexIndices == null || vertexIndices.Length < 2 || vertexIndices.Distinct().Count() != vertexIndices.Length)
                throw ProBuilderErrors.SchemaInvalid("At least 2 unique vertexIndices are required for merge-vertices.");
            if (vertexCount < 0 || vertexIndices.Any(index => index < 0 || index >= vertexCount))
                throw ProBuilderErrors.SchemaInvalid("vertexIndices contains an invalid vertex index.");
            var collapseToFirst = request.CollapseToFirst;
            var before = ProBuilderVertexObjectCompletionProof.CaptureMergeVertices(mesh, vertexIndices, collapseToFirst);
            if (before == null) throw ProBuilderErrors.OperationFailed("merge-vertices", "An effectful merge requires initialized sharedTextures, absent UV1, and a completely finite readable predicted terminal.");
            if (before.IsNoOp)
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Changed = false, NoOpReason = "expected-terminal-equals-before", MergedToIndex = before.ExpectedReturnedIndex, SourceVertexCount = vertexIndices.Length });
            ctx.CommitEffect();
            try
            {
            int mergedToIndex;
            try
            {
                Undo.RecordObject(go, "ProBuilder Merge Vertices");
                if (!ProBuilderReflection.TryMergeVerticesWithCollapse(mesh, vertexIndices, collapseToFirst, out mergedToIndex))
                    throw ProBuilderErrors.OutcomeUnknown("merge-vertices", "invoke", "ProBuilder 5.x VertexEditing.MergeVertices could not be invoked.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("merge-vertices", "invoke", exception.Message); }
            ProBuilderMesh freshMesh;
            try
            {
                if (!ProBuilderReflection.RefreshMeshPreservingVertexPayload(mesh)) throw ProBuilderErrors.OutcomeUnknown("merge-vertices", "readback", "ProBuilder mesh refresh failed after the effect.");
                EditorSceneManager.MarkSceneDirty(go.scene);
                freshMesh = ProBuilderOperationProof.ResolveFreshMesh(target, identity) as ProBuilderMesh;
                if (freshMesh == null) throw ProBuilderErrors.OutcomeUnknown("merge-vertices", "readback", "Fresh target identity could not be resolved.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("merge-vertices", "readback", exception.Message); }
            try
            {
                if (!ProBuilderVertexObjectCompletionProof.MergeVerticesMatches(before, freshMesh, mergedToIndex)) throw ProBuilderErrors.OutcomeUnknown("merge-vertices", "postcondition", "The complete ProBuilder 5.2.4 merge terminal state was not proven by fresh readback.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("merge-vertices", "postcondition", exception.Message); }
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Changed = true, MergedToIndex = mergedToIndex, SourceVertexCount = vertexIndices.Length });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("merge-vertices", "postcondition", exception.Message); }
        }
    }
}
#endif
