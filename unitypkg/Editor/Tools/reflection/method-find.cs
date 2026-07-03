#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;

namespace UnityCli
{
    /// <summary>
    /// reflection.method.find — 查找类型的公开/私有/静态/实例方法。只读。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
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
            public List<MethodEntry> Methods { get; set; }
        }

        public sealed class MethodEntry
        {
            public string Name { get; set; }
            public bool IsStatic { get; set; }
            public bool IsPublic { get; set; }
            public bool IsPrivate { get; set; }
            public string ReturnType { get; set; }
            public string DeclaringType { get; set; }
            public List<ParameterEntry> Parameters { get; set; }
        }

        public sealed class ParameterEntry
        {
            public string Name { get; set; }
            public string ParameterType { get; set; }
            public bool HasDefaultValue { get; set; }
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
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

            // 调用内部 ReflectionInvoker(同 assembly 可访问),取 JSON 后解析为 DTO 树
            var json = UnityCliBridge.ReflectionInvoker.FindMethods(request.TypeName, request.MethodName, bindings);
            var parsed = UnityCliToolJson.Parse(json) as Dictionary<string, object>;
            return BuildResult(request.TypeName, parsed);
        }

        // 从解析的 JSON 字典构建 typed Result
        private static Result BuildResult(string typeName, Dictionary<string, object> parsed)
        {
            var result = new Result
            {
                Command = "reflection.method.find",
                TypeName = typeName,
                Count = parsed != null && parsed.TryGetValue("count", out var c) ? (long)c : 0,
                Methods = new List<MethodEntry>()
            };
            if (parsed != null && parsed.TryGetValue("methods", out var methodsObj) && methodsObj is List<object> methods)
            {
                foreach (var m in methods)
                {
                    if (!(m is Dictionary<string, object> md)) continue;
                    var entry = new MethodEntry
                    {
                        Name = md.TryGetValue("name", out var n) ? n as string : null,
                        IsStatic = md.TryGetValue("isStatic", out var st) && st is bool b && b,
                        IsPublic = md.TryGetValue("isPublic", out var pu) && pu is bool bp && bp,
                        IsPrivate = md.TryGetValue("isPrivate", out var pr) && pr is bool bpr && bpr,
                        ReturnType = md.TryGetValue("returnType", out var rt) ? rt as string : null,
                        DeclaringType = md.TryGetValue("declaringType", out var dt) ? dt as string : null,
                        Parameters = new List<ParameterEntry>()
                    };
                    if (md.TryGetValue("parameters", out var paramsObj) && paramsObj is List<object> paramList)
                    {
                        foreach (var p in paramList)
                        {
                            if (!(p is Dictionary<string, object> pd)) continue;
                            entry.Parameters.Add(new ParameterEntry
                            {
                                Name = pd.TryGetValue("name", out var pn) ? pn as string : null,
                                ParameterType = pd.TryGetValue("parameterType", out var pt) ? pt as string : null,
                                HasDefaultValue = pd.TryGetValue("hasDefaultValue", out var hdv) && hdv is bool hb && hb
                            });
                        }
                    }
                    result.Methods.Add(entry);
                }
            }
            return result;
        }
    }
}
#endif
