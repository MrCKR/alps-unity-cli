#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace UnityCli
{
    /// <summary>四类 ProjectSettings Tool 的批准字段、版本分派、精确持久化和 fresh readback owner。</summary>
    internal static class ProjectSettingsToolSupport
    {
        internal const string AudioAssetPath = "ProjectSettings/AudioManager.asset";
        internal const string InputAssetPath = "ProjectSettings/InputManager.asset";
        internal const string PlayerAssetPath = "ProjectSettings/ProjectSettings.asset";
        internal const string TimeAssetPath = "ProjectSettings/TimeManager.asset";

        private const int DefaultInputLimit = 50;
        private const int MaxInputLimit = 200;
        private const int MaxInputAxisScan = 4096;

        private sealed class VersionSurface
        {
            internal string Id;
            internal string AudioVolume;
            internal string AudioRolloff;
            internal string AudioDoppler;
            internal string InputAxes;
            internal string InputName;
            internal string InputSensitivity;
            internal string InputGravity;
            internal string InputDead;
            internal string TimeFixedDelta;
            internal string TimeMaximumDelta;
            internal string TimeScale;
        }

        /// <summary>读 Tool 在主线程读取指定配置资产，不获取 mutation lease。</summary>
        internal static ProjectAudioSettingsSnapshot ReadAudio(string toolName)
        {
            EnsureReady(toolName);
            var surface = ResolveSurface();
            using (var so = LoadSettingsAsset(AudioAssetPath))
            {
                return new ProjectAudioSettingsSnapshot
                {
                    UnityVersion = Application.unityVersion,
                    VersionSurface = surface.Id,
                    Asset = ReadFileIdentity(AudioAssetPath),
                    Volume = ReadFloat(so, surface.AudioVolume),
                    RolloffScale = ReadFloat(so, surface.AudioRolloff),
                    DopplerFactor = ReadFloat(so, surface.AudioDoppler)
                };
            }
        }

        /// <summary>只更新显式提供的三个 Audio 字段，并从新 SerializedObject 与磁盘文件验证终态。</summary>
        internal static ProjectAudioSettingsSetResult SetAudio(ProjectAudioSettingsSetRequest request, UnityCliProjectToolContext ctx)
        {
            const string toolName = "project-settings.audio-settings.set";
            EnsureReady(toolName);
            request = request ?? new ProjectAudioSettingsSetRequest();
            if (!request.Volume.HasValue && !request.RolloffScale.HasValue && !request.DopplerFactor.HasValue)
                throw NoFields(toolName);

            var expectedVolume = NormalizeOptional(request.Volume, "volume", 0f, 1f, false);
            var expectedRolloff = NormalizeOptional(request.RolloffScale, "rolloffScale", 0f, float.MaxValue, false);
            var expectedDoppler = NormalizeOptional(request.DopplerFactor, "dopplerFactor", 0f, float.MaxValue, false);
            var before = ReadAudio(toolName);
            var changedFields = ChangedFields(
                Change("volume", before.Volume, expectedVolume),
                Change("rolloffScale", before.RolloffScale, expectedRolloff),
                Change("dopplerFactor", before.DopplerFactor, expectedDoppler));
            var effects = Effects(toolName, AudioAssetPath, changedFields, false);
            if (changedFields.Count == 0 || ctx.DryRun)
                return new ProjectAudioSettingsSetResult { Applied = false, WouldChange = changedFields.Count > 0, DryRun = ctx.DryRun, ChangedFields = changedFields, Before = before, After = before, PlannedEffects = effects };

            var surface = ResolveSurface();
            using (var so = LoadSettingsAsset(AudioAssetPath))
            {
                var target = so.targetObject;
                var volume = RequireFloat(so, surface.AudioVolume);
                var rolloff = RequireFloat(so, surface.AudioRolloff);
                var doppler = RequireFloat(so, surface.AudioDoppler);
                ctx.CommitEffect();
                SetFloat(volume, expectedVolume);
                SetFloat(rolloff, expectedRolloff);
                SetFloat(doppler, expectedDoppler);
                ApplyAndSave(so, target, AudioAssetPath);
            }

            var after = ReadAudio(toolName);
            VerifyFloat("volume", after.Volume, expectedVolume);
            VerifyFloat("rolloffScale", after.RolloffScale, expectedRolloff);
            VerifyFloat("dopplerFactor", after.DopplerFactor, expectedDoppler);
            VerifyFileChanged(before.Asset, after.Asset, toolName);
            return new ProjectAudioSettingsSetResult { Applied = true, WouldChange = true, DryRun = false, ChangedFields = changedFields, Before = before, After = after, PlannedEffects = effects };
        }

        /// <summary>分页返回 legacy Input Manager 的批准字段；axis 为精确名称过滤，不做模糊匹配。</summary>
        internal static ProjectInputSettingsResult ReadInput(ProjectInputSettingsRequest request, string toolName)
        {
            EnsureReady(toolName);
            request = request ?? new ProjectInputSettingsRequest();
            var offset = request.Offset ?? 0L;
            var limit = request.Limit ?? DefaultInputLimit;
            if (offset < 0 || offset > int.MaxValue)
                throw Invalid("offset", "offset must be between 0 and 2147483647.");
            if (limit < 1 || limit > MaxInputLimit)
                throw Invalid("limit", "limit must be between 1 and " + MaxInputLimit + ".");

            var surface = ResolveSurface();
            var page = new List<ProjectInputAxisSnapshot>((int)limit);
            var totalCount = 0;
            var matchedCount = 0;
            using (var so = LoadSettingsAsset(InputAssetPath))
            {
                var axes = RequireArray(so, surface.InputAxes);
                EnsureAxisCountBounded(axes);
                totalCount = axes.arraySize;
                for (var index = 0; index < axes.arraySize; index++)
                {
                    var axis = axes.GetArrayElementAtIndex(index);
                    if (!string.IsNullOrEmpty(request.Axis) && !string.Equals(ReadString(axis, surface.InputName), request.Axis, StringComparison.Ordinal))
                        continue;
                    if (matchedCount >= offset && page.Count < limit)
                        page.Add(ReadAxis(axis, index, surface));
                    matchedCount++;
                }
            }

            return new ProjectInputSettingsResult
            {
                UnityVersion = Application.unityVersion,
                VersionSurface = surface.Id,
                Asset = ReadFileIdentity(InputAssetPath),
                TotalAxisCount = totalCount,
                MatchedAxisCount = matchedCount,
                ReturnedCount = page.Count,
                HasMore = offset + page.Count < matchedCount,
                Axes = page
            };
        }

        /// <summary>冻结唯一命名axis和三个批准字段，effect后按index/name/value与文件身份重新证明。</summary>
        internal static ProjectInputSettingsSetResult SetInput(ProjectInputSettingsSetRequest request, UnityCliProjectToolContext ctx)
        {
            const string toolName = "project-settings.input-settings.set";
            EnsureReady(toolName);
            request = request ?? new ProjectInputSettingsSetRequest();
            if (string.IsNullOrEmpty(request.Axis))
                throw Invalid("axis", "axis is required and must exactly match one legacy Input Manager axis name.");
            if (!request.Sensitivity.HasValue && !request.Gravity.HasValue && !request.Dead.HasValue)
                throw NoFields(toolName);

            var expectedSensitivity = NormalizeOptional(request.Sensitivity, "sensitivity", 0f, float.MaxValue, false);
            var expectedGravity = NormalizeOptional(request.Gravity, "gravity", 0f, float.MaxValue, false);
            var expectedDead = NormalizeOptional(request.Dead, "dead", 0f, 1f, false);
            var surface = ResolveSurface();
            ProjectInputAxisSnapshot beforeAxis;
            UnityEngine.Object target;
            SerializedObject frozen;
            SerializedProperty sensitivity;
            SerializedProperty gravity;
            SerializedProperty dead;
            using (var inspect = LoadSettingsAsset(InputAssetPath))
            {
                var axes = RequireArray(inspect, surface.InputAxes);
                var matches = FindAxes(axes, request.Axis, surface);
                if (matches.Count == 0) throw new UnityCliBridge.BridgeCommandException("E_PROJECT_INPUT_AXIS_NOT_FOUND", "No legacy Input Manager axis exactly matches '" + request.Axis + "'.", "Run project-settings.input-settings with an axis filter or inspect a bounded page before retrying.");
                // 官方name selector固定选择配置顺序中的首项；index/name在barrier前共同冻结。
                beforeAxis = ReadAxis(axes.GetArrayElementAtIndex(matches[0]), matches[0], surface);
            }

            var beforeAsset = ReadFileIdentity(InputAssetPath);
            var changedFields = ChangedFields(
                Change("sensitivity", beforeAxis.Sensitivity, expectedSensitivity),
                Change("gravity", beforeAxis.Gravity, expectedGravity),
                Change("dead", beforeAxis.Dead, expectedDead));
            var effects = Effects(toolName, InputAssetPath, changedFields, false);
            if (changedFields.Count == 0 || ctx.DryRun)
                return new ProjectInputSettingsSetResult { Applied = false, WouldChange = changedFields.Count > 0, DryRun = ctx.DryRun, ChangedFields = changedFields, Before = beforeAxis, After = beforeAxis, AssetBefore = beforeAsset, AssetAfter = beforeAsset, PlannedEffects = effects };

            frozen = LoadSettingsAsset(InputAssetPath);
            try
            {
                var axes = RequireArray(frozen, surface.InputAxes);
                if (beforeAxis.Index < 0 || beforeAxis.Index >= axes.arraySize)
                    throw TargetChanged(toolName);
                var axis = axes.GetArrayElementAtIndex((int)beforeAxis.Index);
                if (!string.Equals(ReadString(axis, surface.InputName), request.Axis, StringComparison.Ordinal))
                    throw TargetChanged(toolName);
                target = frozen.targetObject;
                sensitivity = RequireFloat(axis, surface.InputSensitivity);
                gravity = RequireFloat(axis, surface.InputGravity);
                dead = RequireFloat(axis, surface.InputDead);
                ctx.CommitEffect();
                SetFloat(sensitivity, expectedSensitivity);
                SetFloat(gravity, expectedGravity);
                SetFloat(dead, expectedDead);
                ApplyAndSave(frozen, target, InputAssetPath);
            }
            finally
            {
                frozen.Dispose();
            }

            ProjectInputAxisSnapshot afterAxis;
            using (var readback = LoadSettingsAsset(InputAssetPath))
            {
                var axes = RequireArray(readback, surface.InputAxes);
                if (beforeAxis.Index < 0 || beforeAxis.Index >= axes.arraySize)
                    throw VerifyFailed(toolName, "The selected Input Manager axis index is no longer present.");
                afterAxis = ReadAxis(axes.GetArrayElementAtIndex((int)beforeAxis.Index), (int)beforeAxis.Index, surface);
            }
            if (!string.Equals(afterAxis.Name, request.Axis, StringComparison.Ordinal))
                throw VerifyFailed(toolName, "The selected Input Manager axis identity changed before terminal readback.");
            VerifyFloat("sensitivity", afterAxis.Sensitivity, expectedSensitivity);
            VerifyFloat("gravity", afterAxis.Gravity, expectedGravity);
            VerifyFloat("dead", afterAxis.Dead, expectedDead);
            var afterAsset = ReadFileIdentity(InputAssetPath);
            VerifyFileChanged(beforeAsset, afterAsset, toolName);
            return new ProjectInputSettingsSetResult { Applied = true, WouldChange = true, DryRun = false, ChangedFields = changedFields, Before = beforeAxis, After = afterAxis, AssetBefore = beforeAsset, AssetAfter = afterAsset, PlannedEffects = effects };
        }

        /// <summary>读取active build target对应的PlayerSettings标量与enum字段。</summary>
        internal static ProjectPlayerSettingsSnapshot ReadPlayer(string toolName)
        {
            EnsureReady(toolName);
            var surface = ResolveSurface();
            var target = ActiveNamedBuildTarget();
            try
            {
                return new ProjectPlayerSettingsSnapshot
                {
                    UnityVersion = Application.unityVersion,
                    VersionSurface = surface.Id,
                    Asset = ReadFileIdentity(PlayerAssetPath),
                    BuildTarget = target.TargetName,
                    CompanyName = PlayerSettings.companyName,
                    ProductName = PlayerSettings.productName,
                    BundleVersion = PlayerSettings.bundleVersion,
                    ScriptingBackend = PlayerSettings.GetScriptingBackend(target).ToString(),
                    ApiCompatibilityLevel = PlayerSettings.GetApiCompatibilityLevel(target).ToString()
                };
            }
            catch (UnityCliBridge.BridgeCommandException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw VersionUnavailable("PlayerSettings for active build target '" + target.TargetName + "' are unavailable: " + ex.Message);
            }
        }

        /// <summary>PlayerSettings enum按当前Unity定义精确解析；reload风险显式投影但不在断线后重放。</summary>
        internal static ProjectPlayerSettingsSetResult SetPlayer(ProjectPlayerSettingsSetRequest request, UnityCliProjectToolContext ctx)
        {
            const string toolName = "project-settings.player-settings.set";
            EnsureReady(toolName);
            request = request ?? new ProjectPlayerSettingsSetRequest();
            if (request.CompanyName == null && request.ProductName == null && request.BundleVersion == null && request.ScriptingBackend == null && request.ApiCompatibilityLevel == null)
                throw NoFields(toolName);

            var before = ReadPlayer(toolName);
            var target = ActiveNamedBuildTarget();
            var backend = ParseEnum<ScriptingImplementation>(request.ScriptingBackend, "scriptingBackend");
            var api = ParseEnum<ApiCompatibilityLevel>(request.ApiCompatibilityLevel, "apiCompatibilityLevel");
            var changedFields = ChangedFields(
                Change("companyName", before.CompanyName, request.CompanyName),
                Change("productName", before.ProductName, request.ProductName),
                Change("bundleVersion", before.BundleVersion, request.BundleVersion),
                Change("scriptingBackend", before.ScriptingBackend, backend.HasValue ? backend.Value.ToString() : null),
                Change("apiCompatibilityLevel", before.ApiCompatibilityLevel, api.HasValue ? api.Value.ToString() : null));
            var requiresReload = changedFields.Contains("scriptingBackend") || changedFields.Contains("apiCompatibilityLevel");
            var effects = Effects(toolName, PlayerAssetPath, changedFields, requiresReload);
            if (changedFields.Count == 0 || ctx.DryRun)
                return new ProjectPlayerSettingsSetResult { Applied = false, WouldChange = changedFields.Count > 0, DryRun = ctx.DryRun, RequiresDomainReload = requiresReload, ChangedFields = changedFields, Before = before, After = before, PlannedEffects = effects };

            var playerObject = LoadSettingsObject(PlayerAssetPath);
            ctx.CommitEffect();
            if (request.CompanyName != null) PlayerSettings.companyName = request.CompanyName;
            if (request.ProductName != null) PlayerSettings.productName = request.ProductName;
            if (request.BundleVersion != null) PlayerSettings.bundleVersion = request.BundleVersion;
            if (backend.HasValue) PlayerSettings.SetScriptingBackend(target, backend.Value);
            if (api.HasValue) PlayerSettings.SetApiCompatibilityLevel(target, api.Value);
            SaveExact(playerObject, PlayerAssetPath);

            var after = ReadPlayer(toolName);
            VerifyString("companyName", after.CompanyName, request.CompanyName);
            VerifyString("productName", after.ProductName, request.ProductName);
            VerifyString("bundleVersion", after.BundleVersion, request.BundleVersion);
            VerifyString("scriptingBackend", after.ScriptingBackend, backend.HasValue ? backend.Value.ToString() : null);
            VerifyString("apiCompatibilityLevel", after.ApiCompatibilityLevel, api.HasValue ? api.Value.ToString() : null);
            VerifyFileChanged(before.Asset, after.Asset, toolName);
            return new ProjectPlayerSettingsSetResult { Applied = true, WouldChange = true, DryRun = false, RequiresDomainReload = requiresReload, ChangedFields = changedFields, Before = before, After = after, PlannedEffects = effects };
        }

        /// <summary>读取TimeManager的持久化字段，不以PlayMode运行时值冒充项目配置。</summary>
        internal static ProjectTimeSettingsSnapshot ReadTime(string toolName)
        {
            EnsureReady(toolName);
            var surface = ResolveSurface();
            using (var so = LoadSettingsAsset(TimeAssetPath))
            {
                return new ProjectTimeSettingsSnapshot
                {
                    UnityVersion = Application.unityVersion,
                    VersionSurface = surface.Id,
                    Asset = ReadFileIdentity(TimeAssetPath),
                    FixedDeltaTime = ReadFloat(so, surface.TimeFixedDelta),
                    MaximumDeltaTime = ReadFloat(so, surface.TimeMaximumDelta),
                    TimeScale = ReadFloat(so, surface.TimeScale)
                };
            }
        }

        /// <summary>Time写入先验证effective fixed/maximum关系，再保存唯一TimeManager asset并fresh证明。</summary>
        internal static ProjectTimeSettingsSetResult SetTime(ProjectTimeSettingsSetRequest request, UnityCliProjectToolContext ctx)
        {
            const string toolName = "project-settings.time-settings.set";
            EnsureReady(toolName);
            request = request ?? new ProjectTimeSettingsSetRequest();
            if (!request.FixedDeltaTime.HasValue && !request.MaximumDeltaTime.HasValue && !request.TimeScale.HasValue)
                throw NoFields(toolName);

            var expectedFixed = NormalizeOptional(request.FixedDeltaTime, "fixedDeltaTime", 0f, float.MaxValue, true);
            var expectedMaximum = NormalizeOptional(request.MaximumDeltaTime, "maximumDeltaTime", 0f, float.MaxValue, true);
            var expectedScale = NormalizeOptional(request.TimeScale, "timeScale", 0f, 100f, false);
            var before = ReadTime(toolName);
            var effectiveFixed = expectedFixed ?? (float)before.FixedDeltaTime;
            var effectiveMaximum = expectedMaximum ?? (float)before.MaximumDeltaTime;
            if (effectiveMaximum < effectiveFixed)
                throw Invalid("maximumDeltaTime", "maximumDeltaTime must be greater than or equal to the effective fixedDeltaTime.");

            var changedFields = ChangedFields(
                Change("fixedDeltaTime", before.FixedDeltaTime, expectedFixed),
                Change("maximumDeltaTime", before.MaximumDeltaTime, expectedMaximum),
                Change("timeScale", before.TimeScale, expectedScale));
            var effects = Effects(toolName, TimeAssetPath, changedFields, false);
            if (changedFields.Count == 0 || ctx.DryRun)
                return new ProjectTimeSettingsSetResult { Applied = false, WouldChange = changedFields.Count > 0, DryRun = ctx.DryRun, ChangedFields = changedFields, Before = before, After = before, PlannedEffects = effects };

            var surface = ResolveSurface();
            using (var so = LoadSettingsAsset(TimeAssetPath))
            {
                var target = so.targetObject;
                var fixedDelta = RequireFloat(so, surface.TimeFixedDelta);
                var maximumDelta = RequireFloat(so, surface.TimeMaximumDelta);
                var scale = RequireFloat(so, surface.TimeScale);
                ctx.CommitEffect();
                SetFloat(fixedDelta, expectedFixed);
                SetFloat(maximumDelta, expectedMaximum);
                SetFloat(scale, expectedScale);
                ApplyAndSave(so, target, TimeAssetPath);
            }

            var after = ReadTime(toolName);
            VerifyFloat("fixedDeltaTime", after.FixedDeltaTime, expectedFixed);
            VerifyFloat("maximumDeltaTime", after.MaximumDeltaTime, expectedMaximum);
            VerifyFloat("timeScale", after.TimeScale, expectedScale);
            VerifyFileChanged(before.Asset, after.Asset, toolName);
            return new ProjectTimeSettingsSetResult { Applied = true, WouldChange = true, DryRun = false, ChangedFields = changedFields, Before = before, After = after, PlannedEffects = effects };
        }

        private static void EnsureReady(string toolName)
        {
            EditorToolSupport.EnsureEditorNotBusy(toolName);
            ResolveSurface();
        }

        /// <summary>未来Unity版本不会自动继承已验证的serialized property surface。</summary>
        private static VersionSurface ResolveSurface()
        {
            var parts = (Application.unityVersion ?? string.Empty).Split('.');
            var major = 0;
            var minor = 0;
            var parsed = parts.Length >= 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minor);
            var supported = parsed && ((major == 2022 && minor == 3) || major == 2023 || (major == 6000 && minor == 0));
            if (!supported)
                throw VersionUnavailable("Unity " + Application.unityVersion + " has no verified ProjectSettings field surface.");
            return new VersionSurface
            {
                Id = major == 6000 ? "unity-6000.0" : "unity-2022.3-2023.x",
                AudioVolume = "m_Volume", AudioRolloff = "Rolloff Scale", AudioDoppler = "Doppler Factor",
                InputAxes = "m_Axes", InputName = "m_Name", InputSensitivity = "sensitivity", InputGravity = "gravity", InputDead = "dead",
                TimeFixedDelta = "Fixed Timestep", TimeMaximumDelta = "Maximum Allowed Timestep", TimeScale = "m_TimeScale"
            };
        }

        private static SerializedObject LoadSettingsAsset(string path)
        {
            var target = LoadSettingsObject(path);
            var so = new SerializedObject(target);
            so.UpdateIfRequiredOrScript();
            return so;
        }

        private static UnityEngine.Object LoadSettingsObject(string path)
        {
            var objects = AssetDatabase.LoadAllAssetsAtPath(path);
            if (objects == null || objects.Length == 0 || objects[0] == null)
                throw new UnityCliBridge.BridgeCommandException("E_PROJECT_SETTINGS_UNAVAILABLE", "Unity could not load the required settings asset: " + path, "Verify the project and Unity version before retrying.");
            return objects[0];
        }

        private static ProjectSettingsFileIdentity ReadFileIdentity(string path)
        {
            var fullPath = Path.Combine(Directory.GetParent(Application.dataPath).FullName, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
                throw new UnityCliBridge.BridgeCommandException("E_PROJECT_SETTINGS_UNAVAILABLE", "The required settings file does not exist: " + path, "Verify the project and Unity version before retrying.");
            using (var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                return new ProjectSettingsFileIdentity { Path = path, Sha256 = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant(), SizeBytes = stream.Length };
            }
        }

        private static void ApplyAndSave(SerializedObject so, UnityEngine.Object target, string path)
        {
            if (!so.ApplyModifiedPropertiesWithoutUndo())
                throw VerifyFailed(path, "Unity did not apply the frozen serialized ProjectSettings changes.");
            SaveExact(target, path);
        }

        private static void SaveExact(UnityEngine.Object target, string path)
        {
            if (target == null)
                throw VerifyFailed(path, "The frozen ProjectSettings asset is no longer available.");
            EditorUtility.SetDirty(target);
            // ProjectSettings singleton不会响应SaveAssetIfDirty；Unity的ProjectSettings持久化入口是SaveAssets。
            AssetDatabase.SaveAssets();
        }

        private static NamedBuildTarget ActiveNamedBuildTarget()
        {
            var group = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (group == BuildTargetGroup.Unknown)
                throw VersionUnavailable("The active build target group is unknown.");
            return NamedBuildTarget.FromBuildTargetGroup(group);
        }

        private static T? ParseEnum<T>(string value, string field) where T : struct
        {
            if (value == null) return null;
            T parsed;
            if (!Enum.TryParse(value, false, out parsed) || !Enum.IsDefined(typeof(T), parsed))
                throw Invalid(field, field + " must exactly match one value supported by Unity " + Application.unityVersion + ": " + string.Join(", ", Enum.GetNames(typeof(T))) + ".");
            return parsed;
        }

        private static float? NormalizeOptional(double? value, string field, float minimum, float maximum, bool exclusiveMinimum)
        {
            if (!value.HasValue) return null;
            if (double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < -float.MaxValue || value.Value > float.MaxValue)
                throw Invalid(field, field + " must be a finite Unity float.");
            var normalized = (float)value.Value;
            if ((exclusiveMinimum && normalized <= minimum) || (!exclusiveMinimum && normalized < minimum) || normalized > maximum)
                throw Invalid(field, field + " is outside the supported range " + (exclusiveMinimum ? "(" : "[") + minimum.ToString(CultureInfo.InvariantCulture) + ", " + maximum.ToString(CultureInfo.InvariantCulture) + "].");
            return normalized;
        }

        private static SerializedProperty RequireArray(SerializedObject so, string path)
        {
            var property = so.FindProperty(path);
            if (property == null || !property.isArray)
                throw VersionUnavailable("Required serialized array '" + path + "' is unavailable in Unity " + Application.unityVersion + ".");
            return property;
        }

        private static SerializedProperty RequireFloat(SerializedObject so, string path)
        {
            var property = so.FindProperty(path);
            if (property == null || property.propertyType != SerializedPropertyType.Float)
                throw VersionUnavailable("Required serialized float '" + path + "' is unavailable in Unity " + Application.unityVersion + ".");
            return property;
        }

        private static SerializedProperty RequireFloat(SerializedProperty parent, string path)
        {
            var property = parent.FindPropertyRelative(path);
            if (property == null || property.propertyType != SerializedPropertyType.Float)
                throw VersionUnavailable("Required serialized float '" + path + "' is unavailable in Unity " + Application.unityVersion + ".");
            return property;
        }

        private static double ReadFloat(SerializedObject so, string path) { return RequireFloat(so, path).floatValue; }
        private static double ReadFloat(SerializedProperty parent, string path) { return RequireFloat(parent, path).floatValue; }

        private static string ReadString(SerializedProperty parent, string path)
        {
            var property = parent.FindPropertyRelative(path);
            if (property == null || property.propertyType != SerializedPropertyType.String)
                throw VersionUnavailable("Required serialized string '" + path + "' is unavailable in Unity " + Application.unityVersion + ".");
            return property.stringValue;
        }

        private static ProjectInputAxisSnapshot ReadAxis(SerializedProperty axis, int index, VersionSurface surface)
        {
            return new ProjectInputAxisSnapshot
            {
                Index = index,
                Name = ReadString(axis, surface.InputName),
                Sensitivity = ReadFloat(axis, surface.InputSensitivity),
                Gravity = ReadFloat(axis, surface.InputGravity),
                Dead = ReadFloat(axis, surface.InputDead)
            };
        }

        private static List<int> FindAxes(SerializedProperty axes, string name, VersionSurface surface)
        {
            EnsureAxisCountBounded(axes);
            var result = new List<int>();
            for (var index = 0; index < axes.arraySize; index++)
                if (string.Equals(ReadString(axes.GetArrayElementAtIndex(index), surface.InputName), name, StringComparison.Ordinal)) result.Add(index);
            return result;
        }

        private static void EnsureAxisCountBounded(SerializedProperty axes)
        {
            if (axes == null || axes.arraySize > MaxInputAxisScan)
                throw new UnityCliBridge.BridgeCommandException("E_PROJECT_SETTINGS_LIMIT_EXCEEDED", "InputManager contains more than " + MaxInputAxisScan + " axis entries and cannot be scanned safely.", "Reduce the legacy Input Manager axis count before retrying.");
        }

        private static void SetFloat(SerializedProperty property, float? value) { if (value.HasValue) property.floatValue = value.Value; }

        private static string Change(string field, double before, float? expected)
        {
            return expected.HasValue && (float)before != expected.Value ? field : null;
        }

        private static string Change(string field, string before, string expected)
        {
            return expected != null && !string.Equals(before, expected, StringComparison.Ordinal) ? field : null;
        }

        private static List<string> ChangedFields(params string[] fields) { return fields.Where(field => field != null).ToList(); }

        private static List<ProjectSettingsPlannedEffect> Effects(string toolName, string path, List<string> fields, bool reload)
        {
            if (fields == null || fields.Count == 0) return new List<ProjectSettingsPlannedEffect>();
            return new List<ProjectSettingsPlannedEffect>
            {
                new ProjectSettingsPlannedEffect { Type = toolName, AssetPath = path, Fields = new List<string>(fields), RequiresDomainReload = reload }
            };
        }

        private static void VerifyFloat(string field, double actual, float? expected)
        {
            if (expected.HasValue && (float)actual != expected.Value)
                throw VerifyFailed(field, "Fresh ProjectSettings readback does not equal the requested Unity float.");
        }

        private static void VerifyString(string field, string actual, string expected)
        {
            if (expected != null && !string.Equals(actual, expected, StringComparison.Ordinal))
                throw VerifyFailed(field, "Fresh ProjectSettings readback does not equal the requested value.");
        }

        private static void VerifyFileChanged(ProjectSettingsFileIdentity before, ProjectSettingsFileIdentity after, string toolName)
        {
            if (before == null || after == null || string.IsNullOrEmpty(after.Sha256) || string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal))
                throw VerifyFailed(toolName, "The exact ProjectSettings file did not retain a distinct persisted payload.");
        }

        private static UnityCliBridge.BridgeCommandException NoFields(string toolName)
        {
            return new UnityCliBridge.BridgeCommandException("E_PROJECT_SETTINGS_EMPTY", toolName + " requires at least one approved setting field.", "Pass one or more fields shown by tools describe " + toolName + ".");
        }

        private static UnityCliBridge.BridgeCommandException Invalid(string field, string message)
        {
            return new UnityCliBridge.BridgeCommandException("E_PROJECT_SETTINGS_INVALID", "Invalid ProjectSettings field '" + field + "': " + message, "Correct the field using tools describe and retry.");
        }

        private static UnityCliBridge.BridgeCommandException VersionUnavailable(string message)
        {
            return new UnityCliBridge.BridgeCommandException("E_PROJECT_SETTINGS_VERSION_UNAVAILABLE", message, "Use a Unity version and field surface explicitly supported by this Tool descriptor.");
        }

        private static UnityCliBridge.BridgeCommandException VerifyFailed(string toolName, string message)
        {
            return new UnityCliBridge.BridgeCommandException("E_PROJECT_SETTINGS_VERIFY_FAILED", message, "Read the corresponding project.*-settings Tool before deciding whether to retry " + toolName + ".");
        }

        private static UnityCliBridge.BridgeCommandException TargetChanged(string toolName)
        {
            return new UnityCliBridge.BridgeCommandException("E_PROJECT_SETTINGS_TARGET_CHANGED", "The frozen ProjectSettings target changed before the effect barrier.", "Read the corresponding settings Tool and submit a new " + toolName + " request.");
        }
    }

    public sealed class ProjectSettingsFileIdentity
    {
        public string Path { get; set; }
        public string Sha256 { get; set; }
        public long SizeBytes { get; set; }
    }

    public sealed class ProjectSettingsPlannedEffect
    {
        public string Type { get; set; }
        public string AssetPath { get; set; }
        public List<string> Fields { get; set; }
        public bool RequiresDomainReload { get; set; }
    }

    public sealed class ProjectSettingsReadRequest { }

    public sealed class ProjectAudioSettingsSnapshot
    {
        public string UnityVersion { get; set; }
        public string VersionSurface { get; set; }
        public ProjectSettingsFileIdentity Asset { get; set; }
        public double Volume { get; set; }
        public double RolloffScale { get; set; }
        public double DopplerFactor { get; set; }
    }

    public sealed class ProjectAudioSettingsSetRequest
    {
        public double? Volume { get; set; }
        public double? RolloffScale { get; set; }
        public double? DopplerFactor { get; set; }
    }

    public sealed class ProjectAudioSettingsSetResult
    {
        public bool Applied { get; set; }
        public bool WouldChange { get; set; }
        public bool DryRun { get; set; }
        public List<string> ChangedFields { get; set; }
        public ProjectAudioSettingsSnapshot Before { get; set; }
        public ProjectAudioSettingsSnapshot After { get; set; }
        public List<ProjectSettingsPlannedEffect> PlannedEffects { get; set; }
    }

    public sealed class ProjectInputSettingsRequest
    {
        public string Axis { get; set; }
        public long? Offset { get; set; }
        public long? Limit { get; set; }
    }

    public sealed class ProjectInputAxisSnapshot
    {
        public long Index { get; set; }
        public string Name { get; set; }
        public double Sensitivity { get; set; }
        public double Gravity { get; set; }
        public double Dead { get; set; }
    }

    public sealed class ProjectInputSettingsResult
    {
        public string UnityVersion { get; set; }
        public string VersionSurface { get; set; }
        public ProjectSettingsFileIdentity Asset { get; set; }
        public long TotalAxisCount { get; set; }
        public long MatchedAxisCount { get; set; }
        public long ReturnedCount { get; set; }
        public bool HasMore { get; set; }
        public List<ProjectInputAxisSnapshot> Axes { get; set; }
    }

    public sealed class ProjectInputSettingsSetRequest
    {
        [UnityCliRequired]
        public string Axis { get; set; }
        public double? Sensitivity { get; set; }
        public double? Gravity { get; set; }
        public double? Dead { get; set; }
    }

    public sealed class ProjectInputSettingsSetResult
    {
        public bool Applied { get; set; }
        public bool WouldChange { get; set; }
        public bool DryRun { get; set; }
        public List<string> ChangedFields { get; set; }
        public ProjectInputAxisSnapshot Before { get; set; }
        public ProjectInputAxisSnapshot After { get; set; }
        public ProjectSettingsFileIdentity AssetBefore { get; set; }
        public ProjectSettingsFileIdentity AssetAfter { get; set; }
        public List<ProjectSettingsPlannedEffect> PlannedEffects { get; set; }
    }

    public sealed class ProjectPlayerSettingsSnapshot
    {
        public string UnityVersion { get; set; }
        public string VersionSurface { get; set; }
        public ProjectSettingsFileIdentity Asset { get; set; }
        public string BuildTarget { get; set; }
        public string CompanyName { get; set; }
        public string ProductName { get; set; }
        public string BundleVersion { get; set; }
        public string ScriptingBackend { get; set; }
        public string ApiCompatibilityLevel { get; set; }
    }

    public sealed class ProjectPlayerSettingsSetRequest
    {
        public string CompanyName { get; set; }
        public string ProductName { get; set; }
        public string BundleVersion { get; set; }
        public string ScriptingBackend { get; set; }
        public string ApiCompatibilityLevel { get; set; }
    }

    public sealed class ProjectPlayerSettingsSetResult
    {
        public bool Applied { get; set; }
        public bool WouldChange { get; set; }
        public bool DryRun { get; set; }
        public bool RequiresDomainReload { get; set; }
        public List<string> ChangedFields { get; set; }
        public ProjectPlayerSettingsSnapshot Before { get; set; }
        public ProjectPlayerSettingsSnapshot After { get; set; }
        public List<ProjectSettingsPlannedEffect> PlannedEffects { get; set; }
    }

    public sealed class ProjectTimeSettingsSnapshot
    {
        public string UnityVersion { get; set; }
        public string VersionSurface { get; set; }
        public ProjectSettingsFileIdentity Asset { get; set; }
        public double FixedDeltaTime { get; set; }
        public double MaximumDeltaTime { get; set; }
        public double TimeScale { get; set; }
    }

    public sealed class ProjectTimeSettingsSetRequest
    {
        public double? FixedDeltaTime { get; set; }
        public double? MaximumDeltaTime { get; set; }
        public double? TimeScale { get; set; }
    }

    public sealed class ProjectTimeSettingsSetResult
    {
        public bool Applied { get; set; }
        public bool WouldChange { get; set; }
        public bool DryRun { get; set; }
        public List<string> ChangedFields { get; set; }
        public ProjectTimeSettingsSnapshot Before { get; set; }
        public ProjectTimeSettingsSnapshot After { get; set; }
        public List<ProjectSettingsPlannedEffect> PlannedEffects { get; set; }
    }
}
#endif
