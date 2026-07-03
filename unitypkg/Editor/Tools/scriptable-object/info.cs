#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>
    /// scriptable-object.info — 读取 ScriptableObject 资产的属性摘要。只读。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ScriptableObjectInfoTool : IUnityCliTool<ScriptableObjectInfoTool.Request, ScriptableObjectInfoTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string Path { get; set; }
            public string Target { get; set; }
        }

        public sealed class Result
        {
            public ScriptableObjectInfo ScriptableObject { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scriptable-object.info",
            Summary = "Read ScriptableObject asset properties (path, guid, name, type, public fields).",
            Description = "Read a ScriptableObject asset's properties at an Assets/*.asset path. Returns path, guid, name, type, and public field/property values. Read-only.",
            Group = "scriptable-object",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Read a ScriptableObject", Input = new Request { Path = "Assets/Data/Config.asset" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var path = ScriptableObjectToolSupport.RequireScriptableObjectPath(request.Path ?? request.Target);
            var asset = ScriptableObjectToolSupport.LoadScriptableObject(path);
            return new Result
            {
                ScriptableObject = ScriptableObjectToolSupport.BuildScriptableObjectInfo(path, asset)
            };
        }
    }
}
#endif
