#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// prefab.modify — 修改 Prefab 内容(name/addChild/removeChild),通过 LoadPrefabContents 编辑后保存。
    /// 对齐旧 PrefabModify handler;session-affecting(asset-write)。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
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
            DestructiveHint = false,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Add a child to a prefab", Input = new Request { Path = "Assets/Prefabs/Player.prefab", AddChild = "HealthBar" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            PrefabToolSupport.EnsureEditorNotBusy("prefab.modify");
            var path = PrefabToolSupport.RequirePrefabPath(request.Path);
            PrefabToolSupport.LoadPrefabAsset(path);
            var root = PrefabUtility.LoadPrefabContents(path);
            var effects = new List<PrefabPlannedEffect>();
            try
            {
                var name = request.Name;
                if (!string.IsNullOrEmpty(name) && root.name != name)
                {
                    root.name = name;
                    effects.Add(new PrefabPlannedEffect { Type = "prefab.modify", Path = path, Property = "name" });
                }

                var addChild = request.AddChild;
                if (!string.IsNullOrEmpty(addChild))
                {
                    var existing = root.transform.Find(addChild);
                    if (existing != null && !(request.Overwrite ?? false))
                    {
                        throw new UnityCliBridge.BridgeCommandException("E_PREFAB_CHILD_EXISTS", "Prefab child already exists: " + addChild, "Pass --overwrite or choose another child name.");
                    }
                    if (existing != null)
                    {
                        Object.DestroyImmediate(existing.gameObject);
                    }
                    var child = new GameObject(addChild);
                    child.transform.SetParent(root.transform, false);
                    effects.Add(new PrefabPlannedEffect { Type = "prefab.modify", Path = path, AddChild = addChild });
                }

                var removeChild = request.RemoveChild;
                if (!string.IsNullOrEmpty(removeChild))
                {
                    var child = root.transform.Find(removeChild);
                    if (child == null)
                    {
                        throw new UnityCliBridge.BridgeCommandException("E_PREFAB_CHILD_NOT_FOUND", "Prefab child not found: " + removeChild, "Run tools run prefab.hierarchy --input '{\"path\":\"<path>\"}' to inspect child paths.");
                    }
                    Object.DestroyImmediate(child.gameObject);
                    effects.Add(new PrefabPlannedEffect { Type = "prefab.modify", Path = path, RemoveChild = removeChild });
                }

                if (effects.Count == 0)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_PREFAB_MODIFY_NOOP", "No Prefab modifications were requested.", "Pass name, addChild or removeChild.");
                }

                PrefabUtility.SaveAsPrefabAsset(root, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                return new Result
                {
                    Prefab = PrefabToolSupport.BuildPrefabInfo(path, root, root, true),
                    PlannedEffects = effects
                };
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
    }
}
#endif
