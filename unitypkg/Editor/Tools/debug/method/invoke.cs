#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;

namespace UnityCli
{
    /// <summary>
    /// <summary>通过反射调用任意 C# 方法。</summary>
    /// </summary>
    public class RuntimeReflectionMethodInvokeTool : IUnityCliTool<RuntimeReflectionMethodInvokeTool.Request, RuntimeReflectionMethodInvokeTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired]
            public string TypeName { get; set; }
            [UnityCliRequired]
            public string MethodName { get; set; }
            [UnityCliAnyJson] public object Args { get; set; }
            [UnityCliItemsRange(0, 256)] public List<string> ParameterTypes { get; set; }
            [UnityCliAnyJson] public Dictionary<string, object> InstanceRef { get; set; }
            [UnityCliRequired] public UnityCliCompletionProbe CompletionProbe { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            [UnityCliAnyJson] public object Data { get; set; }
            [UnityCliItemsRange(0, 1)] public List<PlannedEffect> PlannedEffects { get; set; }
            public UnityCliCompletionProbeReadback Completion { get; set; }
        }

        public sealed class PlannedEffect
        {
            public string Type { get; set; }
            public string Target { get; set; }
            public string SideEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "debug.method.invoke",
            Summary = "Call one synchronous C# method in the live Editor and prove declared state through a read Tool.",
            Description = "Resolve the exact type, overload, arguments, and optional instance under one request budget before invoking one synchronous method. completionProbe is mandatory and proves declared before/after state through an eligible read Tool in the same Editor turn. Candidate methods, parameters, signatures, reflection work, monotonic main-thread time, and output bytes are globally bounded in dry-run and execution. Task/ValueTask, iterator, yield, and AsyncOperation methods are rejected; a method return value is never accepted as completion proof.",
            Group = "debug",
            DestructiveHint = true,
            SideEffects = new[] { "arbitrary-method-invocation" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            SupportsDryRun = true,
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample
                {
                    Description = "Preview a synchronous overload without invoking it",
                    Input = new Request
                    {
                        TypeName = "UnityEngine.Mathf", MethodName = "Abs", Args = new object[] { -2.5f }, ParameterTypes = new List<string> { "System.Single" },
                        CompletionProbe = new UnityCliCompletionProbe
                        {
                            ReadTool = "scene.gameobject", Input = new Dictionary<string, object> { { "target", "Main Camera" } },
                            Assertions = new List<UnityCliCompletionAssertion>
                            {
                                new UnityCliCompletionAssertion { Path = "/gameObject/transform/position/x", Operator = "approx", Before = 0.0, After = 1.0, Tolerance = 0.0001 }
                            }
                        }
                    },
                    Execution = new ToolExampleExecution { DryRun = true }
                }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.TypeName) || string.IsNullOrEmpty(request.MethodName))
            {
                throw new UnityCliBridge.BridgeCommandException("E_METHOD_TARGET_REQUIRED",
                    "debug.method.invoke requires typeName and methodName.",
                    "Run tools run debug.method.find to discover methods, then retry with the required target fields.");
            }

            var readBudget = ctx.UseReadBudget(request.Budget);
            var bindings = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            // overload、参数与 instance 都是可预见校验，必须在 effect barrier 前完成并使用同一 budget。
            var prepared = UnityCliBridge.ReflectionInvoker.PrepareCall(
                request.TypeName, request.MethodName,
                request.Args, request.ParameterTypes, request.InstanceRef, bindings, readBudget);

            // dry-run 只完成有界 preparation，不执行目标方法。
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    PlannedEffects = new List<PlannedEffect>
                    {
                        new PlannedEffect { Type = "debug.method.invoke", Target = request.TypeName + "." + request.MethodName, SideEffects = "arbitrary-method-invocation" }
                    }
                });
            }

            Dictionary<string, object> data = null;
            var completion = ctx.RunCompletionProbe(request.CompletionProbe, () => data = UnityCliBridge.ReflectionInvoker.InvokePrepared(prepared, readBudget));
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Data = data,
                Completion = completion,
                PlannedEffects = new List<PlannedEffect>
                {
                    new PlannedEffect { Type = "debug.method.invoke", Target = request.TypeName + "." + request.MethodName, SideEffects = "arbitrary-method-invocation" }
                }
            });
        }
    }
}
#endif
