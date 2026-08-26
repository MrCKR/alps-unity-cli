#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// scriptable-object.modify — 修改 ScriptableObject 资产(单个属性或 patches[] 批量)。
    /// 复用 UnityCliBridge 内部 helper(ApplyScriptableObjectPatch/SetSerializedPropertyValue 等,
    /// 已改为 internal)避免重复复杂的 SerializedProperty 类型转换逻辑。
    /// </summary>
    public class ScriptableObjectModifyTool : IUnityCliTool<ScriptableObjectModifyTool.Request, ScriptableObjectModifyTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Path { get; set; }
            public string Target { get; set; }
            public string Property { get; set; }
            [UnityCliAnyJson] public object Value { get; set; }
            [UnityCliAnyJson] public List<object> Patches { get; set; }
        }

        public sealed class Result
        {
            public bool DryRun { get; set; }
            public bool Changed { get; set; }
            public ScriptableObjectInfo ScriptableObject { get; set; }
            [UnityCliAnyJson] public List<object> PatchResults { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scriptable-object.modify",
            Summary = "Modify a ScriptableObject asset by single property or SerializedObject patches[].",
            Description = "Modify a ScriptableObject asset at an Assets/*.asset path. Supports a single property/value pair or patches[] with SerializedObject propertyPath/value entries (op=set or array_resize). Validates all patches before applying; any failure means no changes. Supports dry-run.",
            Group = "scriptable-object",
            DestructiveHint = true,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            SupportsDryRun = true,
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Set a single property", Input = new Request { Path = "Assets/Data/Config.asset", Property = "maxItems", Value = 100 } },
                new ToolExample { Description = "Apply patches", Input = new Request { Path = "Assets/Data/Config.asset", Patches = new List<object> { new Dictionary<string, object> { { "propertyPath", "maxItems" }, { "value", 100 } } } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ScriptableObjectToolSupport.EnsureEditorNotBusy("scriptable-object.modify");
            var path = ScriptableObjectToolSupport.RequireScriptableObjectPath(request.Path ?? request.Target);
            var asset = ScriptableObjectToolSupport.LoadScriptableObject(path);

            if (request.Patches != null && request.Patches.Count > 0)
            {
                return new System.Threading.Tasks.ValueTask<Result>(ModifyPatches(path, asset, request.Patches, ctx));
            }

            var property = request.Property;
            if (string.IsNullOrEmpty(property))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PROPERTY_REQUIRED", "scriptable-object.modify requires property <name> or patches[].", "Pass a public writable field/property name, or patches with SerializedObject propertyPath/value entries.");
            }

            // 所有 member 查找与值转换必须在 barrier 前完成。
            var write = UnityCliBridge.PrepareObjectMemberWrite(asset, property, request.Value);
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    DryRun = true,
                    ScriptableObject = ScriptableObjectToolSupport.BuildScriptableObjectInfo(path, asset)
                });
            }

            ctx.CommitEffect();
            Undo.RecordObject(asset, "unitycli tools run scriptable-object.modify");
            UnityCliBridge.SetObjectMemberValue(asset, write);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var assetGuid = AssetDatabase.AssetPathToGUID(path);
            var observedPath = AssetDatabase.GUIDToAssetPath(assetGuid);
            var observedAsset = ScriptableObjectToolSupport.LoadScriptableObject(observedPath);
            if (observedAsset == null || !UnityCliBridge.ObjectMemberValueMatches(observedAsset, write))
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PROPERTY_READBACK_FAILED", "ScriptableObject member did not persist the exact requested value: " + property, "Inspect the target asset and retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Changed = true,
                ScriptableObject = ScriptableObjectToolSupport.BuildScriptableObjectInfo(path, observedAsset)
            });
        }

        /// <summary>先以 validateOnly 遍历全部 patch；commit 后只执行已证明可应用的写入。</summary>
        private static Result ModifyPatches(string path, ScriptableObject asset, List<object> patchItems, UnityCliProjectToolContext ctx)
        {
            if (patchItems.Count == 0)
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PATCHES_INVALID", "patches must be a non-empty JSON array.", "Pass patches like [{\"propertyPath\":\"fieldName\",\"value\":123}].");

            var validationObject = new SerializedObject(asset);
            validationObject.Update();
            var validationResults = new List<object>();
            var hasFailure = false;
            var changed = false;
            for (var i = 0; i < patchItems.Count; i++)
            {
                bool patchOk;
                bool patchChanged;
                var patchResult = UnityCliBridge.ApplyScriptableObjectPatch(validationObject, patchItems[i] as Dictionary<string, object>, i, true, out patchOk, out patchChanged);
                validationResults.Add(patchResult);
                if (!patchOk) hasFailure = true;
                if (patchChanged) changed = true;
            }
            if (hasFailure)
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PATCH_FAILED", "One or more ScriptableObject patches could not be applied.", "Fix failed patch propertyPath/value entries and retry; no changes were applied.");
            if (ctx.DryRun)
                return new Result { DryRun = true, Changed = false, ScriptableObject = ScriptableObjectToolSupport.BuildScriptableObjectInfo(path, asset), PatchResults = validationResults };

            ctx.CommitEffect();
            Undo.RecordObject(asset, "unitycli tools run scriptable-object.modify patches");
            var serialized = new SerializedObject(asset);
            serialized.Update();
            var appliedResults = new List<object>();
            for (var i = 0; i < patchItems.Count; i++)
            {
                bool patchOk;
                bool patchChanged;
                var patchResult = UnityCliBridge.ApplyScriptableObjectPatch(serialized, patchItems[i] as Dictionary<string, object>, i, false, out patchOk, out patchChanged);
                appliedResults.Add(patchResult);
                if (!patchOk)
                    throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PATCH_APPLY_FAILED", "A validated ScriptableObject patch failed during apply.", "Inspect the request journal and target asset before retrying.");
            }
            if (changed)
            {
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            var observedAsset = ScriptableObjectToolSupport.LoadScriptableObject(path);
            var observedSerialized = new SerializedObject(observedAsset);
            observedSerialized.Update();
            VerifyObservedPatches(observedSerialized, patchItems);
            return new Result
            {
                Changed = changed,
                ScriptableObject = ScriptableObjectToolSupport.BuildScriptableObjectInfo(path, observedAsset),
                PatchResults = appliedResults
            };
        }

        /// <summary>重新加载资产后逐项比较 patch 的实际持久化值；未知类型拒绝将结果视为已证明。</summary>
        private static void VerifyObservedPatches(SerializedObject observedSerialized, List<object> patchItems)
        {
            for (var index = 0; index < patchItems.Count; index++)
            {
                var patch = patchItems[index] as Dictionary<string, object>;
                object rawPath;
                if (patch == null || (!patch.TryGetValue("propertyPath", out rawPath) && !patch.TryGetValue("property_path", out rawPath) && !patch.TryGetValue("path", out rawPath)))
                    throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PATCH_READBACK_FAILED", "Applied patch has no readable property path.", "Inspect the request journal before retrying.");
                var propertyPath = UnityCliBridge.NormalizeSerializedPropertyPath(rawPath as string);
                object value;
                patch.TryGetValue("value", out value);
                object rawOp;
                var op = patch.TryGetValue("op", out rawOp) ? rawOp as string : "set";
                var property = observedSerialized.FindProperty(propertyPath);
                if (property == null)
                    throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PATCH_READBACK_FAILED", "Applied patch property could not be reloaded: " + propertyPath, "Inspect the target asset and request journal before retrying.");
                if (string.Equals(op, "array_resize", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (!property.isArray || property.arraySize != System.Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture))
                        throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PATCH_READBACK_FAILED", "Applied array resize did not persist: " + propertyPath, "Inspect the target asset and request journal before retrying.");
                    continue;
                }
                if (!PatchValueMatches(property, value))
                    throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PATCH_READBACK_FAILED", "Applied patch value did not persist: " + propertyPath, "Inspect the target asset and request journal before retrying.");
            }
        }

        private static bool PatchValueMatches(SerializedProperty property, object value)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.intValue == System.Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return property.boolValue == System.Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);
                case SerializedPropertyType.Float: return Mathf.Approximately(property.floatValue, System.Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture));
                case SerializedPropertyType.String: return property.stringValue == (value == null ? null : System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                case SerializedPropertyType.Color: return property.colorValue == (Color)UnityCliValueConverter.ConvertValue(value, typeof(Color));
                case SerializedPropertyType.ObjectReference: return property.objectReferenceValue == UnityCliValueConverter.ConvertValue(value, typeof(UnityEngine.Object)) as UnityEngine.Object;
                case SerializedPropertyType.Vector2: return property.vector2Value == (Vector2)UnityCliValueConverter.ConvertValue(value, typeof(Vector2));
                case SerializedPropertyType.Vector3: return property.vector3Value == (Vector3)UnityCliValueConverter.ConvertValue(value, typeof(Vector3));
                case SerializedPropertyType.Vector4: return property.vector4Value == (Vector4)UnityCliValueConverter.ConvertValue(value, typeof(Vector4));
                case SerializedPropertyType.Quaternion: return property.quaternionValue == (Quaternion)UnityCliValueConverter.ConvertValue(value, typeof(Quaternion));
                case SerializedPropertyType.Enum:
                    var requested = System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    var expectedIndex = int.TryParse(requested, out var numeric) ? numeric : System.Array.FindIndex(property.enumNames, name => string.Equals(name, requested, System.StringComparison.OrdinalIgnoreCase));
                    return property.enumValueIndex == expectedIndex;
                default: throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PATCH_READBACK_UNSUPPORTED", "Cannot prove persisted value for SerializedProperty type: " + property.propertyType, "Use a supported primitive, enum, object reference, color, vector, quaternion, or array_resize patch.");
            }
        }
    }
}
#endif
