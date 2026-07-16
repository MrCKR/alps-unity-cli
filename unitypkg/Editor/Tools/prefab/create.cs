#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// prefab.create — 从源 GameObject 或 Prefab 资产创建新 Prefab。拒绝覆盖(除非 overwrite)。
    /// </summary>
    public class PrefabCreateTool : IUnityCliTool<PrefabCreateTool.Request, PrefabCreateTool.Result>
    {
        public sealed class Request
        {
            public string Source { get; set; }
            public string Target { get; set; }
            public string Path { get; set; }
            public string Destination { get; set; }
            public string To { get; set; }
            public bool? Overwrite { get; set; }
        }

        public sealed class Result
        {
            public PrefabInfo Prefab { get; set; }
            public string Source { get; set; }
            public string Mode { get; set; }
            public List<PrefabPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "prefab.create",
            Summary = "Create a new Assets/*.prefab from a source GameObject or an existing prefab asset path.",
            Description = "Create a new Assets/*.prefab. When source is an Assets/*.prefab path, the asset is copied; otherwise source is resolved as a scene GameObject (name, hierarchy path, or instance id) and saved as a prefab. Refuses to overwrite an existing prefab unless overwrite=true.",
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
                new ToolExample { Description = "Save a scene object as prefab", Input = new Request { Source = "Player", Path = "Assets/Prefabs/Player.prefab" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            PrefabToolSupport.EnsureEditorNotBusy("prefab.create");
            var source = request.Source ?? request.Target;
            var path = PrefabToolSupport.RequirePrefabOutputPath(request.Path ?? request.Destination ?? request.To);
            var overwrite = request.Overwrite ?? false;
            if (string.IsNullOrEmpty(source))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "Prefab create requires a source GameObject or Prefab asset path.", "Pass a hierarchy path, GameObject name, instance id, or existing Assets/*.prefab path.");
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null && !overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_OUTPUT_EXISTS", "Prefab output already exists: " + path, "Pass --overwrite or choose a different Assets/*.prefab path.");
            }

            AssetToolSupport.CreateAssetDirectory(path);
            // 源是已有 Prefab 资产:复制资产
            var sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetToolSupport.NormalizeAssetPath(source));
            if (sourcePrefab != null)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
                if (!AssetDatabase.CopyAsset(AssetToolSupport.NormalizeAssetPath(source), path))
                {
                    throw new UnityCliBridge.BridgeCommandException("E_PREFAB_CREATE_FAILED", "Failed to copy Prefab asset to: " + path, "Check destination path and Unity Console diagnostics.");
                }
                AssetDatabase.SaveAssets();
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Prefab = PrefabToolSupport.BuildPrefabInfo(path, PrefabToolSupport.LoadPrefabAsset(path), null, true),
                    Source = source,
                    Mode = "copy",
                    PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.create", Path = path, Source = source } }
                });
            }

            // 源是场景 GameObject:保存为 Prefab
            var gameObject = PrefabToolSupport.ResolveGameObjectFirstOrDefault(source);
            if (gameObject == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "Source GameObject not found: " + source, "Run tools run scene.hierarchy to list objects, or pass a hierarchy path / instance id.");
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }
            var saved = PrefabUtility.SaveAsPrefabAssetAndConnect(gameObject, path, InteractionMode.AutomatedAction);
            if (saved == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_CREATE_FAILED", "Failed to create Prefab at: " + path, "Check destination path, scene object validity and Unity Console diagnostics.");
            }
            AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Prefab = PrefabToolSupport.BuildPrefabInfo(path, PrefabToolSupport.LoadPrefabAsset(path), null, true),
                Source = source,
                Mode = "scene",
                PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.create", Path = path, Source = source } }
            });
        }
    }
}
#endif
