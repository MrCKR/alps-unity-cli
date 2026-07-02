#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.repair-mesh — 修复 ProBuilder 网格(移除退化三角形与未用顶点)。
    /// </summary>
    public class RepairMeshTool : IUnityCliTool<RepairMeshTool.Request, RepairMeshTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
        }

        public sealed class Result
        {
            public long RemovedDegenerateCount { get; set; }
            public long FixedCount { get; set; }
            public long RemainingIssues { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.repair-mesh",
            Summary = "Repair a ProBuilder mesh by removing degenerate triangles and unused vertices.",
            Description = "Attempt to repair a ProBuilder mesh by removing degenerate triangles and unused vertices. Reports counts of removed issues and any remaining issues after repair.",
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
                new ToolExample { Description = "修复网格", Input = new Request { Target = "MyCube" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("repair-mesh", "No ProBuilderMesh on target.");

            // 先校验以统计问题数
            var validationResult = ProBuilderReflection.Validate(mesh);
            var degenerateCount = 0;
            var unusedCount = 0;

            if (validationResult != null)
            {
                try
                {
                    var resultType = validationResult.GetType();
                    var degProp = resultType.GetProperty("degenerateTriangles");
                    if (degProp != null) degenerateCount = Convert.ToInt32(degProp.GetValue(validationResult), CultureInfo.InvariantCulture);
                    var unusedProp = resultType.GetProperty("unusedVertices");
                    if (unusedProp != null) unusedCount = Convert.ToInt32(unusedProp.GetValue(validationResult), CultureInfo.InvariantCulture);
                }
                catch (Exception) { }
            }

            Undo.RecordObject(go, "ProBuilder Repair Mesh");

            // 通过反射移除退化三角形
            var meshOpsType = ProBuilderReflection.GetMeshOperationsType();
            if (meshOpsType != null)
            {
                try
                {
                    var removeDegMethod = meshOpsType.GetMethod("RemoveDegenerateTriangles", new[] { ProBuilderReflection.GetProBuilderMeshType() });
                    if (removeDegMethod != null)
                        removeDegMethod.Invoke(null, new object[] { mesh });

                    var removeUnusedMethod = meshOpsType.GetMethod("RemoveUnusedVertices", new[] { ProBuilderReflection.GetProBuilderMeshType() });
                    if (removeUnusedMethod != null)
                        removeUnusedMethod.Invoke(null, new object[] { mesh });
                }
                catch (Exception) { }
            }

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var fixedCount = degenerateCount + unusedCount;

            // 重新校验以检查剩余问题
            var remainingIssues = 0;
            var postValidation = ProBuilderReflection.Validate(mesh);
            if (postValidation != null)
            {
                try
                {
                    var resultType = postValidation.GetType();
                    var degProp = resultType.GetProperty("degenerateTriangles");
                    var unusedProp = resultType.GetProperty("unusedVertices");
                    if (degProp != null) remainingIssues += Convert.ToInt32(degProp.GetValue(postValidation), CultureInfo.InvariantCulture);
                    if (unusedProp != null) remainingIssues += Convert.ToInt32(unusedProp.GetValue(postValidation), CultureInfo.InvariantCulture);
                }
                catch (Exception) { }
            }

            return new Result
            {
                RemovedDegenerateCount = degenerateCount,
                FixedCount = fixedCount,
                RemainingIssues = remainingIssues
            };
        }
    }
}
#endif
