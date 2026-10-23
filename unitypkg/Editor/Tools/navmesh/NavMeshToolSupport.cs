#if UNITY_EDITOR
#pragma warning disable 0618
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>NavMesh settings 的公开 typed readback。</summary>
    public sealed class NavMeshSettingsData
    {
        public bool Available { get; set; }
        public double AgentRadius { get; set; }
        public double AgentHeight { get; set; }
        public double AgentSlope { get; set; }
        public double AgentClimb { get; set; }
        public double MinRegionArea { get; set; }
        public double VoxelSize { get; set; }
        public bool ManualVoxelSize { get; set; }
        public string SettingsPath { get; set; }
        public string SettingsHash { get; set; }
        public long SettingsSizeBytes { get; set; }
    }

    /// <summary>当前 NavMesh world triangulation 的有界摘要。</summary>
    public sealed class NavMeshTopologyData
    {
        public long VertexCount { get; set; }
        public long IndexCount { get; set; }
        public string Sha256 { get; set; }
    }

    public sealed class NavMeshPlannedEffect
    {
        public string Type { get; set; }
        public string Target { get; set; }
    }

    /// <summary>参与 legacy/surface 持久化 proof 的 scene 文件身份。</summary>
    public sealed class NavMeshSceneFileData
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string Sha256 { get; set; }
        public long SizeBytes { get; set; }
        public bool Dirty { get; set; }
    }

    /// <summary>legacy settings、topology 与 editor guard 的共享实现。</summary>
    internal static class NavMeshToolSupport
    {
        internal const string NavigationPackageId = "com.unity.ai.navigation";
        private const string SettingsRelativePath = "ProjectSettings/NavMeshAreas.asset";
        private const string SettingsRootProperty = "m_Settings";
        private const string PropAgentRadius = "agentRadius";
        private const string PropAgentHeight = "agentHeight";
        private const string PropAgentSlope = "agentSlope";
        private const string PropAgentClimb = "agentClimb";
        private const string PropMinRegionArea = "minRegionArea";
        private const string PropManualCellSize = "manualCellSize";
        private const string PropCellSize = "cellSize";

        internal sealed class SettingsWritePlan
        {
            internal NavMeshSettingsData Before;
            internal NavMeshSettingsData Desired;
            internal UnityEngine.Object SettingsObject;
            internal List<string> Fields;
            internal bool Changed;
        }

        internal static void EnsureEditorReady(string tool, bool requireEditMode)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_BUSY", tool + " requires an idle Unity Editor.", "Wait for compilation/import to finish, then retry.");
            if (requireEditMode && (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode))
                throw new UnityCliBridge.BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE", tool + " is available only in Edit Mode.", "Exit Play Mode, then retry.");
        }

        internal static void EnsureNoActiveBake(string tool)
        {
            var status = UnityCliBridge.UnityCliNavMeshBakeCoordinator.PublicStatus();
            if (UnityCliBridge.UnityCliNavMeshBakeCoordinator.HasActiveNativeBake()
                || string.Equals(Convert.ToString(status["state"], CultureInfo.InvariantCulture), "running", StringComparison.Ordinal))
                throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_BAKE_ACTIVE", tool + " cannot mutate NavMesh state while a tracked or untracked native bake is running.", "Use navmesh.bake-status and wait for all native NavMesh work to finish before retrying.");
        }

        /// <summary>必需 serialized property 缺失时 fail closed，不使用猜测默认值。</summary>
        internal static NavMeshSettingsData ReadSettings()
        {
            var settingsObject = ProjectSettingsObject();
            var serialized = new SerializedObject(settingsObject);
            serialized.Update();
            var prefix = DefaultAgentPrefix(serialized);
            var data = new NavMeshSettingsData
            {
                Available = true,
                AgentRadius = Float(serialized, prefix + "." + PropAgentRadius),
                AgentHeight = Float(serialized, prefix + "." + PropAgentHeight),
                AgentSlope = Float(serialized, prefix + "." + PropAgentSlope),
                AgentClimb = Float(serialized, prefix + "." + PropAgentClimb),
                MinRegionArea = Float(serialized, prefix + "." + PropMinRegionArea),
                VoxelSize = Float(serialized, prefix + "." + PropCellSize),
                ManualVoxelSize = Bool(serialized, prefix + "." + PropManualCellSize),
                SettingsPath = SettingsRelativePath
            };
            PopulateSettingsFile(data);
            return data;
        }

        /// <summary>effect 前冻结完整 requested settings 与当前 settings 文件身份。</summary>
        internal static SettingsWritePlan PrepareSettings(
            double? agentRadius,
            double? agentHeight,
            double? agentSlope,
            double? agentClimb,
            double? minRegionArea,
            double? voxelSize,
            bool? manualVoxelSize)
        {
            var fields = new List<string>();
            var before = ReadSettings();
            var desired = Copy(before);
            ApplyRequested("agentRadius", agentRadius, 0.000001d, 1000d, fields, value => desired.AgentRadius = value);
            ApplyRequested("agentHeight", agentHeight, 0.000001d, 1000d, fields, value => desired.AgentHeight = value);
            ApplyRequested("agentSlope", agentSlope, 0d, 60d, fields, value => desired.AgentSlope = value);
            ApplyRequested("agentClimb", agentClimb, 0d, 1000d, fields, value => desired.AgentClimb = value);
            ApplyRequested("minRegionArea", minRegionArea, 0d, 1000000000d, fields, value => desired.MinRegionArea = value);
            ApplyRequested("voxelSize", voxelSize, 0.000001d, 1000d, fields, value => { desired.VoxelSize = value; desired.ManualVoxelSize = true; });
            if (manualVoxelSize.HasValue) { fields.Add("manualVoxelSize"); desired.ManualVoxelSize = manualVoxelSize.Value; }
            if (fields.Count == 0)
                throw new UnityCliBridge.BridgeCommandException("E_SCHEMA_INVALID", "navmesh.settings-set requires at least one settings field.", "Pass one or more fields returned by navmesh.settings.");
            var settingsObject = ProjectSettingsObject();
            return new SettingsWritePlan
            {
                Before = before,
                Desired = desired,
                SettingsObject = settingsObject,
                Fields = fields.Distinct(StringComparer.Ordinal).ToList(),
                Changed = fields.Any(field => !FieldEquals(field, before, desired))
            };
        }

        /// <summary>一次写入所有冻结字段并保存 ProjectSettings，调用方负责 effect barrier。</summary>
        internal static void ApplySettings(SettingsWritePlan plan)
        {
            var serialized = new SerializedObject(plan.SettingsObject);
            serialized.Update();
            var prefix = DefaultAgentPrefix(serialized);
            foreach (var field in plan.Fields)
            {
                switch (field)
                {
                    case "agentRadius": SetFloat(serialized, prefix + "." + PropAgentRadius, plan.Desired.AgentRadius); break;
                    case "agentHeight": SetFloat(serialized, prefix + "." + PropAgentHeight, plan.Desired.AgentHeight); break;
                    case "agentSlope": SetFloat(serialized, prefix + "." + PropAgentSlope, plan.Desired.AgentSlope); break;
                    case "agentClimb": SetFloat(serialized, prefix + "." + PropAgentClimb, plan.Desired.AgentClimb); break;
                    case "minRegionArea": SetFloat(serialized, prefix + "." + PropMinRegionArea, plan.Desired.MinRegionArea); break;
                    case "voxelSize": SetFloat(serialized, prefix + "." + PropCellSize, plan.Desired.VoxelSize); SetBool(serialized, prefix + "." + PropManualCellSize, true); break;
                    case "manualVoxelSize": SetBool(serialized, prefix + "." + PropManualCellSize, plan.Desired.ManualVoxelSize); break;
                    default: throw new InvalidOperationException("Unexpected NavMesh settings field: " + field);
                }
            }
            if (!serialized.ApplyModifiedPropertiesWithoutUndo())
                throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SETTINGS_WRITE_FAILED", "Unity did not apply the requested NavMesh settings.", "Inspect ProjectSettings/NavMeshAreas.asset and Unity Console before retrying.");
            EditorUtility.SetDirty(plan.SettingsObject);
            AssetDatabase.SaveAssets();
        }

        internal static NavMeshSettingsData ProveSettings(SettingsWritePlan plan)
        {
            var after = ReadSettings();
            foreach (var field in plan.Fields)
                if (!FieldEquals(field, after, plan.Desired))
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh settings terminal readback does not match the requested values.", "Run navmesh.settings before deciding whether to retry.");
            if (after.SettingsSizeBytes <= 0 || string.IsNullOrEmpty(after.SettingsHash) || !string.Equals(after.SettingsPath, plan.Before.SettingsPath, StringComparison.Ordinal)
                || (plan.Changed && string.Equals(after.SettingsHash, plan.Before.SettingsHash, StringComparison.Ordinal)))
                throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh settings persistence could not be proven.", "Inspect ProjectSettings/NavMeshAreas.asset before deciding whether to retry.");
            return after;
        }

        /// <summary>effect 前冻结全部 loaded saved scene；任何既存 dirty scene 都禁止被 bake/clear 顺带保存。</summary>
        internal static List<NavMeshSceneFileData> PrepareLoadedSceneFiles(string tool)
        {
            var paths = new List<string>();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid() || !scene.isLoaded) continue;
                if (string.IsNullOrEmpty(scene.path))
                    throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SCENE_REQUIRED", tool + " requires every loaded target scene to be saved.", "Save or close untitled scenes, then retry.");
                paths.Add(scene.path);
            }
            return PrepareSceneFiles(paths, tool);
        }

        internal static List<NavMeshSceneFileData> PrepareSceneFiles(IEnumerable<string> paths, string tool)
        {
            var result = new List<NavMeshSceneFileData>();
            foreach (var path in (paths ?? Array.Empty<string>()).Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
            {
                var scene = SceneManager.GetSceneByPath(path);
                if (!scene.IsValid() || !scene.isLoaded)
                    throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SCENE_REQUIRED", tool + " target scene is not loaded: " + path + ".");
                if (scene.isDirty)
                    throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SCENE_DIRTY", tool + " will not save a target scene that already has unrelated dirty changes: " + path + ".", "Save or discard that scene's changes, then retry.");
                result.Add(ReadSceneFile(scene));
            }
            if (result.Count == 0)
                throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SCENE_REQUIRED", tool + " requires at least one loaded, saved scene.", "Save and load the target scene, then retry.");
            return result;
        }

        /// <summary>effect前或terminal readback验证冻结scene identity与文件内容未被外部修改。</summary>
        internal static void ValidateFrozenSceneFiles(IEnumerable<NavMeshSceneFileData> frozenScenes, string tool)
        {
            foreach (var frozen in frozenScenes ?? Array.Empty<NavMeshSceneFileData>())
            {
                var scene = SceneManager.GetSceneByPath(frozen.Path);
                if (!scene.IsValid() || !scene.isLoaded || scene.isDirty
                    || !string.Equals(AssetDatabase.AssetPathToGUID(frozen.Path), frozen.Guid, StringComparison.Ordinal))
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", tool + " target scene identity or dirty state changed.");
                var observed = ReadSceneFile(scene);
                if (observed.SizeBytes != frozen.SizeBytes || !string.Equals(observed.Sha256, frozen.Sha256, StringComparison.Ordinal))
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", tool + " target scene file changed outside the frozen request.");
            }
        }

        /// <summary>effect 后只保存冻结 scene，并证明 GUID/path/clean/file artifact；不保存其他 loaded scene。</summary>
        internal static List<NavMeshSceneFileData> SaveAndProveSceneFiles(IEnumerable<NavMeshSceneFileData> before, bool requireChangedHash)
        {
            var result = new List<NavMeshSceneFileData>();
            foreach (var frozen in before ?? Array.Empty<NavMeshSceneFileData>())
            {
                var scene = SceneManager.GetSceneByPath(frozen.Path);
                if (!scene.IsValid() || !scene.isLoaded || !string.Equals(AssetDatabase.AssetPathToGUID(frozen.Path), frozen.Guid, StringComparison.Ordinal))
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh target scene identity changed before persistence proof.", "Inspect the target scenes before deciding whether to retry.");
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene))
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh target scene could not be saved after the effect.", "Inspect the target scene and Unity Console before deciding whether to retry.");
                var observed = ReadSceneFile(scene);
                if (observed.Dirty || (requireChangedHash && string.Equals(observed.Sha256, frozen.Sha256, StringComparison.Ordinal)))
                    throw new UnityCliBridge.BridgeCommandException("E_EXECUTION_TIMEOUT", "NavMesh scene persistence proof did not change the serialized target.", "Inspect the target scene file before deciding whether to retry.");
                result.Add(observed);
            }
            return result;
        }

        internal static Dictionary<string, object> SceneDictionary(NavMeshSceneFileData value)
        {
            return new Dictionary<string, object> { { "path", value.Path }, { "guid", value.Guid }, { "sha256", value.Sha256 }, { "sizeBytes", value.SizeBytes }, { "dirty", value.Dirty } };
        }

        private static NavMeshSceneFileData ReadSceneFile(Scene scene)
        {
            var root = Path.GetDirectoryName(Application.dataPath);
            var fullPath = Path.Combine(root ?? string.Empty, scene.path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath)) throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SCENE_REQUIRED", "NavMesh target scene file is missing: " + scene.path + ".");
            var guid = AssetDatabase.AssetPathToGUID(scene.path);
            if (string.IsNullOrEmpty(guid)) throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SCENE_REQUIRED", "NavMesh target scene GUID is unavailable: " + scene.path + ".");
            var bytes = File.ReadAllBytes(fullPath);
            return new NavMeshSceneFileData { Path = scene.path, Guid = guid, Sha256 = Sha256(bytes), SizeBytes = bytes.LongLength, Dirty = scene.isDirty };
        }

        internal static NavMeshTopologyData ReadTopology()
        {
            var triangulation = NavMesh.CalculateTriangulation();
            var vertices = triangulation.vertices ?? Array.Empty<Vector3>();
            var indices = triangulation.indices ?? Array.Empty<int>();
            var text = new StringBuilder();
            for (var i = 0; i < vertices.Length; i++)
                text.Append(vertices[i].x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(vertices[i].y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(vertices[i].z.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            text.Append('|');
            for (var i = 0; i < indices.Length; i++) text.Append(indices[i].ToString(CultureInfo.InvariantCulture)).Append(',');
            return new NavMeshTopologyData { VertexCount = vertices.LongLength, IndexCount = indices.LongLength, Sha256 = Sha256(Encoding.UTF8.GetBytes(text.ToString())) };
        }

        internal static Dictionary<string, object> TopologyDictionary()
        {
            var value = ReadTopology();
            return new Dictionary<string, object> { { "vertexCount", value.VertexCount }, { "indexCount", value.IndexCount }, { "sha256", value.Sha256 } };
        }

        internal static bool TopologyEmpty(NavMeshTopologyData value)
        {
            return value != null && value.VertexCount == 0 && value.IndexCount == 0;
        }

        private static void PopulateSettingsFile(NavMeshSettingsData data)
        {
            var root = Path.GetDirectoryName(Application.dataPath);
            var path = Path.Combine(root ?? string.Empty, SettingsRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SETTINGS_UNAVAILABLE", "ProjectSettings/NavMeshAreas.asset is unavailable.", "Open the project in Unity and inspect Navigation settings.");
            var bytes = File.ReadAllBytes(path);
            data.SettingsHash = Sha256(bytes);
            data.SettingsSizeBytes = bytes.LongLength;
        }

        /// <summary>集中绑定 Unity editor 的 NavMeshProjectSettings singleton；缺失 API 或 default agent 时 fail closed。</summary>
        private static UnityEngine.Object ProjectSettingsObject()
        {
            var type = typeof(Editor).Assembly.GetType("UnityEditor.Unsupported", false);
            var method = type == null ? null : type.GetMethod("GetSerializedAssetInterfaceSingleton", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            var value = method == null ? null : method.Invoke(null, new object[] { "NavMeshProjectSettings" }) as UnityEngine.Object;
            if (value == null)
                throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SETTINGS_SCHEMA_UNSUPPORTED", "Unity NavMeshProjectSettings singleton API is unavailable.", "Use a Unity version supported by this CLI build.");
            return value;
        }

        private static string DefaultAgentPrefix(SerializedObject serialized)
        {
            var settings = serialized.FindProperty(SettingsRootProperty);
            if (settings == null || !settings.isArray)
                throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SETTINGS_SCHEMA_UNSUPPORTED", "Unity NavMeshProjectSettings.m_Settings is unavailable.");
            for (var index = 0; index < settings.arraySize; index++)
            {
                var element = settings.GetArrayElementAtIndex(index);
                var id = element == null ? null : element.FindPropertyRelative("agentTypeID");
                if (id != null && id.propertyType == SerializedPropertyType.Integer && id.intValue == 0) return element.propertyPath;
            }
            throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SETTINGS_SCHEMA_UNSUPPORTED", "Unity default NavMesh agent settings are unavailable.");
        }

        private static NavMeshSettingsData Copy(NavMeshSettingsData value)
        {
            return new NavMeshSettingsData
            {
                Available = value.Available,
                AgentRadius = value.AgentRadius,
                AgentHeight = value.AgentHeight,
                AgentSlope = value.AgentSlope,
                AgentClimb = value.AgentClimb,
                MinRegionArea = value.MinRegionArea,
                VoxelSize = value.VoxelSize,
                ManualVoxelSize = value.ManualVoxelSize,
                SettingsPath = value.SettingsPath,
                SettingsHash = value.SettingsHash,
                SettingsSizeBytes = value.SettingsSizeBytes
            };
        }

        private static void ApplyRequested(string field, double? value, double minimum, double maximum, List<string> fields, Action<double> assign)
        {
            if (!value.HasValue) return;
            if (double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < minimum || value.Value > maximum)
                throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SETTINGS_VALUE_INVALID", field + " must be finite in [" + minimum.ToString("R", CultureInfo.InvariantCulture) + "," + maximum.ToString("R", CultureInfo.InvariantCulture) + "].", "Pass a value inside the documented range.");
            fields.Add(field);
            assign(value.Value);
        }

        private static bool FieldEquals(string field, NavMeshSettingsData left, NavMeshSettingsData right)
        {
            switch (field)
            {
                case "agentRadius": return Close(left.AgentRadius, right.AgentRadius);
                case "agentHeight": return Close(left.AgentHeight, right.AgentHeight);
                case "agentSlope": return Close(left.AgentSlope, right.AgentSlope);
                case "agentClimb": return Close(left.AgentClimb, right.AgentClimb);
                case "minRegionArea": return Close(left.MinRegionArea, right.MinRegionArea);
                case "voxelSize": return Close(left.VoxelSize, right.VoxelSize) && left.ManualVoxelSize == right.ManualVoxelSize;
                case "manualVoxelSize": return left.ManualVoxelSize == right.ManualVoxelSize;
                default: return false;
            }
        }

        private static bool Close(double left, double right) { return Math.Abs(left - right) <= 0.00001d; }
        private static double Float(SerializedObject value, string path) { var property = Required(value, path, SerializedPropertyType.Float); return property.floatValue; }
        private static bool Bool(SerializedObject value, string path)
        {
            var property = RequiredAny(value, path, SerializedPropertyType.Boolean, SerializedPropertyType.Integer);
            return property.propertyType == SerializedPropertyType.Boolean ? property.boolValue : property.intValue != 0;
        }
        private static void SetFloat(SerializedObject value, string path, double number) { Required(value, path, SerializedPropertyType.Float).floatValue = checked((float)number); }
        private static void SetBool(SerializedObject value, string path, bool flag)
        {
            var property = RequiredAny(value, path, SerializedPropertyType.Boolean, SerializedPropertyType.Integer);
            if (property.propertyType == SerializedPropertyType.Boolean) property.boolValue = flag; else property.intValue = flag ? 1 : 0;
        }
        private static SerializedProperty Required(SerializedObject value, string path, SerializedPropertyType type) { return RequiredAny(value, path, type); }
        /// <summary>Unity 2022/6000 对 serialized bool 的 type 投影不同，只接受已登记的两种明确形态。</summary>
        private static SerializedProperty RequiredAny(SerializedObject value, string path, params SerializedPropertyType[] types)
        {
            var property = value.FindProperty(path);
            if (property == null || !types.Contains(property.propertyType))
                throw new UnityCliBridge.BridgeCommandException("E_NAVMESH_SETTINGS_SCHEMA_UNSUPPORTED", "Unity NavMesh settings property is unavailable or has an unsupported type: " + path + ".", "Use a Unity version supported by this CLI build.");
            return property;
        }
        internal static string Sha256(byte[] value) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(value ?? Array.Empty<byte>())).Replace("-", string.Empty).ToLowerInvariant(); }
    }
}
#pragma warning restore 0618
#endif
