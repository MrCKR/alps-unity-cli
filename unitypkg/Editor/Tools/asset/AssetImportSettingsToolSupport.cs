#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>asset.import-settings 两个 Tool 的 importer 解析、预检、写入与 fresh proof owner。</summary>
    internal static class AssetImportSettingsToolSupport
    {
        internal static readonly string[] Platforms = { "Default", "Standalone", "iOS", "Android", "WebGL", "tvOS" };
        private static readonly HashSet<string> AudioSampleKeys = new HashSet<string>(new[]
        {
            "loadType", "compressionFormat", "quality", "sampleRateSetting", "sampleRateOverride"
        }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ReloadRiskImporterTypes = new HashSet<string>(new[]
        {
            "MonoImporter", "PluginImporter", "AssemblyDefinitionImporter", "AssemblyDefinitionReferenceImporter"
        }, StringComparer.Ordinal);

        internal sealed class MutationPlan
        {
            public string Path;
            public string Guid;
            public string Platform;
            public string ImporterType;
            public AssetImporter Importer;
            public readonly List<MemberWrite> DefaultWrites = new List<MemberWrite>();
            public readonly List<MemberWrite> StructuredWrites = new List<MemberWrite>();
            public object StructuredValue;
            public bool ApplyAudioDefault;
            public bool ApplyTexturePlatform;
            public bool ApplyAudioPlatform;
            public bool ClearAudioPlatform;
            public readonly List<AssetImportSettingChange> Changes = new List<AssetImportSettingChange>();
            public bool Changed { get { return Changes.Count > 0; } }
        }

        internal sealed class MemberWrite
        {
            public string Key;
            public PropertyInfo Property;
            public FieldInfo Field;
            public object Before;
            public object Expected;

            public object Read(object target)
            {
                return Property != null ? Property.GetValue(target, null) : Field.GetValue(target);
            }

            public void Apply(object target)
            {
                if (Property != null) Property.SetValue(target, Expected, null);
                else Field.SetValue(target, Expected);
            }
        }

        /// <summary>读取 importer 快照；只访问 AssetDatabase 当前状态，不触发 import、refresh 或写入。</summary>
        internal static AssetImportSettingsSnapshot Read(string rawPath, string rawPlatform)
        {
            var path = AssetToolSupport.RequireExistingAssetPath(rawPath);
            var platform = CanonicalPlatform(rawPlatform);
            var importer = RequireImporter(path);
            var snapshot = new AssetImportSettingsSnapshot
            {
                Guid = AssetDatabase.AssetPathToGUID(path),
                ImporterType = importer.GetType().Name,
                ImporterCategory = Category(importer),
                UnityVersion = Application.unityVersion,
                Settings = ReadDefaultSettings(importer),
                PlatformOverride = ReadPlatformSettings(importer, platform)
            };
            if (string.IsNullOrEmpty(snapshot.Guid))
                throw Error("E_ASSET_GUID_MISSING", "The asset has no stable GUID: " + path, path, platform);
            return snapshot;
        }

        /// <summary>在 effect 前解析全部 key、类型和值；任何未知或冲突都会阻止整次写入。</summary>
        internal static MutationPlan Prepare(string rawPath, string rawPlatform, Dictionary<string, object> settings)
        {
            var path = AssetToolSupport.RequireExistingAssetPath(rawPath);
            var platform = CanonicalPlatform(rawPlatform);
            if (settings == null || settings.Count == 0)
                throw Error("E_IMPORT_SETTINGS_REQUIRED", "settings must be a non-empty JSON object.", path, platform);
            if (settings.Keys.Any(string.IsNullOrWhiteSpace))
                throw Error("E_IMPORT_SETTINGS_INVALID", "settings contains an empty member name.", path, platform);
            var duplicateKey = settings.Keys.GroupBy(key => key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
            if (duplicateKey != null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_IMPORT_SETTINGS_CONFLICT",
                    "settings contains member names that differ only by case and resolve to the same importer member.",
                    new Dictionary<string, object> { { "path", path }, { "platform", platform }, { "keys", duplicateKey.ToArray() } },
                    new[] { "Keep exactly one casing for each importer setting name." });
            }

            var importer = RequireImporter(path);
            if (ReloadRiskImporterTypes.Contains(importer.GetType().Name))
                throw Error("E_IMPORT_SETTINGS_RELOAD_RISK", "This importer can trigger script or plugin reload and is not supported by the direct terminal Tool: " + importer.GetType().Name + ".", path, platform);
            var plan = new MutationPlan
            {
                Path = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                Platform = platform,
                ImporterType = importer.GetType().AssemblyQualifiedName,
                Importer = importer
            };
            var issues = new List<AssetImportSettingIssue>();
            if (string.Equals(platform, "Default", StringComparison.Ordinal)) PrepareDefault(plan, settings, issues);
            else PreparePlatform(plan, settings, issues);
            if (issues.Count > 0)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_IMPORT_SETTINGS_INVALID",
                    "One or more import settings could not be applied; no importer state was changed.",
                    new Dictionary<string, object>
                    {
                        { "path", path }, { "platform", platform }, { "importerType", importer.GetType().Name }, { "issues", issues }
                    },
                    new[] { "Run tools run asset.import-settings for the same path and platform, then retry with supported setting names and values." });
            }
            return plan;
        }

        /// <summary>effect 后应用冻结计划并同步 reimport；调用方负责先调用 CommitEffect。</summary>
        internal static void Apply(MutationPlan plan)
        {
            foreach (var write in plan.DefaultWrites) write.Apply(plan.Importer);
            if (plan.ApplyAudioDefault)
                ((AudioImporter)plan.Importer).defaultSampleSettings = (AudioImporterSampleSettings)plan.StructuredValue;
            if (plan.ApplyTexturePlatform)
                ((TextureImporter)plan.Importer).SetPlatformTextureSettings((TextureImporterPlatformSettings)plan.StructuredValue);
            if (plan.ClearAudioPlatform)
                ((AudioImporter)plan.Importer).ClearSampleSettingOverride(UnityPlatformName(plan.Platform));
            else if (plan.ApplyAudioPlatform)
                ((AudioImporter)plan.Importer).SetOverrideSampleSettings(UnityPlatformName(plan.Platform), (AudioImporterSampleSettings)plan.StructuredValue);
            plan.Importer.SaveAndReimport();
        }

        /// <summary>reimport 后重新获取 importer，并验证 GUID、类型和每个冻结 expected value。</summary>
        internal static AssetImportSettingsSnapshot Prove(MutationPlan plan)
        {
            var guid = AssetDatabase.AssetPathToGUID(plan.Path);
            if (!string.Equals(plan.Guid, guid, StringComparison.Ordinal))
                throw Error("E_IMPORT_SETTINGS_PROOF_FAILED", "The asset GUID changed during reimport.", plan.Path, plan.Platform);
            var importer = RequireImporter(plan.Path);
            if (!string.Equals(importer.GetType().AssemblyQualifiedName, plan.ImporterType, StringComparison.Ordinal))
                throw Error("E_IMPORT_SETTINGS_PROOF_FAILED", "The importer type changed during reimport.", plan.Path, plan.Platform);

            foreach (var write in plan.DefaultWrites) EnsureExpected(write, importer, plan);
            object structured = null;
            if (plan.ApplyAudioDefault) structured = ((AudioImporter)importer).defaultSampleSettings;
            else if (plan.ApplyTexturePlatform) structured = ((TextureImporter)importer).GetPlatformTextureSettings(UnityPlatformName(plan.Platform));
            else if (plan.ApplyAudioPlatform) structured = ((AudioImporter)importer).GetOverrideSampleSettings(UnityPlatformName(plan.Platform));
            if (structured != null)
                foreach (var write in plan.StructuredWrites) EnsureExpected(write, structured, plan);
            if (plan.ClearAudioPlatform && ((AudioImporter)importer).ContainsSampleSettingsOverride(UnityPlatformName(plan.Platform)))
                throw Error("E_IMPORT_SETTINGS_PROOF_FAILED", "The audio platform override still exists after it was cleared.", plan.Path, plan.Platform);
            if (plan.ApplyAudioPlatform && !((AudioImporter)importer).ContainsSampleSettingsOverride(UnityPlatformName(plan.Platform)))
                throw Error("E_IMPORT_SETTINGS_PROOF_FAILED", "The audio platform override was not observed after reimport.", plan.Path, plan.Platform);
            return Read(plan.Path, plan.Platform);
        }

        private static void PrepareDefault(MutationPlan plan, Dictionary<string, object> settings, List<AssetImportSettingIssue> issues)
        {
            object audioSample = null;
            if (plan.Importer is AudioImporter audio) audioSample = audio.defaultSampleSettings;
            foreach (var pair in settings.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (audioSample != null && AudioSampleKeys.Contains(pair.Key))
                {
                    var write = PrepareMember(audioSample, typeof(AudioImporterSampleSettings), pair.Key, pair.Value, issues);
                    if (write == null) continue;
                    write.Apply(audioSample);
                    plan.StructuredWrites.Add(write);
                    AddChange(plan, write);
                    continue;
                }
                var defaultWrite = PrepareMember(plan.Importer, plan.Importer.GetType(), pair.Key, pair.Value, issues);
                if (defaultWrite == null) continue;
                plan.DefaultWrites.Add(defaultWrite);
                AddChange(plan, defaultWrite);
            }
            if (plan.StructuredWrites.Count > 0)
            {
                plan.StructuredValue = audioSample;
                plan.ApplyAudioDefault = plan.StructuredWrites.Any(write => !ValuesEqual(write.Before, write.Expected));
            }
        }

        private static void PreparePlatform(MutationPlan plan, Dictionary<string, object> settings, List<AssetImportSettingIssue> issues)
        {
            if (plan.Importer is ModelImporter)
            {
                issues.Add(Issue("platform", "ModelImporter has no per-platform import override.", "Default", plan.Platform));
                return;
            }
            var overridden = ReadOptionalBoolean(settings, "overridden", issues);
            var businessKeys = settings.Keys.Where(key => !string.Equals(key, "overridden", StringComparison.OrdinalIgnoreCase)).ToList();
            if (overridden == false && businessKeys.Count > 0)
            {
                issues.Add(Issue("overridden", "overridden=false cannot be combined with platform setting values.", "false as the only setting", "combined settings"));
                return;
            }

            if (plan.Importer is TextureImporter texture)
            {
                object boxed = texture.GetPlatformTextureSettings(UnityPlatformName(plan.Platform));
                PrepareStructuredWrites(plan, boxed, typeof(TextureImporterPlatformSettings), businessKeys, settings, issues);
                var beforeOverride = ReadBooleanMember(boxed, "overridden");
                var expectedOverride = overridden ?? true;
                var overrideWrite = PrepareMember(boxed, typeof(TextureImporterPlatformSettings), "overridden", expectedOverride, issues);
                if (overrideWrite != null)
                {
                    overrideWrite.Apply(boxed);
                    plan.StructuredWrites.Add(overrideWrite);
                    if (!ValuesEqual(beforeOverride, expectedOverride)) AddChange(plan, overrideWrite);
                }
                SetStringMember(boxed, typeof(TextureImporterPlatformSettings), "name", UnityPlatformName(plan.Platform));
                plan.StructuredValue = boxed;
                plan.ApplyTexturePlatform = plan.Changes.Count > 0;
                return;
            }

            if (plan.Importer is AudioImporter audio)
            {
                var hasOverride = audio.ContainsSampleSettingsOverride(UnityPlatformName(plan.Platform));
                if (overridden == false)
                {
                    plan.ClearAudioPlatform = hasOverride;
                    if (hasOverride) plan.Changes.Add(Change("overridden", true, false));
                    return;
                }
                object boxed = audio.GetOverrideSampleSettings(UnityPlatformName(plan.Platform));
                PrepareStructuredWrites(plan, boxed, typeof(AudioImporterSampleSettings), businessKeys, settings, issues);
                if (!hasOverride) plan.Changes.Add(Change("overridden", false, true));
                plan.StructuredValue = boxed;
                plan.ApplyAudioPlatform = plan.Changes.Count > 0;
                return;
            }

            issues.Add(Issue("platform", "This importer has no supported per-platform override.", "TextureImporter or AudioImporter", plan.Importer.GetType().Name));
        }

        private static void PrepareStructuredWrites(MutationPlan plan, object boxed, Type type, List<string> keys, Dictionary<string, object> settings, List<AssetImportSettingIssue> issues)
        {
            foreach (var key in keys.OrderBy(value => value, StringComparer.Ordinal))
            {
                if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(Issue(key, "The platform name is owned by the Tool.", "omit this key", "provided"));
                    continue;
                }
                var write = PrepareMember(boxed, type, key, settings[key], issues);
                if (write == null) continue;
                write.Apply(boxed);
                plan.StructuredWrites.Add(write);
                AddChange(plan, write);
            }
        }

        private static MemberWrite PrepareMember(object target, Type type, string key, object raw, List<AssetImportSettingIssue> issues)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            var property = type.GetProperty(key, flags);
            var field = property == null ? type.GetField(key, flags) : null;
            if (property != null && (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0)) property = null;
            if (property == null && field == null)
            {
                issues.Add(Issue(key, "Unknown or non-writable importer member.", "public writable member", "unknown"));
                return null;
            }
            var memberType = property != null ? property.PropertyType : field.FieldType;
            object converted;
            if (!TryConvert(raw, memberType, out converted))
            {
                issues.Add(Issue(key, "The JSON value cannot be converted to the importer member type.", memberType.FullName, Actual(raw)));
                return null;
            }
            var write = new MemberWrite { Key = key, Property = property, Field = field, Expected = converted };
            try { write.Before = write.Read(target); }
            catch
            {
                issues.Add(Issue(key, "The importer member could not be read before effect.", memberType.FullName, "unreadable"));
                return null;
            }
            return write;
        }

        private static void AddChange(MutationPlan plan, MemberWrite write)
        {
            if (!ValuesEqual(write.Before, write.Expected)) plan.Changes.Add(Change(write.Key, PublicValue(write.Before), PublicValue(write.Expected)));
        }

        private static void EnsureExpected(MemberWrite write, object target, MutationPlan plan)
        {
            object actual;
            try { actual = write.Read(target); }
            catch { throw Error("E_IMPORT_SETTINGS_PROOF_FAILED", "A changed importer member could not be read: " + write.Key, plan.Path, plan.Platform); }
            if (!ValuesEqual(actual, write.Expected))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_IMPORT_SETTINGS_PROOF_FAILED",
                    "A changed importer member did not match the frozen expected value after reimport: " + write.Key,
                    new Dictionary<string, object>
                    {
                        { "path", plan.Path }, { "platform", plan.Platform }, { "setting", write.Key },
                        { "expected", PublicValue(write.Expected) }, { "actual", PublicValue(actual) }
                    },
                    new[] { "Read asset.import-settings before deciding whether a new write is safe." });
            }
        }

        private static AssetImporter RequireImporter(string path)
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null)
                throw Error("E_ASSET_IMPORTER_NOT_FOUND", "No AssetImporter exists for: " + path, path, "Default");
            return importer;
        }

        private static string CanonicalPlatform(string raw)
        {
            var value = string.IsNullOrEmpty(raw) ? "Default" : raw;
            foreach (var platform in Platforms)
                if (string.Equals(platform, value, StringComparison.OrdinalIgnoreCase)) return platform;
            throw new UnityCliBridge.BridgeCommandException(
                "E_IMPORT_SETTINGS_PLATFORM_INVALID",
                "Unsupported importer platform: " + value,
                new Dictionary<string, object> { { "platform", value }, { "supported", Platforms } },
                new[] { "Use Default, Standalone, iOS, Android, WebGL, or tvOS." });
        }

        private static string UnityPlatformName(string platform)
        {
            return string.Equals(platform, "iOS", StringComparison.Ordinal) ? "iPhone" : platform;
        }

        private static string Category(AssetImporter importer)
        {
            if (importer is TextureImporter) return "texture";
            if (importer is ModelImporter) return "model";
            if (importer is AudioImporter) return "audio";
            return "generic";
        }

        private static Dictionary<string, object> ReadDefaultSettings(AssetImporter importer)
        {
            var texture = importer as TextureImporter;
            if (texture != null)
            {
                return new Dictionary<string, object>
                {
                    { "textureType", texture.textureType.ToString() }, { "textureShape", texture.textureShape.ToString() },
                    { "sRGBTexture", texture.sRGBTexture }, { "alphaSource", texture.alphaSource.ToString() },
                    { "alphaIsTransparency", texture.alphaIsTransparency }, { "isReadable", texture.isReadable },
                    { "streamingMipmaps", texture.streamingMipmaps }, { "mipmapEnabled", texture.mipmapEnabled },
                    { "wrapMode", texture.wrapMode.ToString() }, { "filterMode", texture.filterMode.ToString() },
                    { "anisoLevel", texture.anisoLevel }, { "spriteImportMode", texture.spriteImportMode.ToString() },
                    { "spritePixelsPerUnit", texture.spritePixelsPerUnit }, { "npotScale", texture.npotScale.ToString() },
                    { "maxTextureSize", texture.maxTextureSize }
                };
            }
            var model = importer as ModelImporter;
            if (model != null)
            {
                return new Dictionary<string, object>
                {
                    { "globalScale", model.globalScale }, { "useFileScale", model.useFileScale },
                    { "meshCompression", model.meshCompression.ToString() }, { "isReadable", model.isReadable },
                    { "weldVertices", model.weldVertices }, { "indexFormat", model.indexFormat.ToString() },
                    { "keepQuads", model.keepQuads }, { "importNormals", model.importNormals.ToString() },
                    { "normalCalculationMode", model.normalCalculationMode.ToString() }, { "normalSmoothingAngle", model.normalSmoothingAngle },
                    { "importTangents", model.importTangents.ToString() }, { "importBlendShapes", model.importBlendShapes },
                    { "importCameras", model.importCameras }, { "importLights", model.importLights },
                    { "swapUVChannels", model.swapUVChannels }, { "generateSecondaryUV", model.generateSecondaryUV },
                    { "materialImportMode", model.materialImportMode.ToString() }, { "materialLocation", model.materialLocation.ToString() },
                    { "importAnimation", model.importAnimation }, { "animationType", model.animationType.ToString() },
                    { "importConstraints", model.importConstraints }, { "importVisibility", model.importVisibility }
                };
            }
            var audio = importer as AudioImporter;
            if (audio != null)
            {
                var sample = audio.defaultSampleSettings;
                return new Dictionary<string, object>
                {
                    { "forceToMono", audio.forceToMono }, { "loadInBackground", audio.loadInBackground },
                    { "ambisonic", audio.ambisonic },
                    { "loadType", sample.loadType.ToString() }, { "compressionFormat", sample.compressionFormat.ToString() },
                    { "quality", sample.quality }, { "sampleRateSetting", sample.sampleRateSetting.ToString() },
                    { "sampleRateOverride", sample.sampleRateOverride }
                };
            }
            return new Dictionary<string, object>
            {
                { "userData", importer.userData }, { "assetBundleName", importer.assetBundleName },
                { "assetBundleVariant", importer.assetBundleVariant }
            };
        }

        private static Dictionary<string, object> ReadPlatformSettings(AssetImporter importer, string platform)
        {
            var isDefault = string.Equals(platform, "Default", StringComparison.Ordinal);
            var texture = importer as TextureImporter;
            if (texture != null)
            {
                var value = isDefault ? texture.GetDefaultPlatformTextureSettings() : texture.GetPlatformTextureSettings(UnityPlatformName(platform));
                return new Dictionary<string, object>
                {
                    { "overridden", !isDefault && value.overridden }, { "maxTextureSize", value.maxTextureSize },
                    { "resizeAlgorithm", value.resizeAlgorithm.ToString() }, { "format", value.format.ToString() },
                    { "textureCompression", value.textureCompression.ToString() }, { "compressionQuality", value.compressionQuality },
                    { "crunchedCompression", value.crunchedCompression },
                    { "androidETC2FallbackOverride", value.androidETC2FallbackOverride.ToString() }
                };
            }
            var audio = importer as AudioImporter;
            if (audio != null)
            {
                var value = isDefault ? audio.defaultSampleSettings : audio.GetOverrideSampleSettings(UnityPlatformName(platform));
                return new Dictionary<string, object>
                {
                    { "overridden", !isDefault && audio.ContainsSampleSettingsOverride(UnityPlatformName(platform)) },
                    { "loadType", value.loadType.ToString() }, { "compressionFormat", value.compressionFormat.ToString() },
                    { "quality", value.quality }, { "sampleRateSetting", value.sampleRateSetting.ToString() },
                    { "sampleRateOverride", value.sampleRateOverride }
                };
            }
            return null;
        }

        private static bool TryConvert(object raw, Type rawType, out object converted)
        {
            converted = null;
            var nullable = Nullable.GetUnderlyingType(rawType);
            var type = nullable ?? rawType;
            if (raw == null)
            {
                if (nullable != null || !rawType.IsValueType) return true;
                return false;
            }
            if (type.IsEnum)
            {
                var text = raw as string;
                if (text != null)
                {
                    try
                    {
                        var parsed = Enum.Parse(type, text, true);
                        if (!IsDefinedEnumValue(type, parsed)) return false;
                        converted = parsed;
                        return true;
                    }
                    catch { return false; }
                }
                try
                {
                    var parsed = Enum.ToObject(type, Convert.ToInt64(raw, CultureInfo.InvariantCulture));
                    if (!IsDefinedEnumValue(type, parsed)) return false;
                    converted = parsed;
                    return true;
                }
                catch { return false; }
            }
            if (type == typeof(string))
            {
                converted = raw as string;
                return converted != null;
            }
            if (type == typeof(bool))
            {
                if (!(raw is bool)) return false;
                converted = raw;
                return true;
            }
            if (type == typeof(char))
            {
                var text = raw as string;
                if (text == null || text.Length != 1) return false;
                converted = text[0];
                return true;
            }
            if (UnityCliSchemaDeriver.IsInteger(type))
            {
                if (!IsJsonNumber(raw)) return false;
                try
                {
                    var number = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
                    if (decimal.Truncate(number) != number) return false;
                    converted = Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
                    return true;
                }
                catch { return false; }
            }
            if (UnityCliSchemaDeriver.IsNumber(type))
            {
                if (!IsJsonNumber(raw)) return false;
                try
                {
                    converted = Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
                    var number = Convert.ToDouble(converted, CultureInfo.InvariantCulture);
                    return !double.IsNaN(number) && !double.IsInfinity(number);
                }
                catch { return false; }
            }
            return false;
        }

        private static bool IsJsonNumber(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint
                || value is long || value is ulong || value is float || value is double || value is decimal;
        }

        private static bool IsDefinedEnumValue(Type type, object value)
        {
            if (Enum.IsDefined(type, value)) return true;
            if (!type.IsDefined(typeof(FlagsAttribute), false)) return false;
            try
            {
                var numeric = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                ulong allowed = 0;
                foreach (var declared in Enum.GetValues(type)) allowed |= Convert.ToUInt64(declared, CultureInfo.InvariantCulture);
                return (numeric & ~allowed) == 0;
            }
            catch { return false; }
        }

        private static bool? ReadOptionalBoolean(Dictionary<string, object> settings, string key, List<AssetImportSettingIssue> issues)
        {
            var pair = settings.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (pair.Key == null) return null;
            if (pair.Value is bool) return (bool)pair.Value;
            issues.Add(Issue(key, "The override switch must be a boolean.", "boolean", Actual(pair.Value)));
            return null;
        }

        private static bool ReadBooleanMember(object target, string key)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            var property = target.GetType().GetProperty(key, flags);
            if (property != null) return (bool)property.GetValue(target, null);
            var field = target.GetType().GetField(key, flags);
            return field != null && (bool)field.GetValue(target);
        }

        private static void SetStringMember(object target, Type type, string key, string value)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
            var property = type.GetProperty(key, flags);
            if (property != null && property.CanWrite) property.SetValue(target, value, null);
            else
            {
                var field = type.GetField(key, flags);
                if (field != null) field.SetValue(target, value);
            }
        }

        private static bool ValuesEqual(object left, object right)
        {
            return left == null ? right == null : left.Equals(right);
        }

        private static object PublicValue(object value)
        {
            return value != null && value.GetType().IsEnum ? value.ToString() : value;
        }

        private static string Actual(object value)
        {
            if (value == null) return "null";
            if (value is string) return "string";
            if (value is bool) return "boolean";
            if (value is long || value is int) return "integer";
            if (value is decimal || value is double || value is float) return "number";
            if (value is System.Collections.IList) return "array";
            if (value is IDictionary<string, object>) return "object";
            return value.GetType().Name;
        }

        private static AssetImportSettingChange Change(string key, object before, object after)
        {
            return new AssetImportSettingChange { Setting = key, Before = PublicValue(before), After = PublicValue(after) };
        }

        private static AssetImportSettingIssue Issue(string key, string reason, string expected, string actual)
        {
            return new AssetImportSettingIssue { Setting = key, Reason = reason, Expected = expected, Actual = actual };
        }

        private static UnityCliBridge.BridgeCommandException Error(string code, string message, string path, string platform)
        {
            return new UnityCliBridge.BridgeCommandException(
                code,
                message,
                new Dictionary<string, object> { { "path", path }, { "platform", platform } },
                new[] { "Run tools run asset.import-settings with the same path and platform to inspect current importer state." });
        }
    }

    /// <summary>一个 importer 的 stable path/GUID/type/version 和当前设置快照。</summary>
    public sealed class AssetImportSettingsSnapshot
    {
        public string Guid { get; set; }
        public string ImporterType { get; set; }
        public string ImporterCategory { get; set; }
        public string UnityVersion { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> Settings { get; set; }
        [UnityCliAnyJson] public Dictionary<string, object> PlatformOverride { get; set; }
    }

    public sealed class AssetImportSettingChange
    {
        public string Setting { get; set; }
        [UnityCliAnyJson] public object Before { get; set; }
        [UnityCliAnyJson] public object After { get; set; }
    }

    public sealed class AssetImportSettingIssue
    {
        public string Setting { get; set; }
        public string Reason { get; set; }
        public string Expected { get; set; }
        public string Actual { get; set; }
    }
}
#endif
