#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;

namespace UnityCli
{
    /// <summary>
    /// reflection.method.find — 查找类型的公开/私有/静态/实例方法。只读。
    /// </summary>
    public class ReflectionMethodFindTool : IUnityCliTool<ReflectionMethodFindTool.Request, ReflectionMethodFindTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string TypeName { get; set; }
            public string MethodName { get; set; }
            public bool IncludePrivate { get; set; }
            public bool IncludeStatic { get; set; } = true;
            public bool IncludeInstance { get; set; } = true;
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string TypeName { get; set; }
            public long Count { get; set; }
            public List<UnityCliBridge.ReflectionMethodDescriptor> Methods { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "reflection.method.find",
            Summary = "Find C# methods on a type by name; does not limit public/private/static/instance scope.",
            Description = "Find methods on a live C# type. Does not limit public/private/static/instance scope. Use the returned signatures to construct reflection.method.call calls. Read-only.",
            Group = "reflection",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Find Translate methods on Transform", Input = new Request { TypeName = "UnityEngine.Transform", MethodName = "Translate" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.TypeName))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TYPE_NAME_REQUIRED",
                    "reflection.method.find requires typeName.",
                    "Pass --input '{\"typeName\":\"UnityEngine.Transform\",\"methodName\":\"Translate\"}'.");
            }

            // 构建 BindingFlags:默认包含公开/非公开,按 includeStatic/includeInstance 调整
            var bindings = BindingFlags.Public | BindingFlags.NonPublic;
            if (request.IncludeStatic) bindings |= BindingFlags.Static;
            if (request.IncludeInstance) bindings |= BindingFlags.Instance;
            if (!request.IncludePrivate)
            {
                bindings = BindingFlags.Public | (request.IncludeStatic ? BindingFlags.Static : BindingFlags.Default) | (request.IncludeInstance ? BindingFlags.Instance : BindingFlags.Default);
            }

            var methods = UnityCliBridge.ReflectionInvoker.FindMethods(request.TypeName, request.MethodName, bindings);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "reflection.method.find",
                TypeName = request.TypeName,
                Count = methods.Count,
                Methods = methods
            });
        }
    }
}
#endif
