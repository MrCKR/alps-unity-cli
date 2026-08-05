#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace UnityCli.ProBuilder
{
    public class SetFaceMaterialTool : IUnityCliTool<SetFaceMaterialTool.Request, SetFaceMaterialTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } public List<long> FaceIndices { get; set; } [UnityCliRequired] public string MaterialPath { get; set; } }
        public sealed class Result { public long AssignedFaceCount { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "probuilder.set-face-material", Summary = "Assign a material asset to selected faces of a ProBuilder mesh.", Description = "Assign a material asset to selected faces.", Group = "probuilder", DestructiveHint = false, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 10000, MaxTimeoutMs = 30000, Examples = new[] { new ToolExample { Description = "为面 0 分配材质", Input = new Request { Target = "MyTwoMaterialMesh", FaceIndices = new List<long> { 0 }, MaterialPath = "Assets/Materials/Existing.mat" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var target = request.Target; var go = ProBuilderInput.ResolveTarget(target); if (go == null) throw ProBuilderErrors.TargetNotFound(target); var mesh = ProBuilderReflection.GetProBuilderMesh(go); if (mesh == null) throw ProBuilderErrors.OperationFailed("set-face-material", "No ProBuilderMesh on target."); var material = string.IsNullOrEmpty(request.MaterialPath) ? null : AssetDatabase.LoadAssetAtPath<Material>(request.MaterialPath); if (material == null) throw ProBuilderErrors.OperationFailed("set-face-material", "Material not found at path: " + request.MaterialPath);
            var indexes = request.FaceIndices?.Select(value => ProBuilderOperationProof.CheckedInt(value, "faceIndices")).ToArray(); if (indexes == null || indexes.Length == 0) indexes = Enumerable.Range(0, ProBuilderReflection.GetFaceCount(mesh)).ToArray(); var faces = ProBuilderReflection.GetFacesByIndices(mesh, indexes); if (faces == null || indexes.Distinct().Count() != indexes.Length || go.GetComponent<Renderer>() == null) throw ProBuilderErrors.SchemaInvalid("faceIndices must identify unique valid faces and the target must have a renderer."); if (ProBuilderAtomicReadback.RequestedFacesUseMaterial(mesh, indexes, material)) return new System.Threading.Tasks.ValueTask<Result>(new Result { AssignedFaceCount = 0 });
            ctx.CommitEffect(); try { ProBuilderAtomicReadback.RegisterMeshUndo(go, mesh, "ProBuilder Set Face Material", false, go.GetComponent<Renderer>()); if (!ProBuilderReflection.SetFaceMaterial(mesh, faces, material)) throw ProBuilderErrors.OutcomeUnknown("set-face-material", "invoke", "ProBuilderMesh.SetMaterial failed."); if (!ProBuilderReflection.RefreshMesh(mesh)) throw ProBuilderErrors.OutcomeUnknown("set-face-material", "readback", "ProBuilder refresh failed after the effect."); EditorSceneManager.MarkSceneDirty(go.scene); var fresh = ProBuilderReflection.GetProBuilderMesh(EditorUtility.InstanceIDToObject(go.GetInstanceID()) as GameObject); if (fresh == null) throw ProBuilderErrors.OutcomeUnknown("set-face-material", "readback", "Fresh target mesh is unreadable."); if (!ProBuilderAtomicReadback.RequestedFacesUseMaterial(fresh, indexes, material)) throw ProBuilderErrors.OutcomeUnknown("set-face-material", "postcondition", "The requested faces do not use the requested material."); return new System.Threading.Tasks.ValueTask<Result>(new Result { AssignedFaceCount = faces.Length }); } catch (Exception exception) when (!(exception is UnityCliBridge.BridgeCommandException)) { throw ProBuilderErrors.OutcomeUnknown("set-face-material", "invoke", exception.Message); }
        }
    }
}
#endif
