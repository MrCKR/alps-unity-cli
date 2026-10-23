#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>Prefab override 与 variant Tool 的 immutable plan、effect 和 fresh proof owner。</summary>
    internal static class PrefabOverrideToolSupport
    {
        internal sealed class OverridePlan
        {
            public string Selector;
            public int InstanceId;
            public string ScenePath;
            public string SourcePath;
            public string SourceGuid;
            public string SourceHash;
            public GameObject Root;
            public GameObject Source;
            public PrefabOverrideSummary Before;
        }

        internal sealed class VariantPlan
        {
            public string BasePath;
            public string BaseGuid;
            public string BaseHash;
            public string OutputPath;
            public GameObject BaseAsset;
        }

        /// <summary>解析 scene instance 并冻结 source identity/hash；不创建 Undo 或修改 Prefab 状态。</summary>
        internal static OverridePlan PrepareOverride(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector))
                throw Error("E_PREFAB_INSTANCE_REQUIRED", "A prefab scene instance selector is required.", null, null);
            var selected = GameObjectToolSupport.ResolveUniqueTarget(selector, "Prefab instance is required.");
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(selected);
            if (root == null || !root.scene.IsValid() || !root.scene.isLoaded)
                throw Error("E_PREFAB_INSTANCE_REQUIRED", "The selected GameObject is not part of a loaded scene prefab instance.", null, null);
            var sourcePath = PrefabToolSupport.RequirePrefabPath(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root));
            var sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            var source = PrefabToolSupport.LoadPrefabAsset(sourcePath);
            if (string.IsNullOrEmpty(sourceGuid) || !string.Equals(AssetDatabase.GUIDToAssetPath(sourceGuid), sourcePath, StringComparison.Ordinal))
                throw Error("E_PREFAB_SOURCE_PROOF_MISSING", "The prefab source GUID does not resolve to the current source path.", sourcePath, sourceGuid);
            if (PrefabUtility.GetCorrespondingObjectFromSource(root) != source)
                throw Error("E_PREFAB_SOURCE_PROOF_MISSING", "The prefab instance does not resolve to the loaded source asset.", sourcePath, sourceGuid);
            return new OverridePlan
            {
                Selector = selector,
                InstanceId = root.GetInstanceID(),
                ScenePath = root.scene.path,
                SourcePath = sourcePath,
                SourceGuid = sourceGuid,
                SourceHash = FileSha256(sourcePath),
                Root = root,
                Source = source,
                Before = ReadOverrides(root)
            };
        }

        /// <summary>应用全部 non-default overrides 到 source asset，并将 scene instance 标为 dirty。</summary>
        internal static void ApplyOverrides(OverridePlan plan)
        {
            Undo.RegisterFullObjectHierarchyUndo(plan.Root, "Apply Prefab Overrides");
            PrefabUtility.ApplyPrefabInstance(plan.Root, InteractionMode.AutomatedAction);
            UnityCliSceneMutation.MarkSceneDirty(plan.Root.scene);
            AssetDatabase.SaveAssets();
        }

        /// <summary>回退全部 non-default overrides，仅修改 scene instance，不写 source asset。</summary>
        internal static void RevertOverrides(OverridePlan plan)
        {
            Undo.RegisterFullObjectHierarchyUndo(plan.Root, "Revert Prefab Overrides");
            PrefabUtility.RevertPrefabInstance(plan.Root, InteractionMode.AutomatedAction);
            UnityCliSceneMutation.MarkSceneDirty(plan.Root.scene);
        }

        /// <summary>effect 后重新解析 instance/source，并证明 apply 或 revert 的独立业务终态。</summary>
        internal static PrefabOverrideResult ProveOverrides(OverridePlan plan, string action)
        {
            var root = EditorUtility.InstanceIDToObject(plan.InstanceId) as GameObject;
            if (root == null || !root.scene.IsValid() || !root.scene.isLoaded)
                throw Error("E_PREFAB_OVERRIDE_PROOF_FAILED", "The prefab instance disappeared before terminal readback.", plan.SourcePath, plan.SourceGuid);
            var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            var sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            var source = string.IsNullOrEmpty(sourcePath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            if (!string.Equals(sourcePath, plan.SourcePath, StringComparison.Ordinal)
                || !string.Equals(sourceGuid, plan.SourceGuid, StringComparison.Ordinal)
                || source == null
                || PrefabUtility.GetCorrespondingObjectFromSource(root) != source
                || PrefabUtility.GetPrefabInstanceStatus(root) != PrefabInstanceStatus.Connected)
                throw Error("E_PREFAB_OVERRIDE_PROOF_FAILED", "The prefab instance/source connection changed unexpectedly.", sourcePath, sourceGuid);
            var after = ReadOverrides(root);
            if (after.HasOverrides)
                throw Error("E_PREFAB_OVERRIDE_PROOF_FAILED", "The prefab instance still has non-default overrides after " + action + ".", sourcePath, sourceGuid);
            var sourceHash = FileSha256(sourcePath);
            if (string.Equals(action, "apply", StringComparison.Ordinal) && string.Equals(sourceHash, plan.SourceHash, StringComparison.Ordinal))
                throw Error("E_PREFAB_OVERRIDE_PROOF_FAILED", "Applying overrides did not change the serialized source prefab.", sourcePath, sourceGuid);
            if (string.Equals(action, "revert", StringComparison.Ordinal) && !string.Equals(sourceHash, plan.SourceHash, StringComparison.Ordinal))
                throw Error("E_PREFAB_OVERRIDE_PROOF_FAILED", "Reverting scene overrides unexpectedly changed the source prefab.", sourcePath, sourceGuid);
            return new PrefabOverrideResult
            {
                Action = action,
                Changed = plan.Before.HasOverrides,
                DryRun = false,
                Source = BuildAssetIdentity(sourcePath, source),
                Before = plan.Before,
                After = after,
                ScenePath = root.scene.path,
                SceneDirty = root.scene.isDirty,
                SourceHashBefore = plan.SourceHash,
                SourceHashAfter = sourceHash,
                PlannedEffects = Effects(action, sourcePath)
            };
        }

        internal static PrefabOverrideResult DryRunOverride(OverridePlan plan, string action)
        {
            return new PrefabOverrideResult
            {
                Action = action,
                Changed = plan.Before.HasOverrides,
                DryRun = true,
                Source = BuildAssetIdentity(plan.SourcePath, plan.Source),
                Before = plan.Before,
                After = plan.Before,
                ScenePath = plan.ScenePath,
                SceneDirty = plan.Root.scene.isDirty,
                SourceHashBefore = plan.SourceHash,
                SourceHashAfter = plan.SourceHash,
                PlannedEffects = plan.Before.HasOverrides ? Effects(action, plan.SourcePath) : new List<PrefabPlannedEffect>()
            };
        }

        internal static PrefabOverrideResult NoopOverride(OverridePlan plan, string action)
        {
            var result = DryRunOverride(plan, action);
            result.DryRun = false;
            result.PlannedEffects.Clear();
            return result;
        }

        /// <summary>验证 base/output identity 与 parent folder；创建 preview scene 前完成全部可能失败的静态检查。</summary>
        internal static VariantPlan PrepareVariant(string rawBasePath, string rawOutputPath)
        {
            var basePath = PrefabToolSupport.RequirePrefabPath(rawBasePath);
            var outputPath = PrefabToolSupport.RequirePrefabOutputPath(rawOutputPath);
            if (string.Equals(basePath, outputPath, StringComparison.OrdinalIgnoreCase))
                throw Error("E_PREFAB_SOURCE_DESTINATION_CONFLICT", "Variant output must differ from its base prefab.", outputPath, null);
            var baseAsset = PrefabToolSupport.LoadPrefabAsset(basePath);
            var baseGuid = AssetDatabase.AssetPathToGUID(basePath);
            if (string.IsNullOrEmpty(baseGuid) || !string.Equals(AssetDatabase.GUIDToAssetPath(baseGuid), basePath, StringComparison.Ordinal))
                throw Error("E_PREFAB_SOURCE_PROOF_MISSING", "The base prefab GUID is unavailable.", basePath, baseGuid);
            if (AssetToolSupport.AssetExists(outputPath))
                throw Error("E_PREFAB_OUTPUT_EXISTS", "Variant output already exists: " + outputPath, outputPath, null);
            var outputFullPath = AssetToolSupport.AssetFullPath(outputPath);
            if (File.Exists(outputFullPath) || File.Exists(outputFullPath + ".meta"))
                throw Error("E_PREFAB_OUTPUT_META_EXISTS", "Variant output or its metadata file already exists outside the current AssetDatabase view.", outputPath, null);
            var parent = Path.GetDirectoryName(outputPath);
            parent = string.IsNullOrEmpty(parent) ? null : parent.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent) || !AssetDatabase.IsValidFolder(parent))
                throw Error("E_PREFAB_OUTPUT_FOLDER_NOT_FOUND", "Variant output parent folder does not exist: " + parent, outputPath, null);
            return new VariantPlan
            {
                BasePath = basePath,
                BaseGuid = baseGuid,
                BaseHash = FileSha256(basePath),
                OutputPath = outputPath,
                BaseAsset = baseAsset
            };
        }

        /// <summary>在隔离 preview scene 中保存 variant；finally 始终销毁 instance 并关闭 preview scene。</summary>
        internal static void CreateVariant(VariantPlan plan)
        {
            var previewScene = EditorSceneManager.NewPreviewScene();
            GameObject instance = null;
            try
            {
                instance = PrefabUtility.InstantiatePrefab(plan.BaseAsset, previewScene) as GameObject;
                if (instance == null)
                    throw Error("E_PREFAB_VARIANT_CREATE_FAILED", "Unity could not instantiate the base prefab in the preview scene.", plan.BasePath, plan.BaseGuid);
                bool success;
                var saved = PrefabUtility.SaveAsPrefabAsset(instance, plan.OutputPath, out success);
                if (!success || saved == null)
                    throw Error("E_PREFAB_VARIANT_CREATE_FAILED", "Unity could not save the prefab variant.", plan.OutputPath, null);
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
                EditorSceneManager.ClosePreviewScene(previewScene);
            }
            AssetDatabase.SaveAssets();
        }

        /// <summary>fresh 加载 output，并证明 Variant 类型、base identity、GUID 和 base asset 未被改写。</summary>
        internal static PrefabVariantCreateResult ProveVariant(VariantPlan plan)
        {
            var outputGuid = AssetDatabase.AssetPathToGUID(plan.OutputPath);
            var outputPath = string.IsNullOrEmpty(outputGuid) ? null : AssetDatabase.GUIDToAssetPath(outputGuid);
            var output = string.IsNullOrEmpty(outputPath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(outputPath);
            var baseAsset = AssetDatabase.LoadAssetAtPath<GameObject>(plan.BasePath);
            if (!string.Equals(outputPath, plan.OutputPath, StringComparison.Ordinal)
                || string.IsNullOrEmpty(outputGuid)
                || string.Equals(outputGuid, plan.BaseGuid, StringComparison.Ordinal)
                || output == null
                || PrefabUtility.GetPrefabAssetType(output) != PrefabAssetType.Variant)
                throw Error("E_PREFAB_VARIANT_PROOF_FAILED", "The created asset is not a fresh prefab variant at the requested path.", outputPath, outputGuid);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(output);
            var sourcePath = source == null ? null : AssetDatabase.GetAssetPath(source);
            var sourceGuid = string.IsNullOrEmpty(sourcePath) ? null : AssetDatabase.AssetPathToGUID(sourcePath);
            if (!string.Equals(sourcePath, plan.BasePath, StringComparison.Ordinal)
                || !string.Equals(sourceGuid, plan.BaseGuid, StringComparison.Ordinal)
                || baseAsset == null
                || !string.Equals(FileSha256(plan.BasePath), plan.BaseHash, StringComparison.Ordinal))
                throw Error("E_PREFAB_VARIANT_PROOF_FAILED", "The created variant does not preserve the requested base prefab identity.", sourcePath, sourceGuid);
            return new PrefabVariantCreateResult
            {
                Created = true,
                DryRun = false,
                Variant = BuildAssetIdentity(outputPath, output),
                PlannedEffects = VariantEffects(plan)
            };
        }

        internal static PrefabVariantCreateResult DryRunVariant(VariantPlan plan)
        {
            return new PrefabVariantCreateResult
            {
                Created = false,
                DryRun = true,
                Variant = new PrefabAssetIdentity { Path = plan.OutputPath, AssetType = PrefabAssetType.Variant.ToString(), IsVariant = true, BasePath = plan.BasePath, BaseGuid = plan.BaseGuid },
                PlannedEffects = VariantEffects(plan)
            };
        }

        private static PrefabOverrideSummary ReadOverrides(GameObject root)
        {
            var modifications = PrefabUtility.GetPropertyModifications(root) ?? new PropertyModification[0];
            return new PrefabOverrideSummary
            {
                HasOverrides = PrefabUtility.HasPrefabInstanceAnyOverrides(root, false),
                PropertyModificationCount = modifications.LongLength,
                InstanceStatus = PrefabUtility.GetPrefabInstanceStatus(root).ToString()
            };
        }

        private static PrefabAssetIdentity BuildAssetIdentity(string path, GameObject asset)
        {
            var variant = asset == null ? null : PrefabToolSupport.BuildVariantInfo(asset);
            return new PrefabAssetIdentity
            {
                Path = path,
                Guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path),
                RootName = asset == null ? null : asset.name,
                AssetType = asset == null ? null : PrefabUtility.GetPrefabAssetType(asset).ToString(),
                IsVariant = asset != null && PrefabUtility.GetPrefabAssetType(asset) == PrefabAssetType.Variant,
                BasePath = variant == null ? null : variant.BasePath,
                BaseGuid = variant == null ? null : variant.BaseGuid
            };
        }

        private static List<PrefabPlannedEffect> Effects(string action, string path)
        {
            return new List<PrefabPlannedEffect>
            {
                new PrefabPlannedEffect { Type = "prefab.overrides." + action, Path = path, Source = path }
            };
        }

        private static List<PrefabPlannedEffect> VariantEffects(VariantPlan plan)
        {
            return new List<PrefabPlannedEffect>
            {
                new PrefabPlannedEffect { Type = "prefab.variant-create", Path = plan.OutputPath, Source = plan.BasePath }
            };
        }

        private static string FileSha256(string assetPath)
        {
            var fullPath = AssetToolSupport.AssetFullPath(assetPath);
            if (!File.Exists(fullPath))
                throw Error("E_PREFAB_SOURCE_PROOF_MISSING", "The serialized prefab file is unavailable: " + assetPath, assetPath, null);
            try
            {
                using (var stream = File.OpenRead(fullPath))
                using (var sha = SHA256.Create())
                    return string.Concat(sha.ComputeHash(stream).Select(value => value.ToString("x2")));
            }
            catch (Exception)
            {
                throw Error("E_PREFAB_SOURCE_PROOF_UNAVAILABLE", "The serialized prefab hash could not be read.", assetPath, null);
            }
        }

        private static UnityCliBridge.BridgeCommandException Error(string code, string message, string path, string guid)
        {
            return new UnityCliBridge.BridgeCommandException(
                code,
                message,
                new Dictionary<string, object> { { "path", path }, { "guid", guid } },
                new[] { "Run tools run prefab.info for the source asset and inspect the selected scene instance before retrying." });
        }
    }

    public sealed class PrefabOverrideSummary
    {
        public bool HasOverrides { get; set; }
        public long PropertyModificationCount { get; set; }
        public string InstanceStatus { get; set; }
    }

    public sealed class PrefabAssetIdentity
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string RootName { get; set; }
        public string AssetType { get; set; }
        public bool IsVariant { get; set; }
        public string BasePath { get; set; }
        public string BaseGuid { get; set; }
    }

    public sealed class PrefabOverrideResult
    {
        public string Action { get; set; }
        public bool Changed { get; set; }
        public bool DryRun { get; set; }
        public PrefabAssetIdentity Source { get; set; }
        public PrefabOverrideSummary Before { get; set; }
        public PrefabOverrideSummary After { get; set; }
        public string ScenePath { get; set; }
        public bool SceneDirty { get; set; }
        public string SourceHashBefore { get; set; }
        public string SourceHashAfter { get; set; }
        public List<PrefabPlannedEffect> PlannedEffects { get; set; }
    }

    public sealed class PrefabVariantCreateResult
    {
        public bool Created { get; set; }
        public bool DryRun { get; set; }
        public PrefabAssetIdentity Variant { get; set; }
        public List<PrefabPlannedEffect> PlannedEffects { get; set; }
    }
}
#endif
