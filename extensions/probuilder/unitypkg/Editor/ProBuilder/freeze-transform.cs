#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
namespace UnityCli.ProBuilder
{
    /// <summary>probuilder.freeze-transform — 将变换烘焙到网格顶点并重置变换。</summary>
    public class FreezeTransformTool : IUnityCliTool<FreezeTransformTool.Request, FreezeTransformTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }
        public sealed class Result { public long BakedVertexCount { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "probuilder.freeze-transform", Summary = "Bake a ProBuilder mesh scale into vertex positions and reset localScale.", Description = "Call ProBuilder 5.2.4 FreezeScaleTransform on a root object: bake localScale into every referenced raw vertex while preserving position and rotation, then set localScale to one.", Group = "probuilder", DestructiveHint = false, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 10000, MaxTimeoutMs = 30000, Examples = new[] { new ToolExample { Description = "烘焙变换到顶点", Input = new Request { Target = "MyCube" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var target = request.Target; var go = ProBuilderInput.ResolveTarget(target); if (go == null) throw ProBuilderErrors.TargetNotFound(target); var mesh = ProBuilderReflection.GetProBuilderMesh(go) as ProBuilderMesh; if (mesh == null) throw ProBuilderErrors.OperationFailed("freeze-transform", "No ProBuilderMesh on target."); var count = mesh.vertexCount;
            if (mesh.vertexCount < 1) throw ProBuilderErrors.OperationFailed("freeze-transform", "Mesh has no vertices.");
            if (go.transform.localScale == Vector3.one) return new System.Threading.Tasks.ValueTask<Result>(new Result { BakedVertexCount = 0 });
            var plan = ProBuilderAtomicReadback.CaptureScaleBake(go, mesh);
            if (plan == null) throw ProBuilderErrors.OperationFailed("freeze-transform", "Scale bake inputs are unreadable or non-finite.");
            var instanceId = go.GetInstanceID();
            ctx.CommitEffect();
            try
            {
                ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Freeze Transform", true);
                mesh.FreezeScaleTransform();
                mesh.ToMesh();
                mesh.Refresh();
                EditorSceneManager.MarkSceneDirty(go.scene);
                var freshGo = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                var fresh = ProBuilderReflection.GetProBuilderMesh(freshGo);
                if (fresh == null) throw ProBuilderErrors.OutcomeUnknown("freeze-transform", "readback", "Fresh target mesh is unreadable.");
                if (!ProBuilderAtomicReadback.ScaleWasBaked(freshGo, fresh, plan)) throw ProBuilderErrors.OutcomeUnknown("freeze-transform", "postcondition", "Scale was not baked into readable geometry.");
                return new System.Threading.Tasks.ValueTask<Result>(new Result { BakedVertexCount = count });
            }
            catch (Exception exception) when (!(exception is UnityCliBridge.BridgeCommandException))
            {
                throw ProBuilderErrors.OutcomeUnknown("freeze-transform", "invoke", exception.Message);
            }
        }
    }
}
#endif
