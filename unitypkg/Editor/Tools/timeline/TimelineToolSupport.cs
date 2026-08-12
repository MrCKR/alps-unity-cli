#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>Timeline Tool 的 package-optional 反射、路径、identity 与 fresh readback owner。</summary>
    internal static class TimelineToolSupport
    {
        internal const string PackageId = "com.unity.timeline";
        private const string TimelineAssetTypeName = "UnityEngine.Timeline.TimelineAsset, Unity.Timeline";

        internal sealed class CreatePlan
        {
            internal string Path;
            internal double FrameRate;
            internal bool Overwrite;
            internal bool Existing;
            internal string ExistingGuid;
            internal Type TimelineType;
            internal PropertyInfo EditorSettingsProperty;
            internal PropertyInfo FrameRateProperty;
        }

        /// <summary>effect 前解析 Timeline API、输出路径、frame-rate member 与现有 asset identity。</summary>
        internal static CreatePlan PrepareCreate(string rawPath, double? requestedFrameRate, bool overwrite)
        {
            var timelineType = RequireTimelineAssetType();
            var path = RequireTimelinePath(rawPath);
            var frameRate = requestedFrameRate ?? 60d;
            if (double.IsNaN(frameRate) || double.IsInfinity(frameRate) || frameRate < 0.000001d || frameRate > 1000d)
                throw Error("E_TIMELINE_FRAME_RATE_INVALID", "Timeline frameRate must be finite and within [0.000001,1000].", path, null);

            var settings = timelineType.GetProperty("editorSettings", BindingFlags.Public | BindingFlags.Instance);
            var frameRateProperty = settings == null ? null : settings.PropertyType.GetProperty("frameRate", BindingFlags.Public | BindingFlags.Instance);
            if (frameRateProperty == null || !frameRateProperty.CanRead || !frameRateProperty.CanWrite
                || !(frameRateProperty.PropertyType == typeof(double) || frameRateProperty.PropertyType == typeof(float)))
                throw Error("E_TIMELINE_API_UNAVAILABLE", "The installed Timeline package does not expose a writable editorSettings.frameRate contract.", path, null);

            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            var orphanMetaPath = AssetToolSupport.AssetFullPath(path) + ".meta";
            if (existing == null && !AssetToolSupport.AssetExists(path) && File.Exists(orphanMetaPath))
                throw Error("E_TIMELINE_OUTPUT_META_EXISTS", "Timeline output metadata exists without its asset file.", path, null);
            if (existing == null && AssetToolSupport.AssetExists(path))
                throw Error("E_TIMELINE_OUTPUT_TYPE_CONFLICT", "Timeline output exists but is not a readable main asset.", path, null);
            if (existing != null && !timelineType.IsInstanceOfType(existing))
                throw Error("E_TIMELINE_OUTPUT_TYPE_CONFLICT", "Timeline output path is occupied by a different asset type.", path, AssetDatabase.AssetPathToGUID(path));
            if (existing != null && !overwrite)
                throw Error("E_TIMELINE_OUTPUT_EXISTS", "Timeline output already exists.", path, AssetDatabase.AssetPathToGUID(path));
            return new CreatePlan
            {
                Path = path,
                FrameRate = frameRate,
                Overwrite = overwrite,
                Existing = existing != null,
                ExistingGuid = existing == null ? null : AssetDatabase.AssetPathToGUID(path),
                TimelineType = timelineType,
                EditorSettingsProperty = settings,
                FrameRateProperty = frameRateProperty
            };
        }

        /// <summary>在唯一 effect boundary 后创建或替换 TimelineAsset，并同步写入 asset database。</summary>
        internal static void Create(CreatePlan plan)
        {
            var timeline = ScriptableObject.CreateInstance(plan.TimelineType);
            var persistent = false;
            try
            {
                var settings = plan.EditorSettingsProperty.GetValue(timeline, null);
                if (settings == null)
                    throw Error("E_TIMELINE_CREATE_FAILED", "Timeline editor settings were unavailable after instance creation.", plan.Path, null);
                plan.FrameRateProperty.SetValue(settings,
                    plan.FrameRateProperty.PropertyType == typeof(float) ? (object)(float)plan.FrameRate : plan.FrameRate,
                    null);
                if (plan.Existing && !AssetDatabase.DeleteAsset(plan.Path))
                    throw Error("E_TIMELINE_CREATE_FAILED", "Unity could not replace the existing Timeline asset.", plan.Path, plan.ExistingGuid);
                AssetToolSupport.CreateAssetDirectory(plan.Path);
                AssetDatabase.CreateAsset(timeline, plan.Path);
                persistent = true;
                EditorUtility.SetDirty(timeline);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(plan.Path, ImportAssetOptions.ForceSynchronousImport);
            }
            finally
            {
                if (!persistent && timeline != null) UnityEngine.Object.DestroyImmediate(timeline);
            }
        }

        /// <summary>fresh 加载 TimelineAsset，证明 path/GUID/type/frameRate 和 serialized file artifact。</summary>
        internal static TimelineAssetInfo ProveCreate(CreatePlan plan)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(plan.Path);
            var guid = AssetDatabase.AssetPathToGUID(plan.Path);
            var resolvedPath = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GUIDToAssetPath(guid);
            var frameRate = asset == null ? 0d : ReadFrameRate(asset, plan.TimelineType);
            var fullPath = AssetToolSupport.AssetFullPath(plan.Path);
            var file = new FileInfo(fullPath);
            if (asset == null || !plan.TimelineType.IsInstanceOfType(asset)
                || string.IsNullOrEmpty(guid)
                || !string.Equals(resolvedPath, plan.Path, StringComparison.Ordinal)
                || (plan.Existing && !string.Equals(guid, plan.ExistingGuid, StringComparison.Ordinal))
                || Math.Abs(frameRate - plan.FrameRate) > 0.000001d
                || !file.Exists || file.Length <= 0L)
                throw Error("E_TIMELINE_CREATE_PROOF_FAILED", "The created Timeline asset did not match its frozen path, GUID, type, frame rate, or file artifact.", resolvedPath, guid);
            return new TimelineAssetInfo
            {
                Path = resolvedPath,
                Guid = guid,
                Name = asset.name,
                Type = asset.GetType().FullName,
                FrameRate = frameRate,
                Duration = ReadDoubleProperty(asset, plan.TimelineType, "duration"),
                TrackCount = 0,
                SizeBytes = file.Length
            };
        }

        internal static TimelineAssetInfo Preview(CreatePlan plan)
        {
            return new TimelineAssetInfo
            {
                Path = plan.Path,
                Guid = plan.ExistingGuid,
                Name = Path.GetFileNameWithoutExtension(plan.Path),
                Type = plan.TimelineType.FullName,
                FrameRate = plan.FrameRate,
                Duration = 0d,
                TrackCount = 0,
                SizeBytes = 0L
            };
        }

        internal static Type RequireTimelineAssetType()
        {
            var type = Type.GetType(TimelineAssetTypeName, false);
            if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type))
                throw Error("E_TIMELINE_PACKAGE_UNAVAILABLE", "The installed project does not expose the TimelineAsset runtime type.", null, null);
            return type;
        }

        internal static string RequireTimelinePath(string rawPath)
        {
            var path = AssetToolSupport.RequireAssetPath(rawPath);
            if (!path.EndsWith(".playable", StringComparison.OrdinalIgnoreCase))
                throw Error("E_TIMELINE_PATH_INVALID", "Timeline asset path must end with .playable.", path, null);
            return path;
        }

        internal static double ReadFrameRate(object timeline, Type timelineType)
        {
            var settingsProperty = timelineType.GetProperty("editorSettings", BindingFlags.Public | BindingFlags.Instance);
            var settings = settingsProperty == null ? null : settingsProperty.GetValue(timeline, null);
            if (settings == null) return 0d;
            var property = settings.GetType().GetProperty("frameRate", BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanRead) return 0d;
            return Convert.ToDouble(property.GetValue(settings, null), CultureInfo.InvariantCulture);
        }

        internal static double ReadDoubleProperty(object target, Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanRead) return 0d;
            return Convert.ToDouble(property.GetValue(target, null), CultureInfo.InvariantCulture);
        }

        private static UnityCliBridge.BridgeCommandException Error(string code, string message, string path, string guid)
        {
            return new UnityCliBridge.BridgeCommandException(
                code,
                message,
                new Dictionary<string, object> { { "path", path }, { "guid", guid }, { "requiredPackage", PackageId } },
                new[] { "Run tools describe timeline.create and verify com.unity.timeline is installed before retrying." });
        }
    }

    public sealed class TimelineAssetInfo
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public double FrameRate { get; set; }
        public double Duration { get; set; }
        public long TrackCount { get; set; }
        public long SizeBytes { get; set; }
    }

    public sealed class TimelinePlannedEffect
    {
        public string Type { get; set; }
        public string Path { get; set; }
        public string Target { get; set; }
        public string SideEffects { get; set; }
    }
}
#endif
