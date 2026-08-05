#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.merge-objects — 将多个 GameObject 合并为单个 ProBuilder 对象。
    /// </summary>
    public class MergeObjectsTool : IUnityCliTool<MergeObjectsTool.Request, MergeObjectsTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public List<string> Targets { get; set; }
            public string Name { get; set; }                         // 可选,默认 ProBuilder_Merged
        }

        public sealed class Result
        {
            public ObjectRef MergedObject { get; set; }
        }

        public sealed class ObjectRef
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.merge-objects",
            Summary = "Merge multiple GameObjects into a single ProBuilder mesh object.",
            Description = "Merge at least two existing ProBuilderMesh objects into the first requested object with the ProBuilder 5.2.4 CombineMeshes API. Every source must already have ProBuilderMesh and no UV1; convert regular meshes with probuilder.convert-to-probuilder before calling this Tool. UV1 is rejected because ToMesh clears it. All target-local projections and the complete merged refresh arithmetic must be finite and bounded before mutation.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 15000,
            MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "合并两个对象", Input = new Request { Targets = new List<string> { "Cube1", "Cube2" }, Name = "Merged" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var targets = ProBuilderInput.ResolveTargets(request.Targets);
            if (request.Targets == null || targets == null || targets.Length != request.Targets.Count || targets.Length < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 unique, unambiguous targets required for merge-objects.");

            var name = request.Name ?? "ProBuilder_Merged";
            if (string.IsNullOrWhiteSpace(name)) throw ProBuilderErrors.SchemaInvalid("name must be non-empty.");
            if (targets.Select(target => target.GetInstanceID()).Distinct().Count() != targets.Length)
                throw ProBuilderErrors.SchemaInvalid("merge-objects does not accept duplicate source identities.");
            if (targets.Any(left => targets.Any(right => left != right && (left.transform.IsChildOf(right.transform) || right.transform.IsChildOf(left.transform)))))
                throw ProBuilderErrors.SchemaInvalid("merge-objects does not accept ancestor/descendant sources.");
            if (targets.Any(target => ProBuilderReflection.GetProBuilderMesh(target) == null))
                throw ProBuilderErrors.SchemaInvalid("merge-objects requires every target to already have a ProBuilderMesh component; call probuilder.convert-to-probuilder first for regular meshes.");
            if (targets.Any(target => target.scene.handle != targets[0].scene.handle || !target.scene.isLoaded || target.transform.childCount != 0 && target != targets[0]))
                throw ProBuilderErrors.SchemaInvalid("All sources must be in the same loaded scene and non-first sources must not have children.");
            if (targets.Any(target => !FiniteInvertible(target.transform.localToWorldMatrix) || !FiniteInvertible(target.transform.worldToLocalMatrix)))
                throw ProBuilderErrors.SchemaInvalid("Every source transform must be finite and invertible.");
            var plan = ProBuilderVertexObjectCompletionProof.CaptureMergeObjects(targets, name);
            if (plan == null)
                throw ProBuilderErrors.SchemaInvalid("merge-objects requires strict canonical ProBuilder sources, complete sharedTextures, absent UV1, closed non-null material slots, deterministic contributor UVs, and fewer than 65535 raw vertices.");

            var pbMeshes = targets.Select(ProBuilderReflection.GetProBuilderMesh).ToArray();
            ctx.CommitEffect();
            try
            {
                Undo.RecordObject(targets[0], "ProBuilder Merge Objects");
                if (!ProBuilderReflection.CombineMeshesViaReflection(pbMeshes, pbMeshes[0])) throw ProBuilderErrors.OutcomeUnknown("merge-objects", "invoke", "ProBuilder 5.x CombineMeshes.Combine did not return exactly one mesh.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("merge-objects", "invoke", exception.Message); }
            GameObject freshGo;
            object freshMesh;
            try
            {
                if (!ProBuilderReflection.RefreshMesh(pbMeshes[0])) throw ProBuilderErrors.OutcomeUnknown("merge-objects", "readback", "ProBuilder mesh refresh failed after the effect.");
                for (var index = 1; index < targets.Length; index++) Undo.DestroyObjectImmediate(targets[index]);
                targets[0].name = name;
                EditorSceneManager.MarkSceneDirty(targets[0].scene);
                freshGo = EditorUtility.InstanceIDToObject(plan.Sources[0].Identity.GameObjectInstanceId) as GameObject;
                freshMesh = ProBuilderReflection.GetProBuilderMesh(freshGo);
                if (freshGo == null || freshMesh == null) throw ProBuilderErrors.OutcomeUnknown("merge-objects", "readback", "Result identity or mesh could not be resolved.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("merge-objects", "readback", exception.Message); }
            try
            {
                if (!ProBuilderVertexObjectCompletionProof.MergeObjectsMatches(plan, freshGo, freshMesh as UnityEngine.ProBuilder.ProBuilderMesh)) throw ProBuilderErrors.OutcomeUnknown("merge-objects", "postcondition", "The complete ProBuilder 5.2.4 combine terminal state was not proven by fresh readback.");
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("merge-objects", "postcondition", exception.Message); }
            try
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result { MergedObject = new ObjectRef { Id = freshGo.GetInstanceID(), Name = freshGo.name, Path = HierarchyPath(freshGo) } });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception) { throw ProBuilderErrors.OutcomeUnknown("merge-objects", "postcondition", exception.Message); }
        }

        /// <summary>矩阵必须有限且可逆，避免 contributor world 到 target local 变换不可复演。</summary>
        private static bool FiniteInvertible(Matrix4x4 matrix)
        {
            if (Mathf.Abs(matrix.determinant) <= Mathf.Epsilon) return false;
            for (var index = 0; index < 16; index++) if (float.IsNaN(matrix[index]) || float.IsInfinity(matrix[index])) return false;
            return true;
        }

        private static string HierarchyPath(GameObject go)
        {
            var names = new Stack<string>();
            var cur = go.transform;
            while (cur != null) { names.Push(cur.name); cur = cur.parent; }
            return string.Join("/", names.ToArray());
        }
    }
}
#endif
