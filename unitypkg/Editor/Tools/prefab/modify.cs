#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// prefab.modify — 修改 Prefab 内容(name/addChild/removeChild),通过 LoadPrefabContents 编辑后保存。
    /// </summary>
    public class PrefabModifyTool : IUnityCliTool<PrefabModifyTool.Request, PrefabModifyTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Name { get; set; }
            public string AddChild { get; set; }
            public string RemoveChild { get; set; }
            public bool? Overwrite { get; set; }
        }

        public sealed class Result
        {
            public PrefabInfo Prefab { get; set; }
            public List<PrefabPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "prefab.modify",
            Summary = "Modify a prefab's root name or add/remove a child by editing prefab contents in-place.",
            Description = "Modify a prefab's contents. Supports renaming the root, adding a child (overwrite=true to replace an existing child of the same name), and removing a child by name. At least one mutation is required. Changes are saved through PrefabUtility.SaveAsPrefabAsset.",
            Group = "prefab",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Add a child to a prefab", Input = new Request { Path = "Assets/Prefabs/Player.prefab", AddChild = "HealthBar" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            PrefabToolSupport.EnsureEditorNotBusy("prefab.modify");
            var path = PrefabToolSupport.RequirePrefabPath(request.Path);
            PrefabToolSupport.LoadPrefabAsset(path);
            var root = PrefabUtility.LoadPrefabContents(path);
            var effects = new List<PrefabPlannedEffect>();
            try
            {
                var name = request.Name;
                var addChild = request.AddChild;
                var removeChild = request.RemoveChild;
                var existingAddChild = string.IsNullOrEmpty(addChild) ? null : root.transform.Find(addChild);
                var existingRemoveChild = string.IsNullOrEmpty(removeChild) ? null : root.transform.Find(removeChild);
                if (existingAddChild != null && !(request.Overwrite ?? false))
                    throw new UnityCliBridge.BridgeCommandException("E_PREFAB_CHILD_EXISTS", "Prefab child already exists: " + addChild, "Pass --overwrite or choose another child name.");
                if (!string.IsNullOrEmpty(addChild) && addChild == removeChild)
                    throw new UnityCliBridge.BridgeCommandException("E_PREFAB_CHILD_CONFLICT", "addChild and removeChild cannot target the same child.", "Use separate prefab.modify requests for conflicting child operations.");
                if (!string.IsNullOrEmpty(removeChild) && existingRemoveChild == null)
                    throw new UnityCliBridge.BridgeCommandException("E_PREFAB_CHILD_NOT_FOUND", "Prefab child not found: " + removeChild, "Run tools run prefab.hierarchy --input '{\"path\":\"<path>\"}' to inspect child paths.");
                var rename = !string.IsNullOrEmpty(name) && root.name != name;
                if (!rename && string.IsNullOrEmpty(addChild) && string.IsNullOrEmpty(removeChild))
                    throw new UnityCliBridge.BridgeCommandException("E_PREFAB_MODIFY_NOOP", "No Prefab modifications were requested.", "Pass name, addChild or removeChild.");

                ctx.CommitEffect();
                if (rename)
                {
                    root.name = name;
                    effects.Add(new PrefabPlannedEffect { Type = "prefab.modify", Path = path, Property = "name" });
                }
                if (!string.IsNullOrEmpty(addChild))
                {
                    if (existingAddChild != null) Object.DestroyImmediate(existingAddChild.gameObject);
                    var child = new GameObject(addChild);
                    child.transform.SetParent(root.transform, false);
                    effects.Add(new PrefabPlannedEffect { Type = "prefab.modify", Path = path, AddChild = addChild });
                }
                if (!string.IsNullOrEmpty(removeChild))
                {
                    Object.DestroyImmediate(existingRemoveChild.gameObject);
                    effects.Add(new PrefabPlannedEffect { Type = "prefab.modify", Path = path, RemoveChild = removeChild });
                }

                PrefabUtility.SaveAsPrefabAsset(root, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                var observed = PrefabToolSupport.LoadPrefabAsset(path);
                if (rename && observed.name != name)
                    throw new UnityCliBridge.BridgeCommandException("E_PREFAB_MODIFY_PROOF_MISSING", "Prefab root name was not persisted.", "Inspect the Prefab asset before submitting a new request.");
                if (!string.IsNullOrEmpty(addChild) && observed.transform.Find(addChild) == null)
                    throw new UnityCliBridge.BridgeCommandException("E_PREFAB_MODIFY_PROOF_MISSING", "Prefab child was not persisted: " + addChild, "Inspect the Prefab asset before submitting a new request.");
                if (!string.IsNullOrEmpty(removeChild) && observed.transform.Find(removeChild) != null)
                    throw new UnityCliBridge.BridgeCommandException("E_PREFAB_MODIFY_PROOF_MISSING", "Prefab child was not removed: " + removeChild, "Inspect the Prefab asset before submitting a new request.");
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Prefab = PrefabToolSupport.BuildPrefabInfo(path, observed, observed, true),
                    PlannedEffects = effects
                });
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
#endif
