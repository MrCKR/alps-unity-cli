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
            var sourcePath = AssetToolSupport.NormalizeAssetPath(source);
            if (!string.IsNullOrEmpty(sourcePath) && sourcePath.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase) && string.Equals(sourcePath, path, System.StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_SOURCE_DESTINATION_CONFLICT", "Prefab source and destination must differ: " + path, "Choose a different destination path.");
            var sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (!string.IsNullOrEmpty(sourcePath) && sourcePath.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase) && AssetToolSupport.AssetExists(sourcePath) && sourcePrefab == null)
                throw new UnityCliBridge.BridgeCommandException("E_ASSET_NOT_PREFAB", "Source asset is not a Prefab: " + sourcePath, "Pass an existing Assets/*.prefab source or a scene GameObject.");
            var sourceGameObject = sourcePrefab == null ? PrefabToolSupport.ResolveGameObjectFirstOrDefault(source) : null;
            if (sourcePrefab == null && sourceGameObject == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "Source GameObject not found: " + source, "Run tools run scene.hierarchy to list objects, or pass a hierarchy path / instance id.");
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null && !overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_OUTPUT_EXISTS", "Prefab output already exists: " + path, "Pass --overwrite or choose a different Assets/*.prefab path.");
            }

            ctx.CommitEffect();
            AssetToolSupport.CreateAssetDirectory(path);
            // 源是已有 Prefab 资产:复制资产
            if (sourcePrefab != null)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
                if (!AssetDatabase.CopyAsset(sourcePath, path))
                    throw new UnityCliBridge.BridgeCommandException("E_PREFAB_CREATE_FAILED", "Unity could not copy the source Prefab asset.", "Check the source and destination paths.");
                AssetDatabase.SaveAssets();
                var outputGuid = AssetDatabase.AssetPathToGUID(path);
                var observedPath = string.IsNullOrEmpty(outputGuid) ? null : AssetDatabase.GUIDToAssetPath(outputGuid);
                if (string.IsNullOrEmpty(observedPath) || !string.Equals(observedPath, path, System.StringComparison.OrdinalIgnoreCase))
                    throw new UnityCliBridge.BridgeCommandException("E_PREFAB_CREATE_PROOF_MISSING", "Prefab GUID did not resolve back to the requested destination.", "Inspect the output asset after import.");
                var observedCopy = string.IsNullOrEmpty(observedPath) ? null : PrefabToolSupport.LoadPrefabAsset(observedPath);
                if (observedCopy == null || observedCopy.name != sourcePrefab.name)
                    throw new UnityCliBridge.BridgeCommandException("E_PREFAB_CREATE_PROOF_MISSING", "Copied Prefab asset was not available after save.", "Inspect the output path after Unity finishes importing.");
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Prefab = PrefabToolSupport.BuildPrefabInfo(observedPath, observedCopy, null, true),
                    Source = source,
                    Mode = "copy",
                    PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.create", Path = path, Source = source } }
                });
            }

            // 源是场景 GameObject:保存为 Prefab
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }
            var expectedRootName = sourceGameObject.name;
            PrefabUtility.SaveAsPrefabAssetAndConnect(sourceGameObject, path, InteractionMode.AutomatedAction);
            AssetDatabase.SaveAssets();
            var sceneOutputGuid = AssetDatabase.AssetPathToGUID(path);
            var sceneObservedPath = string.IsNullOrEmpty(sceneOutputGuid) ? null : AssetDatabase.GUIDToAssetPath(sceneOutputGuid);
            if (string.IsNullOrEmpty(sceneObservedPath) || !string.Equals(sceneObservedPath, path, System.StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_CREATE_PROOF_MISSING", "Prefab GUID did not resolve back to the requested destination.", "Inspect the output asset after import.");
            var observedPrefab = string.IsNullOrEmpty(sceneObservedPath) ? null : PrefabToolSupport.LoadPrefabAsset(sceneObservedPath);
            if (observedPrefab == null || observedPrefab.name != expectedRootName)
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_CREATE_PROOF_MISSING", "Created Prefab asset was not available after save.", "Inspect the output path after Unity finishes importing.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Prefab = PrefabToolSupport.BuildPrefabInfo(sceneObservedPath, observedPrefab, null, true),
                Source = source,
                Mode = "scene",
                PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.create", Path = path, Source = source } }
            });
        }
    }
}
#endif
