#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// scriptable-object.create — 在 Assets/*.asset 创建新 ScriptableObject 资产。
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
        }

        public sealed class Result
        {
            public bool DryRun { get; set; }
            public ScriptableObjectInfo ScriptableObject { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scriptable-object.create",
            Summary = "Create a new ScriptableObject asset at an Assets/*.asset path.",
            Description = "Create a new ScriptableObject asset at an Assets/*.asset path. Resolves the type by full name or class name. Refuses to overwrite an existing asset unless overwrite=true. Supports dry-run.",
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
                new ToolExample { Description = "Create a ScriptableObject", Input = new Request { Path = "Assets/Data/Config.asset", Type = "MyConfig" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ScriptableObjectToolSupport.EnsureEditorNotBusy("scriptable-object.create");
            var path = ScriptableObjectToolSupport.RequireScriptableObjectPath(request.Path);
            var type = ScriptableObjectToolSupport.ResolveScriptableObjectType(request.Type);
            if (AssetDatabase.LoadAssetAtPath<ScriptableObject>(path) != null && !request.Overwrite)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_EXISTS", "ScriptableObject asset already exists: " + path, "Pass overwrite=true or choose a different Assets/*.asset path.");
            }
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result { DryRun = true });
            }
            ctx.CommitEffect();
            ScriptableObjectToolSupport.CreateAssetDirectory(path);
            if (request.Overwrite && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
            {
                AssetDatabase.DeleteAsset(path);
            }
            var asset = ScriptableObject.CreateInstance(type);
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var observedAsset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (observedAsset == null || observedAsset.GetType() != type)
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_CREATE_READBACK_FAILED", "Created ScriptableObject could not be reloaded with the requested type: " + path, "Check Unity Console for asset import errors.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                ScriptableObject = ScriptableObjectToolSupport.BuildScriptableObjectInfo(path, observedAsset)
            });
        }
    }
}
#endif
