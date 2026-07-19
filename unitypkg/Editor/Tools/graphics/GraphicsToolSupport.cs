#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>
    /// graphics 域 IUnityCliTool 共享 helper:渲染管线摘要、QualitySettings、Volume 反射、
    /// 渲染统计、颜色解析、天空盒环境设置。仅供 graphics 域自包含工具复用。
    /// </summary>
    public static class GraphicsToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            SceneToolSupport.EnsureEditorNotBusy(command);
        }

        /// <summary>Color 转 #RRGGBBAA 十六进制字符串。</summary>
        public static string ColorHtml(Color color)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", (int)(color.r * 255f), (int)(color.g * 255f), (int)(color.b * 255f), (int)(color.a * 255f));
        }

        /// <summary>解析并校验颜色字符串。</summary>
        public static Color ParseColor(string value)
        {
            if (string.IsNullOrEmpty(value)) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_COLOR_INVALID", "Color is required.", "Pass #RRGGBB, #RRGGBBAA, or components in the range 0..1.");
            if (value.StartsWith("#"))
            {
                var hex = value.Substring(1);
                if (hex.Length != 6 && hex.Length != 8) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_COLOR_INVALID", "Invalid color: " + value, "Pass #RRGGBB or #RRGGBBAA.");
                if (hex.Length == 6) hex += "FF";
                int r, g, b, a;
                if (!int.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) || !int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) || !int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b) || !int.TryParse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a)) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_COLOR_INVALID", "Invalid color: " + value, "Pass hexadecimal color components.");
                return new Color(r / 255f, g / 255f, b / 255f, a / 255f);
            }
            var parts = value.Split(',');
            if (parts.Length != 3 && parts.Length != 4) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_COLOR_INVALID", "Invalid color: " + value, "Pass three or four components in the range 0..1.");
            return new Color(ParseFinite(parts[0], "color.r", 0f, 1f), ParseFinite(parts[1], "color.g", 0f, 1f), ParseFinite(parts[2], "color.b", 0f, 1f), parts.Length == 4 ? ParseFinite(parts[3], "color.a", 0f, 1f) : 1f);
        }

        public static float ParseFinite(string value, string property, float minimum, float maximum)
        {
            float parsed;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || float.IsNaN(parsed) || float.IsInfinity(parsed) || parsed < minimum || parsed > maximum)
                throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_RANGE_INVALID", "Invalid " + property + ": " + value, "Pass a finite value in the supported range.");
            return parsed;
        }

        public static T ParseEnum<T>(string raw, string property) where T : struct
        {
            T value;
            if (string.IsNullOrEmpty(raw) || !Enum.TryParse<T>(raw, true, out value) || !Enum.IsDefined(typeof(T), value)) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_ENUM_INVALID", "Invalid " + property + ": " + raw, "Pass a supported " + typeof(T).Name + " value.");
            return value;
        }

        public static string NormalizeAssetPathRequired(string raw, string extension)
        {
            var path = SceneToolSupport.NormalizeAssetPath(raw);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new UnityCliBridge.BridgeCommandException("E_GRAPHICS_ASSET_PATH_INVALID", "Invalid asset path: " + raw, "Pass an Assets/ path ending in " + extension + ".");
            return path;
        }

        /// <summary>保存并从记录的 Unity asset path/GUID 重新获取资产，避免使用写入前对象作为读回证明。</summary>
        public static T ReloadAsset<T>(string path, string guid) where T : UnityEngine.Object
        {
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            var resolvedPath = string.IsNullOrEmpty(guid) ? path : AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(resolvedPath) || (!string.IsNullOrEmpty(path) && !string.Equals(path, resolvedPath, StringComparison.Ordinal))) return null;
            return AssetDatabase.LoadAssetAtPath<T>(resolvedPath);
        }

        public static bool IsValidTextureLod(int value) { return value >= 0 && value <= 8; }
        public static bool IsValidMaximumLod(int value) { return value >= 0 && value <= 100000; }

        public static string FileSha256(byte[] bytes) { using (var sha = SHA256.Create()) return string.Concat(Array.ConvertAll(sha.ComputeHash(bytes), value => value.ToString("x2"))); }
        public static bool SameColor(Color actual, Color expected) { return Mathf.Approximately(actual.r, expected.r) && Mathf.Approximately(actual.g, expected.g) && Mathf.Approximately(actual.b, expected.b) && Mathf.Approximately(actual.a, expected.a); }

        /// <summary>解析 Vector3 数组字符串("x,y,z x,y,z ...")。</summary>
        public static Vector3[] ParseVector3Array(string value)
        {
            if (string.IsNullOrEmpty(value)) return new Vector3[0];
            var items = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<Vector3>();
            foreach (var item in items)
            {
                var trimmed = item.Trim('[', ']', '(', ')');
                if (!string.IsNullOrEmpty(trimmed)) list.Add(GameObjectToolSupport.ParseVector3(trimmed));
            }
            return list.ToArray();
        }

        /// <summary>解析 QualityLevel:数字或名称。</summary>
        public static int ResolveQualityLevel(string rawLevel)
        {
            int level;
            if (int.TryParse(rawLevel, NumberStyles.Integer, CultureInfo.InvariantCulture, out level))
            {
                if (level >= 0 && level < QualitySettings.names.Length) return level;
            }
            for (var i = 0; i < QualitySettings.names.Length; i++)
            {
                if (string.Equals(QualitySettings.names[i], rawLevel, StringComparison.OrdinalIgnoreCase)) return i;
            }
            throw new UnityCliBridge.BridgeCommandException("E_QUALITY_LEVEL_INVALID", "Quality level is invalid: " + rawLevel, "Run tools run graphics.quality --input '{}' to inspect available quality level names and indexes.");
        }

        /// <summary>构造渲染管线摘要 DTO。</summary>
        public static GraphicsSummary BuildGraphicsSummary()
        {
            var pipeline = GraphicsSettings.defaultRenderPipeline;
            return new GraphicsSummary
            {
                RenderPipeline = new RenderPipelineSummary
                {
                    Active = RenderPipelineSummaryName(),
                    DefaultAsset = pipeline == null ? null : pipeline.name,
                    DefaultAssetPath = pipeline == null ? null : SceneToolSupport.NormalizeAssetPath(AssetDatabase.GetAssetPath(pipeline))
                },
                CurrentQualityLevel = QualitySettings.GetQualityLevel(),
                QualityLevelName = QualitySettings.names.Length > QualitySettings.GetQualityLevel() ? QualitySettings.names[QualitySettings.GetQualityLevel()] : null
            };
        }

        private static string RenderPipelineSummaryName()
        {
            if (GraphicsSettings.currentRenderPipeline != null) return GraphicsSettings.currentRenderPipeline.GetType().Name;
            return GraphicsSettings.defaultRenderPipeline != null ? GraphicsSettings.defaultRenderPipeline.GetType().Name : "Built-in";
        }

        /// <summary>构造 QualitySettings DTO。</summary>
        public static QualitySettingsInfo BuildQualitySettings()
        {
            return new QualitySettingsInfo
            {
                Names = new List<string>(QualitySettings.names),
                CurrentLevel = QualitySettings.GetQualityLevel(),
                VSyncCount = QualitySettings.vSyncCount,
                AntiAliasing = QualitySettings.antiAliasing,
                ShadowDistance = QualitySettings.shadowDistance,
                MasterTextureLimit = QualitySettings.globalTextureMipmapLimit,
                LodBias = QualitySettings.lodBias,
                MaximumLODLevel = QualitySettings.maximumLODLevel,
                AnisotropicFiltering = QualitySettings.anisotropicFiltering.ToString(),
                Shadows = QualitySettings.shadows.ToString(),
                ShadowResolution = QualitySettings.shadowResolution.ToString(),
                RealtimeReflectionProbes = QualitySettings.realtimeReflectionProbes,
                SoftParticles = QualitySettings.softParticles
            };
        }

        /// <summary>解析 skybox 材质路径,允许 builtin:Default-Skybox。</summary>
        public static Material ResolveSkyboxMaterial(string path)
        {
            if (string.Equals(path, "builtin:Default-Skybox", StringComparison.OrdinalIgnoreCase) || string.Equals(path, "Default-Skybox", StringComparison.OrdinalIgnoreCase))
                return AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            return AssetDatabase.LoadAssetAtPath<Material>(SceneToolSupport.NormalizeAssetPath(path));
        }

        /// <summary>通过反射查找类型(跨程序集)。</summary>
        public static Type FindType(string fullName)
        {
            return GameObjectToolSupport.FindType(fullName);
        }

        /// <summary>反射读取对象成员值。</summary>
        public static object MemberValue(Type type, object obj, string memberName)
        {
            var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
            if (prop != null) return prop.GetValue(obj, null);
            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public);
            if (field != null) return field.GetValue(obj);
            return null;
        }

        /// <summary>构造 ReflectionProbe 摘要 DTO。</summary>
        public static ReflectionProbeInfo BuildReflectionProbeInfo(ReflectionProbe probe)
        {
#if UNITY_2023_2_OR_NEWER
            return new ReflectionProbeInfo { Mode = probe.mode.ToString(), Resolution = probe.resolution, Bounces = probe.bounces, Hdr = probe.hdr, Importance = probe.importance, Size = new Vector3Data(probe.size), Center = new Vector3Data(probe.center) };
#else
            return new ReflectionProbeInfo { Mode = probe.mode.ToString(), Resolution = probe.resolution, Bounces = RenderSettings.reflectionBounces, Hdr = probe.hdr, Importance = probe.importance, Size = new Vector3Data(probe.size), Center = new Vector3Data(probe.center) };
#endif
        }

        private struct RenderStatsSnapshot
        {
            public long DrawCalls, BatchedDrawCalls, Batches, SavedByBatches, Triangles, Vertices;
            public string ScreenRes;
            public long UsedTextureMemory, RenderTextureCount, RenderTextureBytes, UsedVertexBuffer, UsedVertexBufferVRAM, SetPassCalls;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static RenderStatsSnapshot CaptureRenderStats()
        {
#if UNITY_2023_2_OR_NEWER
            var stats = UnityEngine.Rendering.RenderStats.current;
            return new RenderStatsSnapshot
            {
                DrawCalls = stats.drawCalls, BatchedDrawCalls = stats.batchedDrawCalls, Batches = stats.batches,
                SavedByBatches = stats.savedByBatches, Triangles = stats.triangles, Vertices = stats.vertices,
                ScreenRes = stats.screenRes, UsedTextureMemory = stats.usedTextureMemory,
                RenderTextureCount = stats.renderTextureCount, RenderTextureBytes = stats.renderTextureBytes,
                UsedVertexBuffer = stats.usedVertexBuffer, UsedVertexBufferVRAM = stats.usedVertexBufferVRAM, SetPassCalls = stats.setPass
            };
#else
            var drawCalls = UnityEditor.UnityStats.drawCalls;
            var batches = UnityEditor.UnityStats.batches;
            var batchedDrawCalls = UnityEditor.UnityStats.dynamicBatchedDrawCalls + UnityEditor.UnityStats.staticBatchedDrawCalls + UnityEditor.UnityStats.instancedBatchedDrawCalls;
            return new RenderStatsSnapshot
            {
                DrawCalls = drawCalls, BatchedDrawCalls = batchedDrawCalls, Batches = batches, SavedByBatches = drawCalls - batches,
                Triangles = UnityEditor.UnityStats.triangles, Vertices = UnityEditor.UnityStats.vertices,
                ScreenRes = UnityEditor.UnityStats.screenRes, UsedTextureMemory = UnityEditor.UnityStats.usedTextureMemorySize,
                RenderTextureCount = UnityEditor.UnityStats.renderTextureCount, RenderTextureBytes = UnityEditor.UnityStats.renderTextureBytes,
                UsedVertexBuffer = UnityEditor.UnityStats.vboTotalBytes, UsedVertexBufferVRAM = 0, SetPassCalls = UnityEditor.UnityStats.setPassCalls
            };
#endif
        }

        /// <summary>构造渲染统计 DTO。</summary>
        public static RenderStatsInfo BuildRenderStats()
        {
            var s = CaptureRenderStats();
            return new RenderStatsInfo
            {
                DrawCalls = s.DrawCalls, BatchedDrawCalls = s.BatchedDrawCalls, Batches = s.Batches, SavedByBatching = s.SavedByBatches,
                Triangles = s.Triangles, Vertices = s.Vertices, ScreenRes = s.ScreenRes,
                UsedTextureMemory = s.UsedTextureMemory, RenderTextureCount = s.RenderTextureCount, RenderTextureBytes = s.RenderTextureBytes,
                UsedVertexBuffer = s.UsedVertexBuffer, UsedVertexBufferVRAM = s.UsedVertexBufferVRAM, SetPassCalls = s.SetPassCalls
            };
        }
    }

    // ── graphics 域 DTO ──

    public sealed class GraphicsSummary
    {
        public RenderPipelineSummary RenderPipeline { get; set; }
        public long CurrentQualityLevel { get; set; }
        public string QualityLevelName { get; set; }
    }

    public sealed class RenderPipelineSummary
    {
        public string Active { get; set; }
        public string DefaultAsset { get; set; }
        public string DefaultAssetPath { get; set; }
    }

    public sealed class QualitySettingsInfo
    {
        public List<string> Names { get; set; }
        public long CurrentLevel { get; set; }
        public long VSyncCount { get; set; }
        public long AntiAliasing { get; set; }
        public double ShadowDistance { get; set; }
        public long MasterTextureLimit { get; set; }
        public double LodBias { get; set; }
        public long MaximumLODLevel { get; set; }
        public string AnisotropicFiltering { get; set; }
        public string Shadows { get; set; }
        public string ShadowResolution { get; set; }
        public bool RealtimeReflectionProbes { get; set; }
        public bool SoftParticles { get; set; }
    }

    public sealed class RenderStatsInfo
    {
        public long DrawCalls { get; set; }
        public long BatchedDrawCalls { get; set; }
        public long Batches { get; set; }
        public long SavedByBatching { get; set; }
        public long Triangles { get; set; }
        public long Vertices { get; set; }
        public string ScreenRes { get; set; }
        public long UsedTextureMemory { get; set; }
        public long RenderTextureCount { get; set; }
        public long RenderTextureBytes { get; set; }
        public long UsedVertexBuffer { get; set; }
        public long UsedVertexBufferVRAM { get; set; }
        public long SetPassCalls { get; set; }
    }

    public sealed class VolumeInfo
    {
        public GameObjectTargetRef Target { get; set; }
        public bool Enabled { get; set; }
        public bool IsGlobal { get; set; }
        public double Priority { get; set; }
        public double Weight { get; set; }
        public AssetRef Profile { get; set; }
    }

    public sealed class AssetRef
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Guid { get; set; }
    }

    public sealed class LightingSettingsInfo
    {
        public string LightingMode { get; set; }
        public bool BakedGI { get; set; }
        public bool RealtimeGI { get; set; }
        public long DirectSamples { get; set; }
        public long IndirectSamples { get; set; }
        public long EnvironmentSamples { get; set; }
        public long Bounces { get; set; }
        public string Filtering { get; set; }
        public double IndirectResolution { get; set; }
    }

    public sealed class BakeModeInfo
    {
        public string LightingMode { get; set; }
        public long GiWorkflowMode { get; set; }
    }

    public sealed class ReflectionProbeInfo
    {
        public string Mode { get; set; }
        public long Resolution { get; set; }
        public long Bounces { get; set; }
        public bool Hdr { get; set; }
        public long Importance { get; set; }
        public Vector3Data Size { get; set; }
        public Vector3Data Center { get; set; }
    }

    public sealed class SkyboxInfo
    {
        public AssetRef Skybox { get; set; }
        public SunInfo Sun { get; set; }
        public AmbientInfo Ambient { get; set; }
        public FogInfo Fog { get; set; }
        public ReflectionInfo Reflection { get; set; }
    }

    public sealed class SunInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
    }

    public sealed class AmbientInfo
    {
        public string AmbientMode { get; set; }
        public double AmbientIntensity { get; set; }
        public Vector3Data AmbientLight { get; set; }
    }

    public sealed class FogInfo
    {
        public bool Enabled { get; set; }
        public string FogMode { get; set; }
        public string FogColor { get; set; }
        public double FogDensity { get; set; }
        public double FogStartDistance { get; set; }
        public double FogEndDistance { get; set; }
    }

    public sealed class ReflectionInfo
    {
        public string AmbientMode { get; set; }
        public long ReflectionBounces { get; set; }
        public double ReflectionIntensity { get; set; }
    }

    public sealed class GraphicsPlannedEffect
    {
        public string Type { get; set; }
        public string Property { get; set; }
        public string Value { get; set; }
        public string Target { get; set; }
    }

    public sealed class ScreenshotData
    {
        public string ScreenshotPath { get; set; }
        public string ArtifactPath { get; set; }
        public string Format { get; set; }
        public long Width { get; set; }
        public long Height { get; set; }
    }
}
#endif
