#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// scriptable-object.modify — 修改 ScriptableObject 资产(单个属性或 patches[] 批量)。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
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
            public string Value { get; set; }
            public string Patches { get; set; }
            public bool DryRun { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool DryRun { get; set; }
            public bool Changed { get; set; }
            public ScriptableObjectInfo ScriptableObject { get; set; }
            public List<object> PatchResults { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scriptable-object.modify",
            Summary = "Modify a ScriptableObject asset by single property or SerializedObject patches[].",
            Description = "Modify a ScriptableObject asset at an Assets/*.asset path. Supports a single property/value pair or patches[] with SerializedObject propertyPath/value entries (op=set or array_resize). Validates all patches before applying; any failure means no changes. Supports dry-run.",
            Group = "scriptable-object",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "asset-write" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            SupportsDryRun = true,
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Set a single property", Input = new Request { Path = "Assets/Data/Config.asset", Property = "maxItems", Value = "100" } },
                new ToolExample { Description = "Apply patches", Input = new Request { Path = "Assets/Data/Config.asset", Patches = "[{\"propertyPath\":\"maxItems\",\"value\":100}]" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            ScriptableObjectToolSupport.EnsureEditorNotBusy("scriptable-object.modify");
            var path = ScriptableObjectToolSupport.RequireScriptableObjectPath(request.Path ?? request.Target);
            var asset = ScriptableObjectToolSupport.LoadScriptableObject(path);

            // 用 BridgeRequest.Parse 取 patches/property/value/dryRun(结构化 JSON 字段)
            var bridgeRequest = UnityCliBridge.BridgeRequest.Parse(ctx.InputJson);
            var patchesJson = bridgeRequest.RawJsonArg("patches");

            if (!string.IsNullOrEmpty(patchesJson))
            {
                return ModifyPatches(path, asset, patchesJson, bridgeRequest.DryRun);
            }

            // 单属性修改路径
            var property = bridgeRequest.StringArg("property");
            if (string.IsNullOrEmpty(property))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PROPERTY_REQUIRED", "scriptable-object.modify requires property <name> or patches[].", "Pass a public writable field/property name, or patches with SerializedObject propertyPath/value entries.");
            }

            if (bridgeRequest.DryRun)
            {
                return new Result
                {
                    DryRun = true,
                    ScriptableObject = ScriptableObjectToolSupport.BuildScriptableObjectInfo(path, asset)
                };
            }

            Undo.RecordObject(asset, "unity_cli tools run scriptable-object.modify");
            UnityCliBridge.SetObjectMemberValue(asset, property, bridgeRequest.StringArg("value"));
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return new Result
            {
                Command = "scriptable-object.modify",
                Changed = true,
                ScriptableObject = ScriptableObjectToolSupport.BuildScriptableObjectInfo(path, asset)
            };
        }

        // patches 批量修改:逐项验证后整体应用,任一失败不落盘
        private static Result ModifyPatches(string path, ScriptableObject asset, string patchesJson, bool dryRun)
        {
            var patchItems = UnityCliBridge.JsonArrayElements(patchesJson);
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
                var resultJson = UnityCliBridge.ApplyScriptableObjectPatch(so, patchItems[i], i, dryRun, out patchOk, out patchChanged);
                results.Add(UnityCliToolJson.Parse(resultJson));
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
