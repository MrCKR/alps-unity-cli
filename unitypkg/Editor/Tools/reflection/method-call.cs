#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;

namespace UnityCli
{
    /// <summary>
    /// reflection.method.call — 通过反射调用任意 C# 方法。高风险(--yes)。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ReflectionMethodCallTool : IUnityCliTool<ReflectionMethodCallTool.Request, ReflectionMethodCallTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string TypeName { get; set; }
            [UnityCliRequired]
            public string MethodName { get; set; }
            public string Args { get; set; }
            public string InstanceRef { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool Confirmed { get; set; }
            public bool RequiresConfirmation { get; set; }
            public string RiskLevel { get; set; }
            public bool DestructiveHint { get; set; }
            public string TypeName { get; set; }
            public string MethodName { get; set; }
            public object Data { get; set; }
            public List<PlannedEffect> PlannedEffects { get; set; }
        }

        public sealed class PlannedEffect
        {
            public string Type { get; set; }
            public string Target { get; set; }
            public string SideEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "reflection.method.call",
            Summary = "Call any C# method by reflection; arbitrary side effects, requires --yes.",
            Description = "Call any C# method by reflection on a live type. Does not limit scope — arbitrary side effects are possible. Requires --yes. Run reflection.method.find to discover method signatures first. Dry-run returns a preview without execution.",
            Group = "reflection",
            Version = "1.0.0",
            DestructiveHint = true,
            SideEffects = new[] { "arbitrary-method-invocation" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            SupportsDryRun = true,
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview a reflection call (dry-run)", Input = new Request { TypeName = "UnityEngine.Transform", MethodName = "Translate" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (string.IsNullOrEmpty(request.TypeName) || string.IsNullOrEmpty(request.MethodName))
            {
                throw new UnityCliBridge.BridgeCommandException("E_METHOD_TARGET_REQUIRED",
                    "reflection.method.call requires typeName and methodName.",
                    "Run reflection.method.find to discover methods, then call with --yes.");
            }

            // dry-run 预览:不执行,返回确认要求
            var bindings = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var json = UnityCliBridge.ReflectionInvoker.CallMethod(
                request.TypeName, request.MethodName, false,
                request.Args, request.InstanceRef, bindings);
            var data = UnityCliToolJson.Parse(json);
            return new Result
            {
                Command = "reflection.method.call",
                Confirmed = true,
                RequiresConfirmation = false,
                RiskLevel = "critical",
                DestructiveHint = true,
                TypeName = request.TypeName,
                MethodName = request.MethodName,
                Data = data,
                PlannedEffects = new List<PlannedEffect>
                {
                    new PlannedEffect { Type = "reflection.method.call", Target = request.TypeName + "." + request.MethodName, SideEffects = "arbitrary-method-invocation" }
                }
            };
        }
    }
}
#endif
