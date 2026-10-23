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
        private static readonly string[] FrameRateMemberNames = { "frameRate", "fps" };

        internal sealed class CreatePlan
        {
            internal string Path;
            internal double FrameRate;
            internal bool Overwrite;
            internal bool Existing;
            internal string ExistingGuid;
            internal Type TimelineType;
            internal PropertyInfo EditorSettingsProperty;
            internal FrameRateMember FrameRateMember;
        }

        /// <summary>
        /// editorSettings 帧率成员的唯一版本分派点：Timeline 1.5+ 暴露 frameRate，更早版本只有 fps。
        /// 写入、fresh proof 与 mutation 结果读取都经此类型，禁止调用方自行按成员名反射。
        /// </summary>
        internal sealed class FrameRateMember
        {
            private readonly PropertyInfo property;

            internal FrameRateMember(PropertyInfo property)
            {
                this.property = property;
                IsSinglePrecision = property.PropertyType == typeof(float);
            }

            internal string Name { get { return property.Name; } }

            internal bool IsSinglePrecision { get; private set; }

            /// <summary>返回该成员真实持久化的值；float 成员先收敛到单精度，避免 proof 出现往返误差。</summary>
            internal double Persist(double value)
            {
                return IsSinglePrecision ? (double)(float)value : value;
            }

            internal double Read(object settings)
            {
                return Convert.ToDouble(property.GetValue(settings, null), CultureInfo.InvariantCulture);
            }

            internal void Write(object settings, double value)
            {
                property.SetValue(settings, IsSinglePrecision ? (object)(float)value : value, null);
            }
        }

        /// <summary>effect 前解析 Timeline API、输出路径、frame-rate member 与现有 asset identity。</summary>
        internal static CreatePlan PrepareCreate(string rawPath, double? requestedFrameRate, bool overwrite)
        {
            var timelineType = RequireTimelineAssetType();
            var path = RequireTimelinePath(rawPath);
            var requested = requestedFrameRate ?? 60d;
            if (double.IsNaN(requested) || double.IsInfinity(requested) || requested < 0.000001d || requested > 1000d)
                throw Error("E_TIMELINE_FRAME_RATE_INVALID", "Timeline frameRate must be finite and within [0.000001,1000].", path, null,
                    "Pass frameRate within [0.000001,1000] or omit it to use the default 60.");

            var settings = RequireEditorSettingsProperty(timelineType, path);
            var frameRateMember = RequireFrameRateMember(settings.PropertyType, timelineType, path);
            var frameRate = frameRateMember.Persist(requested);

            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            var orphanMetaPath = AssetToolSupport.AssetFullPath(path) + ".meta";
            if (existing == null && !AssetToolSupport.AssetExists(path) && File.Exists(orphanMetaPath))
                throw Error("E_TIMELINE_OUTPUT_META_EXISTS", "Timeline output metadata exists without its asset file.", path, null,
                    "Remove the orphan .meta file or choose another .playable path.");
            if (existing == null && AssetToolSupport.AssetExists(path))
                throw Error("E_TIMELINE_OUTPUT_TYPE_CONFLICT", "Timeline output exists but is not a readable main asset.", path, null,
                    "Choose another .playable path or remove the conflicting asset.");
            if (existing != null && !timelineType.IsInstanceOfType(existing))
                throw Error("E_TIMELINE_OUTPUT_TYPE_CONFLICT", "Timeline output path is occupied by a different asset type.", path, AssetDatabase.AssetPathToGUID(path),
                    "Choose another .playable path or move the existing asset.");
            if (existing != null && !overwrite)
                throw Error("E_TIMELINE_OUTPUT_EXISTS", "Timeline output already exists.", path, AssetDatabase.AssetPathToGUID(path),
                    "Pass overwrite=true to replace the existing Timeline asset, or choose another path.");
            return new CreatePlan
            {
                Path = path,
                FrameRate = frameRate,
                Overwrite = overwrite,
                Existing = existing != null,
                ExistingGuid = existing == null ? null : AssetDatabase.AssetPathToGUID(path),
                TimelineType = timelineType,
                EditorSettingsProperty = settings,
                FrameRateMember = frameRateMember
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
                    throw Error("E_TIMELINE_CREATE_FAILED", "Timeline editor settings were unavailable after instance creation.", plan.Path, null,
                        "Inspect the Unity Console for the Timeline creation failure, then retry.");
                plan.FrameRateMember.Write(settings, plan.FrameRate);
                if (plan.Existing && !AssetDatabase.DeleteAsset(plan.Path))
                    throw Error("E_TIMELINE_CREATE_FAILED", "Unity could not replace the existing Timeline asset.", plan.Path, plan.ExistingGuid,
                        "Check that the existing asset is not locked, then retry with overwrite=true.");
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
            var frameRate = asset == null ? 0d : ReadFrameRate(asset, plan.EditorSettingsProperty, plan.FrameRateMember);
            var fullPath = AssetToolSupport.AssetFullPath(plan.Path);
            var file = new FileInfo(fullPath);
            if (asset == null || !plan.TimelineType.IsInstanceOfType(asset)
                || string.IsNullOrEmpty(guid)
                || !string.Equals(resolvedPath, plan.Path, StringComparison.Ordinal)
                || (plan.Existing && !string.Equals(guid, plan.ExistingGuid, StringComparison.Ordinal))
                || !SameFrameRate(frameRate, plan.FrameRate, plan.FrameRateMember.IsSinglePrecision)
                || !file.Exists || file.Length <= 0L)
                throw Error("E_TIMELINE_CREATE_PROOF_FAILED", "The created Timeline asset did not match its frozen path, GUID, type, frame rate, or file artifact.", resolvedPath, guid,
                    "Run timeline.info on the path to inspect what Unity persisted, then retry with a fresh path.");
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
                throw Error("E_TIMELINE_PACKAGE_UNAVAILABLE", "The installed project does not expose the TimelineAsset runtime type.", null, null,
                    "Install com.unity.timeline in this project and reopen the Editor.");
            return type;
        }

        internal static string RequireTimelinePath(string rawPath)
        {
            var path = AssetToolSupport.RequireAssetPath(rawPath);
            if (!path.EndsWith(".playable", StringComparison.OrdinalIgnoreCase))
                throw Error("E_TIMELINE_PATH_INVALID", "Timeline asset path must end with .playable.", path, null,
                    "Pass an Assets/... .playable path.");
            return path;
        }

        /// <summary>定位 TimelineAsset.editorSettings；缺失说明包契约不可用，不是数据问题。</summary>
        internal static PropertyInfo RequireEditorSettingsProperty(Type timelineType, string path)
        {
            var settings = timelineType.GetProperty("editorSettings", BindingFlags.Public | BindingFlags.Instance);
            if (settings == null || !settings.CanRead)
                throw Error("E_TIMELINE_API_UNAVAILABLE", "The installed Timeline package does not expose TimelineAsset.editorSettings.", path, null,
                    "Update com.unity.timeline to a version that exposes TimelineAsset.editorSettings.");
            return settings;
        }

        /// <summary>按 frameRate -> fps 顺序解析唯一可读写帧率成员；两者都不可用时报告包版本边界。</summary>
        internal static FrameRateMember RequireFrameRateMember(Type settingsType, Type timelineType, string path)
        {
            var found = new List<string>();
            foreach (var name in FrameRateMemberNames)
            {
                var property = settingsType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanRead) continue;
                found.Add(name + " (" + property.PropertyType.Name + ")");
                if (!property.CanWrite) continue;
                if (property.PropertyType != typeof(double) && property.PropertyType != typeof(float)) continue;
                return new FrameRateMember(property);
            }
            throw FrameRateUnsupported(timelineType, path, found);
        }

        /// <summary>读取已创建或已变更 asset 的真实帧率；settings 缺失时不得用 0 冒充事实。</summary>
        internal static double ReadFrameRate(UnityEngine.Object timeline, PropertyInfo settingsProperty, FrameRateMember member)
        {
            var path = AssetDatabase.GetAssetPath(timeline);
            var settings = settingsProperty.GetValue(timeline, null);
            if (settings == null)
                throw Error("E_TIMELINE_API_UNAVAILABLE", "Timeline editorSettings resolved to null.", path, null,
                    "Run timeline.info on the path to inspect the asset, then retry.");
            return member.Read(settings);
        }

        /// <summary>float 成员按已收敛的期望值精确比较，double 成员保留 1e-6 容差。</summary>
        internal static bool SameFrameRate(double observed, double expected, bool singlePrecision)
        {
            return singlePrecision ? observed.Equals(expected) : Math.Abs(observed - expected) <= 0.000001d;
        }

        /// <summary>帧率成员完全缺失时的稳定错误：只有包版本边界，没有可写路径。</summary>
        private static UnityCliBridge.BridgeCommandException FrameRateUnsupported(Type timelineType, string path, List<string> found)
        {
            return new UnityCliBridge.BridgeCommandException(
                "E_TIMELINE_FRAME_RATE_UNSUPPORTED",
                "The installed Timeline package exposes neither a writable editorSettings.frameRate nor a writable editorSettings.fps member.",
                new Dictionary<string, object>
                {
                    { "path", path },
                    { "guid", null },
                    { "requiredPackage", PackageId },
                    { "packageVersion", ResolvePackageVersion(timelineType) },
                    { "foundMembers", found.ToArray() },
                    { "acceptedMembers", FrameRateMemberNames }
                },
                new[]
                {
                    "Repair com.unity.timeline in this project: versions from 1.5 expose editorSettings.frameRate and older versions expose editorSettings.fps.",
                    "Run tools describe timeline.create before retrying."
                });
        }

        /// <summary>包版本只用于错误诊断；解析失败不改变错误分类。</summary>
        private static string ResolvePackageVersion(Type timelineType)
        {
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(timelineType.Assembly);
                return package == null ? null : package.version;
            }
            catch
            {
                return null;
            }
        }

        internal static double ReadDoubleProperty(object target, Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanRead) return 0d;
            return Convert.ToDouble(property.GetValue(target, null), CultureInfo.InvariantCulture);
        }

        /// <summary>failure 必须携带该 code 自己的恢复动作，不能用包安装建议套用所有错误。</summary>
        private static UnityCliBridge.BridgeCommandException Error(string code, string message, string path, string guid, params string[] nextActions)
        {
            return new UnityCliBridge.BridgeCommandException(
                code,
                message,
                new Dictionary<string, object> { { "path", path }, { "guid", guid }, { "requiredPackage", PackageId } },
                nextActions == null || nextActions.Length == 0
                    ? new[] { "Run tools describe timeline.create before retrying." }
                    : nextActions);
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
