#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>Occlusion bake使用的effective settings readback。</summary>
    public sealed class OcclusionBakeSettingsData
    {
        public double SmallestOccluder { get; set; }
        public double SmallestHole { get; set; }
        public double BackfaceThreshold { get; set; }
    }

    /// <summary>Occlusion业务终态的有界fresh snapshot。</summary>
    public sealed class OcclusionBakeSnapshotData
    {
        public long UmbraDataSizeBytes { get; set; }
        public bool DoesSceneHaveManualPortals { get; set; }
        public OcclusionBakeSettingsData Settings { get; set; }
    }

    /// <summary>参与Occlusion持久化proof的scene文件身份。</summary>
    public sealed class OcclusionSceneFileData
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public bool Dirty { get; set; }
    }

    internal sealed class OcclusionSceneContentProofData
    {
        internal string Path;
        internal string ContentSha256;
    }

    public sealed class OcclusionPlannedEffect
    {
        public string Type { get; set; }
        public string Target { get; set; }
    }

    /// <summary>集中拥有Occlusion settings、scene identity和持久化proof。</summary>
    internal static class OcclusionToolSupport
    {
        private static readonly PropertyInfo GradientValueProperty = typeof(SerializedProperty).GetProperty("gradientValue", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        internal static void EnsureEditorReady(string tool, bool requireEditMode)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_BUSY", tool + " requires an idle Unity Editor.", "Wait for compilation/import to finish, then retry.");
            if (requireEditMode && (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode))
                throw new UnityCliBridge.BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE", tool + " is available only in Edit Mode.", "Exit Play Mode, then retry.");
        }

        internal static void EnsureNoActiveBake(string tool)
        {
            var status = UnityCliBridge.UnityCliOcclusionBakeCoordinator.PublicStatus();
            if (StaticOcclusionCulling.isRunning || string.Equals(Convert.ToString(status["state"], CultureInfo.InvariantCulture), "running", StringComparison.Ordinal))
                throw new UnityCliBridge.BridgeCommandException("E_OCCLUSION_BAKE_ACTIVE", tool + " cannot mutate occlusion data while a bake is running.", "Use occlusion.bake-status and occlusion.bake-cancel before retrying.");
        }

        /// <summary>effect前解析并验证全部effective bake settings。</summary>
        internal static OcclusionBakeSettingsData PrepareSettings(double? smallestOccluder, double? smallestHole, double? backfaceThreshold)
        {
            var current = ReadSettings();
            var result = new OcclusionBakeSettingsData
            {
                SmallestOccluder = smallestOccluder ?? current.SmallestOccluder,
                SmallestHole = smallestHole ?? current.SmallestHole,
                BackfaceThreshold = backfaceThreshold ?? current.BackfaceThreshold
            };
            RequireFiniteRange("smallestOccluder", result.SmallestOccluder, 0.000001d, 1000000d);
            RequireFiniteRange("smallestHole", result.SmallestHole, 0.000001d, 1000000d);
            RequireFiniteRange("backfaceThreshold", result.BackfaceThreshold, 1d, 100d);
            result.SmallestOccluder = (double)(float)result.SmallestOccluder;
            result.SmallestHole = (double)(float)result.SmallestHole;
            result.BackfaceThreshold = (double)(float)result.BackfaceThreshold;
            return result;
        }

        internal static OcclusionBakeSettingsData ReadSettings()
        {
            return new OcclusionBakeSettingsData
            {
                SmallestOccluder = StaticOcclusionCulling.smallestOccluder,
                SmallestHole = StaticOcclusionCulling.smallestHole,
                BackfaceThreshold = StaticOcclusionCulling.backfaceThreshold
            };
        }

        /// <summary>一次写入冻结的effective settings；调用方必须先持久化durable effect边界。</summary>
        internal static void ApplySettings(OcclusionBakeSettingsData settings)
        {
            if (settings == null) throw new UnityCliBridge.BridgeCommandException("E_SCHEMA_INVALID", "Occlusion bake settings are required.");
            StaticOcclusionCulling.smallestOccluder = checked((float)settings.SmallestOccluder);
            StaticOcclusionCulling.smallestHole = checked((float)settings.SmallestHole);
            StaticOcclusionCulling.backfaceThreshold = checked((float)settings.BackfaceThreshold);
        }

        internal static OcclusionBakeSnapshotData ReadSnapshot()
        {
            return new OcclusionBakeSnapshotData
            {
                UmbraDataSizeBytes = Math.Max(0L, Convert.ToInt64(StaticOcclusionCulling.umbraDataSize, CultureInfo.InvariantCulture)),
                DoesSceneHaveManualPortals = StaticOcclusionCulling.doesSceneHaveManualPortals,
                Settings = ReadSettings()
            };
        }

        internal static bool SettingsEqual(OcclusionBakeSettingsData left, OcclusionBakeSettingsData right)
        {
            return left != null && right != null
                && Close(left.SmallestOccluder, right.SmallestOccluder)
                && Close(left.SmallestHole, right.SmallestHole)
                && Close(left.BackfaceThreshold, right.BackfaceThreshold);
        }

        /// <summary>effect前冻结全部loaded saved clean scenes，禁止顺带保存既存dirty状态。</summary>
        internal static List<OcclusionSceneFileData> PrepareLoadedSceneFiles(string tool)
        {
            var result = new List<OcclusionSceneFileData>();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                if (string.IsNullOrEmpty(scene.path))
                    throw new UnityCliBridge.BridgeCommandException("E_OCCLUSION_SCENE_REQUIRED", tool + " requires every loaded target scene to be saved.", "Save or close untitled scenes, then retry.");
                if (scene.isDirty)
                    throw new UnityCliBridge.BridgeCommandException("E_OCCLUSION_SCENE_DIRTY", tool + " will not save a target scene that already has unrelated dirty changes: " + scene.path + ".", "Save or discard that scene's changes, then retry.");
                result.Add(ReadSceneFile(scene));
            }
            result = result.OrderBy(value => value.Path, StringComparer.Ordinal).ToList();
            if (result.Count == 0)
                throw new UnityCliBridge.BridgeCommandException("E_OCCLUSION_SCENE_REQUIRED", tool + " requires at least one loaded, saved scene.", "Save and load the target scene, then retry.");
            return result;
        }

        /// <summary>effect前重新解析全部loaded scene，确保冻结集合、GUID和文件内容没有漂移。</summary>
        internal static void ValidateFrozenSceneFiles(IEnumerable<OcclusionSceneFileData> frozen, string tool)
        {
            var expected = (frozen ?? Array.Empty<OcclusionSceneFileData>()).OrderBy(value => value.Path, StringComparer.Ordinal).ToList();
            var current = PrepareLoadedSceneFiles(tool);
            if (current.Count != expected.Count)
                throw TargetChanged(tool, "the loaded scene set changed before dispatch");
            for (var index = 0; index < expected.Count; index++)
            {
                var left = expected[index];
                var right = current[index];
                if (!string.Equals(left.Path, right.Path, StringComparison.Ordinal)
                    || !string.Equals(left.Guid, right.Guid, StringComparison.Ordinal)
                    || !string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal)
                    || left.SizeBytes != right.SizeBytes
                    || right.Dirty)
                    throw TargetChanged(tool, "a frozen scene identity or file changed before dispatch");
            }
        }

        /// <summary>冻结scene中全部GameObject和Component的稳定serialized graph，不暴露到公开结果。</summary>
        internal static List<OcclusionSceneContentProofData> CaptureSceneContentProofs(IEnumerable<OcclusionSceneFileData> scenes)
        {
            var result = new List<OcclusionSceneContentProofData>();
            foreach (var frozen in (scenes ?? Array.Empty<OcclusionSceneFileData>()).OrderBy(value => value.Path, StringComparer.Ordinal))
            {
                var scene = SceneManager.GetSceneByPath(frozen.Path);
                if (!scene.IsValid() || !scene.isLoaded || !string.Equals(AssetDatabase.AssetPathToGUID(frozen.Path), frozen.Guid, StringComparison.Ordinal))
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion target scene identity changed during content proof.", "Inspect the target scenes before deciding whether to retry.");
                result.Add(new OcclusionSceneContentProofData { Path = frozen.Path, ContentSha256 = SceneContentSha256(scene) });
            }
            return result;
        }

        /// <summary>重算冻结scene graph；任意外部对象修改必须在持久化前失败。</summary>
        internal static void ValidateFrozenSceneContentProofs(IEnumerable<OcclusionSceneContentProofData> frozen)
        {
            var expected = (frozen ?? Array.Empty<OcclusionSceneContentProofData>()).OrderBy(value => value.Path, StringComparer.Ordinal).ToList();
            var files = expected.Select(value =>
            {
                var scene = SceneManager.GetSceneByPath(value.Path);
                if (!scene.IsValid() || !scene.isLoaded)
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion target scene identity changed during content proof.", "Inspect the target scenes before deciding whether to retry.");
                return ReadSceneFile(scene);
            }).ToList();
            var current = CaptureSceneContentProofs(files);
            if (current.Count != expected.Count)
                throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion scene content proof set changed after the effect.", "Inspect the target scenes before deciding whether to retry.");
            for (var index = 0; index < expected.Count; index++)
                if (!string.Equals(expected[index].Path, current[index].Path, StringComparison.Ordinal)
                    || !string.Equals(expected[index].ContentSha256, current[index].ContentSha256, StringComparison.Ordinal))
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion target scene content changed outside the tracked effect.", "Inspect the target scene and preserve or discard its unsaved changes before retrying.");
        }

        /// <summary>effect后只保存冻结scene；serialized graph验证必须先于任何MarkSceneDirty或SaveScene。</summary>
        internal static List<OcclusionSceneFileData> SaveAndProveSceneFiles(IEnumerable<OcclusionSceneFileData> before, IEnumerable<OcclusionSceneContentProofData> contentProofs, bool requireChangedHash)
        {
            var frozenFiles = (before ?? Array.Empty<OcclusionSceneFileData>()).OrderBy(value => value.Path, StringComparer.Ordinal).ToList();
            var frozenContent = (contentProofs ?? Array.Empty<OcclusionSceneContentProofData>()).OrderBy(value => value.Path, StringComparer.Ordinal).ToList();
            if (frozenFiles.Count != frozenContent.Count || frozenFiles.Where((value, index) => !string.Equals(value.Path, frozenContent[index].Path, StringComparison.Ordinal)).Any())
                throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion scene content proof does not match the frozen target set.", "Inspect the target scenes before deciding whether to retry.");
            ValidateFrozenSceneContentProofs(contentProofs);
            var result = new List<OcclusionSceneFileData>();
            foreach (var frozen in frozenFiles)
            {
                var scene = SceneManager.GetSceneByPath(frozen.Path);
                if (!scene.IsValid() || !scene.isLoaded || !string.Equals(AssetDatabase.AssetPathToGUID(frozen.Path), frozen.Guid, StringComparison.Ordinal))
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion target scene identity changed before persistence proof.", "Inspect the target scenes before deciding whether to retry.");
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene))
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion target scene could not be saved after the effect.", "Inspect the target scene and Unity Console before deciding whether to retry.");
                var observed = ReadSceneFile(scene);
                if (observed.Dirty || (requireChangedHash && string.Equals(observed.Sha256, frozen.Sha256, StringComparison.Ordinal)))
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion scene persistence proof did not reach the required serialized terminal.", "Inspect the target scene file before deciding whether to retry.");
                result.Add(observed);
            }
            return result;
        }

        internal static Dictionary<string, object> SettingsDictionary(OcclusionBakeSettingsData value)
        {
            return new Dictionary<string, object>
            {
                { "smallestOccluder", value.SmallestOccluder },
                { "smallestHole", value.SmallestHole },
                { "backfaceThreshold", value.BackfaceThreshold }
            };
        }

        internal static Dictionary<string, object> SnapshotDictionary(OcclusionBakeSnapshotData value)
        {
            return new Dictionary<string, object>
            {
                { "umbraDataSizeBytes", value.UmbraDataSizeBytes },
                { "doesSceneHaveManualPortals", value.DoesSceneHaveManualPortals },
                { "settings", SettingsDictionary(value.Settings) }
            };
        }

        internal static Dictionary<string, object> SceneDictionary(OcclusionSceneFileData value)
        {
            return new Dictionary<string, object>
            {
                { "path", value.Path }, { "guid", value.Guid }, { "sha256", value.Sha256 },
                { "sizeBytes", value.SizeBytes }, { "dirty", value.Dirty }
            };
        }

        internal static Dictionary<string, object> SceneContentProofDictionary(OcclusionSceneContentProofData value)
        {
            return new Dictionary<string, object> { { "path", value.Path }, { "contentSha256", value.ContentSha256 } };
        }

        internal static string Sha256(byte[] value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(value ?? Array.Empty<byte>())).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static OcclusionSceneFileData ReadSceneFile(Scene scene)
        {
            var root = Path.GetDirectoryName(Application.dataPath);
            var fullPath = Path.Combine(root ?? string.Empty, scene.path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) throw new UnityCliBridge.BridgeCommandException("E_OCCLUSION_SCENE_REQUIRED", "Occlusion target scene file is missing: " + scene.path + ".");
            var guid = AssetDatabase.AssetPathToGUID(scene.path);
            if (string.IsNullOrEmpty(guid)) throw new UnityCliBridge.BridgeCommandException("E_OCCLUSION_SCENE_REQUIRED", "Occlusion target scene GUID is unavailable: " + scene.path + ".");
            var bytes = File.ReadAllBytes(fullPath);
            return new OcclusionSceneFileData { Path = scene.path, Guid = guid, Sha256 = Sha256(bytes), SizeBytes = bytes.LongLength, Dirty = scene.isDirty };
        }

        /// <summary>按稳定hierarchy顺序和GlobalObjectId散列全部serialized property，排除bake私有scene payload。</summary>
        private static string SceneContentSha256(Scene scene)
        {
            try
            {
                using (var hash = SHA256.Create())
                {
                    Action<string> append = record =>
                    {
                        var bytes = Encoding.UTF8.GetBytes((record ?? string.Empty).Length.ToString(CultureInfo.InvariantCulture) + ":" + (record ?? string.Empty) + "\n");
                        hash.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                    };
                    Action<UnityEngine.Object, string> appendObject = (value, identity) =>
                    {
                        append("object|" + identity + "|" + ObjectIdentity(value) + "|" + value.GetType().AssemblyQualifiedName);
                        var serialized = new SerializedObject(value);
                        var property = serialized.GetIterator();
                        while (property.Next(true))
                            append("property|" + property.depth.ToString(CultureInfo.InvariantCulture) + "|" + property.propertyPath + "|" + property.propertyType + "|" + SerializedPropertyValue(property));
                    };
                    Action<GameObject, string> visit = null;
                    visit = (gameObject, hierarchy) =>
                    {
                        appendObject(gameObject, "gameObject|" + hierarchy);
                        var components = gameObject.GetComponents<Component>();
                        for (var index = 0; index < components.Length; index++)
                        {
                            var component = components[index];
                            if (component == null) append("missingComponent|" + hierarchy + "|" + index.ToString(CultureInfo.InvariantCulture));
                            else appendObject(component, "component|" + hierarchy + "|" + index.ToString(CultureInfo.InvariantCulture));
                        }
                        for (var index = 0; index < gameObject.transform.childCount; index++)
                            visit(gameObject.transform.GetChild(index).gameObject, hierarchy + "/" + index.ToString(CultureInfo.InvariantCulture));
                    };
                    var roots = scene.GetRootGameObjects().OrderBy(value => value.transform.GetSiblingIndex()).ThenBy(ObjectIdentity, StringComparer.Ordinal).ToArray();
                    for (var index = 0; index < roots.Length; index++) visit(roots[index], index.ToString(CultureInfo.InvariantCulture));
                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    return BitConverter.ToString(hash.Hash).Replace("-", string.Empty).ToLowerInvariant();
                }
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "Occlusion scene content proof could not be captured: " + exception.GetType().Name + ".", "Inspect the target scene and Unity Console before deciding whether to retry.");
            }
        }

        private static string ObjectIdentity(UnityEngine.Object value)
        {
            return value == null ? "null" : GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();
        }

        private static string SerializedPropertyValue(SerializedProperty property)
        {
            try
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer: return property.longValue.ToString(CultureInfo.InvariantCulture);
                    case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                    case SerializedPropertyType.Float: return property.doubleValue.ToString("R", CultureInfo.InvariantCulture);
                    case SerializedPropertyType.String: return property.stringValue ?? string.Empty;
                    case SerializedPropertyType.Color: return Float4(property.colorValue.r, property.colorValue.g, property.colorValue.b, property.colorValue.a);
                    case SerializedPropertyType.ObjectReference: return ObjectIdentity(property.objectReferenceValue);
                    case SerializedPropertyType.LayerMask: return property.intValue.ToString(CultureInfo.InvariantCulture);
                    case SerializedPropertyType.Enum: return property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                    case SerializedPropertyType.Vector2: return Float4(property.vector2Value.x, property.vector2Value.y, 0f, 0f);
                    case SerializedPropertyType.Vector3: return Float4(property.vector3Value.x, property.vector3Value.y, property.vector3Value.z, 0f);
                    case SerializedPropertyType.Vector4: return Float4(property.vector4Value.x, property.vector4Value.y, property.vector4Value.z, property.vector4Value.w);
                    case SerializedPropertyType.Rect: return Float4(property.rectValue.x, property.rectValue.y, property.rectValue.width, property.rectValue.height);
                    case SerializedPropertyType.ArraySize: return property.intValue.ToString(CultureInfo.InvariantCulture);
                    case SerializedPropertyType.Character: return property.intValue.ToString(CultureInfo.InvariantCulture);
                    case SerializedPropertyType.AnimationCurve: return AnimationCurveValue(property.animationCurveValue);
                    case SerializedPropertyType.Bounds: return Float4(property.boundsValue.center.x, property.boundsValue.center.y, property.boundsValue.center.z, 0f) + "|" + Float4(property.boundsValue.size.x, property.boundsValue.size.y, property.boundsValue.size.z, 0f);
                    case SerializedPropertyType.Gradient: return GradientValue(property);
                    case SerializedPropertyType.Quaternion: return Float4(property.quaternionValue.x, property.quaternionValue.y, property.quaternionValue.z, property.quaternionValue.w);
                    case SerializedPropertyType.ExposedReference: return ObjectIdentity(property.exposedReferenceValue);
                    case SerializedPropertyType.FixedBufferSize: return property.fixedBufferSize.ToString(CultureInfo.InvariantCulture);
                    case SerializedPropertyType.Vector2Int: return property.vector2IntValue.x.ToString(CultureInfo.InvariantCulture) + "," + property.vector2IntValue.y.ToString(CultureInfo.InvariantCulture);
                    case SerializedPropertyType.Vector3Int: return property.vector3IntValue.x.ToString(CultureInfo.InvariantCulture) + "," + property.vector3IntValue.y.ToString(CultureInfo.InvariantCulture) + "," + property.vector3IntValue.z.ToString(CultureInfo.InvariantCulture);
                    case SerializedPropertyType.RectInt: return property.rectIntValue.ToString();
                    case SerializedPropertyType.BoundsInt: return property.boundsIntValue.ToString();
                    case SerializedPropertyType.ManagedReference: return property.managedReferenceFullTypename + "|" + property.managedReferenceId.ToString(CultureInfo.InvariantCulture);
                    case SerializedPropertyType.Hash128: return property.hash128Value.ToString();
                    default: return property.type ?? string.Empty;
                }
            }
            catch (Exception exception) { return "unreadable:" + exception.GetType().Name; }
        }

        /// <summary>完整编码曲线wrap与全部keyframe切线/权重。</summary>
        private static string AnimationCurveValue(AnimationCurve curve)
        {
            if (curve == null) return "null";
            var builder = new StringBuilder();
            builder.Append(curve.preWrapMode).Append('|').Append(curve.postWrapMode);
            foreach (var key in curve.keys)
            {
                builder.Append('|').Append(key.time.ToString("R", CultureInfo.InvariantCulture));
                builder.Append(',').Append(key.value.ToString("R", CultureInfo.InvariantCulture));
                builder.Append(',').Append(key.inTangent.ToString("R", CultureInfo.InvariantCulture));
                builder.Append(',').Append(key.outTangent.ToString("R", CultureInfo.InvariantCulture));
                builder.Append(',').Append(key.inWeight.ToString("R", CultureInfo.InvariantCulture));
                builder.Append(',').Append(key.outWeight.ToString("R", CultureInfo.InvariantCulture));
                builder.Append(',').Append(key.weightedMode);
            }
            return builder.ToString();
        }

        /// <summary>完整编码Gradient mode、color keys与alpha keys。</summary>
        private static string GradientValue(SerializedProperty property)
        {
            var gradient = GradientValueProperty == null ? null : GradientValueProperty.GetValue(property, null) as Gradient;
            if (gradient == null) return "null";
            var builder = new StringBuilder(gradient.mode.ToString());
            foreach (var key in gradient.colorKeys)
                builder.Append('|').Append(key.time.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(Float4(key.color.r, key.color.g, key.color.b, key.color.a));
            foreach (var key in gradient.alphaKeys)
                builder.Append('|').Append(key.time.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(key.alpha.ToString("R", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        private static string Float4(float x, float y, float z, float w)
        {
            return x.ToString("R", CultureInfo.InvariantCulture) + "," + y.ToString("R", CultureInfo.InvariantCulture) + "," + z.ToString("R", CultureInfo.InvariantCulture) + "," + w.ToString("R", CultureInfo.InvariantCulture);
        }

        private static UnityCliBridge.BridgeCommandException TargetChanged(string tool, string reason)
        {
            return new UnityCliBridge.BridgeCommandException("E_OCCLUSION_TARGET_CHANGED", tool + " refused to dispatch because " + reason + ".", "Inspect the loaded scenes, then submit a new request with a fresh plan.");
        }

        private static void RequireFiniteRange(string field, double value, double minimum, double maximum)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < minimum || value > maximum)
                throw new UnityCliBridge.BridgeCommandException("E_OCCLUSION_SETTINGS_VALUE_INVALID", field + " must be finite in [" + minimum.ToString("R", CultureInfo.InvariantCulture) + "," + maximum.ToString("R", CultureInfo.InvariantCulture) + "].", "Pass a value inside the documented range.");
        }

        private static bool Close(double left, double right) { return Math.Abs(left - right) <= 0.00001d; }
    }
}
#endif
