#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// scriptable-object.create — 在 Assets/*.asset 创建新 ScriptableObject 资产。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ScriptableObjectCreateTool : IUnityCliTool<ScriptableObjectCreateTool.Request, ScriptableObjectCreateTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Path { get; set; }
            [UnityCliRequired]
            public string Type { get; set; }
            public bool Overwrite { get; set; }
            public bool DryRun { get; set; }
        }

        public sealed class Result
        {
            public bool DryRun { get; set; }
            public string Path { get; set; }
            public string Type { get; set; }
            public bool Overwrite { get; set; }
            public ScriptableObjectInfo ScriptableObject { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scriptable-object.create",
            Summary = "Create a new ScriptableObject asset at an Assets/*.asset path.",
            Description = "Create a new ScriptableObject asset at an Assets/*.asset path. Resolves the type by full name or class name. Refuses to overwrite an existing asset unless overwrite=true. Supports dry-run.",
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
                new ToolExample { Description = "Create a ScriptableObject", Input = new Request { Path = "Assets/Data/Config.asset", Type = "MyConfig" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            ScriptableObjectToolSupport.EnsureEditorNotBusy("scriptable-object.create");
            var path = ScriptableObjectToolSupport.RequireScriptableObjectPath(request.Path);
            var type = ScriptableObjectToolSupport.ResolveScriptableObjectType(request.Type);
            if (AssetDatabase.LoadAssetAtPath<ScriptableObject>(path) != null && !request.Overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_EXISTS", "ScriptableObject asset already exists: " + path, "Pass overwrite=true or choose a different Assets/*.asset path.");
            }
            if (request.DryRun)
            {
                return new Result { DryRun = true, Path = path, Type = type.FullName, Overwrite = request.Overwrite };
            }
            ScriptableObjectToolSupport.CreateAssetDirectory(path);
            if (request.Overwrite && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
            {
                AssetDatabase.DeleteAsset(path);
            }
            var asset = ScriptableObject.CreateInstance(type);
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            return new Result
            {
                Path = path,
                Type = type.FullName,
                ScriptableObject = ScriptableObjectToolSupport.BuildScriptableObjectInfo(path, asset)
            };
        }
    }
}
#endif
