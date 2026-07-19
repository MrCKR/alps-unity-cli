#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;

namespace UnityCli
{
    /// <summary>
    /// reflection.method.call — 通过反射调用任意 C# 方法。高风险(--yes)。
    /// </summary>
    public class ReflectionMethodCallTool : IUnityCliTool<ReflectionMethodCallTool.Request, ReflectionMethodCallTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string TypeName { get; set; }
            [UnityCliRequired]
            public string MethodName { get; set; }
            [UnityCliAnyJson] public object Args { get; set; }
            public List<string> ParameterTypes { get; set; }
            [UnityCliAnyJson] public Dictionary<string, object> InstanceRef { get; set; }
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
            [UnityCliAnyJson] public object Data { get; set; }
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
            Summary = "Call a synchronous non-void C# method only inside the frozen batch delivery.",
            Description = "Ordinary direct invocation is refused because a synchronous return value cannot prove arbitrary side effects. The existing frozen batch delivery may call a synchronous non-void method and serialize its result. void, Task/ValueTask, iterator, yield, and AsyncOperation methods are rejected. Run reflection.method.find first and pass parameterTypes for exact overload selection.",
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
                new ToolExample { Description = "Evaluate a synchronous method with a concrete result", Input = new Request { TypeName = "UnityEngine.Mathf", MethodName = "Abs", Args = new object[] { -2.5f }, ParameterTypes = new List<string> { "System.Single" } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.TypeName) || string.IsNullOrEmpty(request.MethodName))
            {
                throw new UnityCliBridge.BridgeCommandException("E_METHOD_TARGET_REQUIRED",
                    "reflection.method.call requires typeName and methodName.",
                    "Run reflection.method.find to discover methods, then call with --yes.");
            }

            // dry-run 必须在任何反射调用前返回，任意方法即使看似只读也不能在预览阶段执行。
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Command = "reflection.method.call",
                    Confirmed = false,
                    RequiresConfirmation = true,
                    RiskLevel = "critical",
                    DestructiveHint = true,
                    TypeName = request.TypeName,
                    MethodName = request.MethodName,
                    PlannedEffects = new List<PlannedEffect>
                    {
                        new PlannedEffect { Type = "reflection.method.call", Target = request.TypeName + "." + request.MethodName, SideEffects = "arbitrary-method-invocation" }
                    }
                });
            }

            var bindings = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            ctx.CommitEffect();
            var data = UnityCliBridge.ReflectionInvoker.CallMethod(
                request.TypeName, request.MethodName,
                request.Args, request.ParameterTypes, request.InstanceRef, bindings);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
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
            });
        }
    }
}
#endif
