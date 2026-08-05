#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
namespace UnityCli.ProBuilder
{
    public class CombineMeshesTool : IUnityCliTool<CombineMeshesTool.Request, CombineMeshesTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public List<string> Targets { get; set; } public string Name { get; set; } }
        public sealed class Result { public ObjectRef CombinedObject { get; set; } public long SourceCount { get; set; } }
        public sealed class ObjectRef { public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.combine-meshes",
            Summary = "Combine ProBuilder meshes into the first target.",
            Description = "Combine at least two unique ProBuilder mesh targets into the first target.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 15000,
            MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "合并两个 strict-domain 三角形", Input = new Request { Name = "CombinedTriangles", Targets = new List<string> { "TriangleA", "TriangleB" } } }
            }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable(); var targets = ProBuilderInput.ResolveTargets(request.Targets); var name = request.Name ?? "ProBuilder_Combined";
            if (targets == null || request.Targets == null || targets.Length != request.Targets.Count || targets.Length < 2 || string.IsNullOrWhiteSpace(name) || targets.Select(value => value.GetInstanceID()).Distinct().Count() != targets.Length) throw ProBuilderErrors.SchemaInvalid("combine-meshes requires at least 2 unique targets and a non-empty name.");
            var meshes = targets.Select(ProBuilderReflection.GetProBuilderMesh).ToArray();
            if (meshes.Any(value => value == null) || meshes.Any(value => ProBuilderReflection.GetFaceCount(value) < 1)) throw ProBuilderErrors.SchemaInvalid("Every target must have readable ProBuilder geometry.");
            var totalVertices = meshes.Sum(ProBuilderReflection.GetVertexCount);
            if (totalVertices < 1 || totalVertices >= ProBuilderMesh.maxVertexCount) throw ProBuilderErrors.SchemaInvalid("Combined raw vertex count must remain below ProBuilderMesh.maxVertexCount.");
            var ids = targets.Select(value => value.GetInstanceID()).ToArray();
            var minimumFaces = meshes.Sum(ProBuilderReflection.GetFaceCount);
            ctx.CommitEffect();
            try
            {
                foreach (var index in Enumerable.Range(0, targets.Length))
                    ProBuilderAtomicReadback.RegisterMeshUndo(targets[index], meshes[index], "ProBuilder Combine Meshes", false, targets[index].GetComponent<Renderer>());
                if (!ProBuilderReflection.CombineMeshesViaReflection(meshes, meshes[0])) throw ProBuilderErrors.OutcomeUnknown("combine-meshes", "invoke", "CombineMeshes.Combine failed.");
                foreach (var source in targets.Skip(1)) Undo.DestroyObjectImmediate(source);
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("combine-meshes", "invoke", ex.Message); }
            try { if (!ProBuilderReflection.RefreshMesh(meshes[0])) throw ProBuilderErrors.OutcomeUnknown("combine-meshes", "readback", "ProBuilder refresh failed."); var fresh = EditorUtility.InstanceIDToObject(ids[0]) as GameObject; if (fresh == null) throw ProBuilderErrors.OutcomeUnknown("combine-meshes", "readback", "Combined target identity could not be resolved."); fresh.name = name; EditorSceneManager.MarkSceneDirty(fresh.scene); var freshMesh = ProBuilderReflection.GetProBuilderMesh(fresh); if (fresh.name != name || !ProBuilderAtomicReadback.SourcesWereRemovedAndGeometryIncluded(fresh, freshMesh, ids, minimumFaces)) throw ProBuilderErrors.OutcomeUnknown("combine-meshes", "postcondition", "Combined geometry or source removal could not be confirmed."); return new System.Threading.Tasks.ValueTask<Result>(new Result { CombinedObject = new ObjectRef { Id = fresh.GetInstanceID(), Name = fresh.name, Path = HierarchyPath(fresh) }, SourceCount = targets.Length }); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("combine-meshes", "readback", ex.Message); }
        }
        private static string HierarchyPath(GameObject go) { var names = new Stack<string>(); for (var current = go.transform; current != null; current = current.parent) names.Push(current.name); return string.Join("/", names.ToArray()); }
    }
}
#endif
