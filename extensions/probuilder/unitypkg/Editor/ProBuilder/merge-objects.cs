#if UNITY_EDITOR
using System.Collections.Generic;
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
            public bool? AutoConvert { get; set; }                  // 可选,默认 true
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
            Description = "Merge multiple GameObjects into exactly one ProBuilder object with the ProBuilder 5.x CombineMeshes API. Requires at least 2 targets and can import regular meshes before merging when autoConvert is true.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 15000,
            MaxTimeoutMs = 60000,
            SupportsJob = true,
            ArtifactKinds = new[] { "json" },
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
            if (targets == null || targets.Length < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 targets required for merge-objects.");

            var name = request.Name ?? "ProBuilder_Merged";
            var autoConvert = request.AutoConvert ?? true;

            // 收集或转换 ProBuilder 网格
            var pbMeshes = new List<object>();
            var nonPbObjects = new List<GameObject>();

            foreach (var go in targets)
            {
                var pbMesh = ProBuilderReflection.GetProBuilderMesh(go);
                if (pbMesh != null)
                {
                    pbMeshes.Add(pbMesh);
                }
                else if (autoConvert)
                {
                    nonPbObjects.Add(go);
                }
                else
                {
                    throw ProBuilderErrors.OperationFailed("merge-objects", $"GameObject '{go.name}' does not have a ProBuilderMesh component and autoConvert is disabled.");
                }
            }

            // 转换非 ProBuilder 对象
            foreach (var go in nonPbObjects)
            {
                var converted = ProBuilderReflection.ConvertToProBuilderMesh(go);
                if (converted == null)
                    throw ProBuilderErrors.OperationFailed("merge-objects", $"Failed to convert '{go.name}' to ProBuilder mesh.");
                pbMeshes.Add(converted);
            }

            if (pbMeshes.Count < 2)
                throw ProBuilderErrors.SchemaInvalid("At least 2 ProBuilder meshes required after conversion.");

            Undo.RecordObject(targets[0], "ProBuilder Merge Objects");

            var merged = ProBuilderReflection.CombineMeshesViaReflection(pbMeshes.ToArray(), pbMeshes[0]);
            if (!merged)
                throw ProBuilderErrors.OperationFailed("merge-objects", "ProBuilder 5.x CombineMeshes.Combine did not return exactly one mesh.");

            ProBuilderReflection.RefreshMesh(pbMeshes[0]);
            var firstGo = targets[0];
            if (!string.IsNullOrEmpty(name)) firstGo.name = name;
            EditorSceneManager.MarkSceneDirty(firstGo.scene);

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                MergedObject = new ObjectRef
                {
                    Id = firstGo.GetInstanceID(),
                    Name = firstGo.name,
                    Path = HierarchyPath(firstGo)
                }
            });
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
