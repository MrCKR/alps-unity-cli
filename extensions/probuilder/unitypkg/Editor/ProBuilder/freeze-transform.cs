#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.freeze-transform — 将变换烘焙到网格顶点并重置变换。
    /// </summary>
    public class FreezeTransformTool : IUnityCliTool<FreezeTransformTool.Request, FreezeTransformTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
        }

        public sealed class Result
        {
            public long BakedVertexCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.freeze-transform",
            Summary = "Bake a ProBuilder mesh transform into vertex positions, resetting the transform to identity.",
            Description = "Bake the current transform into the ProBuilder mesh vertices, then reset the transform to identity (position zero, rotation identity, scale one). Vertex positions are transformed to world space before the reset.",
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
                new ToolExample { Description = "烘焙变换到顶点", Input = new Request { Target = "MyCube" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("freeze-transform", "No ProBuilderMesh on target.");

            var vertexCount = ProBuilderReflection.GetVertexCount(mesh);
            Undo.RecordObject(go, "ProBuilder Freeze Transform");

            // 将顶点位置变换到世界空间,重置变换,再写回(此时 local = world)
            try
            {
                var positions = ProBuilderReflection.GetPositions(mesh);
                if (positions != null)
                {
                    var posList = new List<Vector3>(positions);
                    for (int i = 0; i < posList.Count; i++)
                        posList[i] = go.transform.TransformPoint(posList[i]);

                    // 重置变换
                    go.transform.position = Vector3.zero;
                    go.transform.rotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;

                    // 写回位置(变换为 identity 后 local = world)
                    var positionsProp = ProBuilderReflection.GetProBuilderMeshType()?.GetProperty("positions");
                    if (positionsProp != null && positionsProp.CanWrite)
                        positionsProp.SetValue(mesh, posList);
                    else
                    {
                        var setPositionsMethod = ProBuilderReflection.GetProBuilderMeshType()?.GetMethod("SetPositions",
                            BindingFlags.Instance | BindingFlags.Public);
                        if (setPositionsMethod != null)
                            setPositionsMethod.Invoke(mesh, new object[] { posList.ToArray() });
                    }
                }
            }
            catch (System.Exception) { }

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new System.Threading.Tasks.ValueTask<Result>(new Result { BakedVertexCount = vertexCount });
        }
    }
}
#endif
