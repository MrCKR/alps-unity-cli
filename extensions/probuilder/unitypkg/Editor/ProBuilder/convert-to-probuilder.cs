#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
namespace UnityCli.ProBuilder
{
    public class ConvertToProBuilderTool : IUnityCliTool<ConvertToProBuilderTool.Request, ConvertToProBuilderTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public string Target { get; set; } }
        public sealed class Result { public bool Converted { get; set; } public long VertexCount { get; set; } public long FaceCount { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.convert-to-probuilder",
            Summary = "Convert a Unity mesh into a ProBuilder mesh.",
            Description = "Convert a MeshFilter with readable vertices and triangle indices into a ProBuilder mesh.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "转换普通网格", Input = new Request { Target = "MyPrimitive" } }
            }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var go = ProBuilderInput.ResolveTarget(request.Target); if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target); if (ProBuilderReflection.GetProBuilderMesh(go) != null) throw ProBuilderErrors.OperationFailed("convert-to-probuilder", "Target already has a ProBuilderMesh component.");
            var source = ProBuilderAtomicReadback.CaptureSourceMeshGeometry(go.GetComponent<MeshFilter>()); if (source == null) throw ProBuilderErrors.SchemaInvalid("Target requires a MeshFilter with readable vertices and triangle indices."); var id = go.GetInstanceID(); ctx.CommitEffect(); object converted;
            try { ProBuilderAtomicReadback.RegisterMeshUndo(go, null, "ProBuilder Convert to ProBuilder", false, go.GetComponent<MeshFilter>(), go.GetComponent<Renderer>()); converted = ProBuilderReflection.ConvertToProBuilderMesh(go); if (converted == null) throw ProBuilderErrors.OutcomeUnknown("convert-to-probuilder", "invoke", "MeshImporter failed."); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("convert-to-probuilder", "invoke", ex.Message); }
            try { if (!ProBuilderReflection.RefreshMesh(converted)) throw ProBuilderErrors.OutcomeUnknown("convert-to-probuilder", "readback", "ProBuilder refresh failed."); EditorSceneManager.MarkSceneDirty(go.scene); var freshGo = EditorUtility.InstanceIDToObject(id) as GameObject; var fresh = ProBuilderReflection.GetProBuilderMesh(freshGo); var vertices = ProBuilderReflection.GetVertexCount(fresh); var faces = ProBuilderReflection.GetFaceCount(fresh); if (!ProBuilderAtomicReadback.ConvertedGeometryMatchesSource(fresh, source) || vertices < 1 || faces < 1) throw ProBuilderErrors.OutcomeUnknown("convert-to-probuilder", "postcondition", "Converted geometry could not be confirmed."); return new System.Threading.Tasks.ValueTask<Result>(new Result { Converted = true, VertexCount = vertices, FaceCount = faces }); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("convert-to-probuilder", "readback", ex.Message); }
        }
    }
}
#endif
