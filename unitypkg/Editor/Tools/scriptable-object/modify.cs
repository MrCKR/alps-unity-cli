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
            public string Command { get; set; }
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
            Version = "1.0.0",
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
                return new System.Threading.Tasks.ValueTask<Result>(ModifyPatches(path, asset, request.Patches, ctx.DryRun));
            }

            var property = request.Property;
            if (string.IsNullOrEmpty(property))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PROPERTY_REQUIRED", "scriptable-object.modify requires property <name> or patches[].", "Pass a public writable field/property name, or patches with SerializedObject propertyPath/value entries.");
            }

            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    DryRun = true,
                    ScriptableObject = ScriptableObjectToolSupport.BuildScriptableObjectInfo(path, asset)
                });
            }

            Undo.RecordObject(asset, "unity_cli tools run scriptable-object.modify");
            UnityCliBridge.SetObjectMemberValue(asset, property, request.Value);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "scriptable-object.modify",
                Changed = true,
                ScriptableObject = ScriptableObjectToolSupport.BuildScriptableObjectInfo(path, asset)
            });
        }

        // patches 批量修改：逐项验证后整体应用，任一失败不落盘。
        private static Result ModifyPatches(string path, ScriptableObject asset, List<object> patchItems, bool dryRun)
        {
            if (patchItems.Count == 0)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PATCHES_INVALID", "patches must be a non-empty JSON array.", "Pass patches like [{\"propertyPath\":\"fieldName\",\"value\":123}].");
            }
            var so = new SerializedObject(asset);
            so.Update();
            var results = new List<object>();
            var hasFailure = false;
            var changed = false;
            if (!dryRun) Undo.RecordObject(asset, "unity_cli tools run scriptable-object.modify patches");
            for (var i = 0; i < patchItems.Count; i++)
            {
                bool patchOk;
                bool patchChanged;
                // 调用内部 helper(已改为 internal):验证/写入单个 SerializedProperty patch
                var patchResult = UnityCliBridge.ApplyScriptableObjectPatch(so, patchItems[i] as Dictionary<string, object>, i, dryRun, out patchOk, out patchChanged);
                results.Add(patchResult);
                if (!patchOk) hasFailure = true;
                if (patchChanged) changed = true;
            }
            if (hasFailure)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PATCH_FAILED", "One or more ScriptableObject patches could not be applied.", "Fix failed patch propertyPath/value entries and retry; no changes were applied.");
            }
            if (!dryRun && changed)
            {
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
            }
            return new Result
            {
                Command = "scriptable-object.modify",
                DryRun = dryRun,
                Changed = changed && !dryRun,
                ScriptableObject = ScriptableObjectToolSupport.BuildScriptableObjectInfo(path, asset),
                PatchResults = results
            };
        }
    }
}
#endif
