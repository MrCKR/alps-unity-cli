#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Pipeline.Commands;

// 以下本地 authoring 类型保持官方 0.4 完整名称和公开 shape，用于验证不建立 package assembly 编译依赖的反射路径。
namespace Unity.Pipeline.Commands
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class CliCommandAttribute : Attribute
    {
        public CliCommandAttribute(string name, string description) { Name = name; Description = description; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public bool MainThreadRequired { get; set; } = true;
        public bool RuntimeOnly { get; set; }
    }

    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
    public sealed class CliArgAttribute : Attribute
    {
        public CliArgAttribute(string name, string description) { Name = name; Description = description; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public bool Required { get; set; }
        public object DefaultValue { get; set; }
    }

    public interface IStructuredCommandInput { }
}

/// <summary>DIR-016 官方 attribute discovery、schema、异常与 timeout 的专用 Dev fixture。</summary>
public static class UnityCliOfficialAttributeFixtures
{
    private static int timeoutCompletionCount;

    public sealed class StructuredInput : IStructuredCommandInput
    {
        [CliArg("display_name", "Display name returned by the fixture.", Required = true)]
        public string DisplayName { get; set; }

        [CliArg("count", "Count returned by the fixture.", Required = true)]
        public int Count { get; set; }
    }

    public sealed class FixtureResult
    {
        public string Kind { get; set; }
        public string Message { get; set; }
        public long Count { get; set; }
        public bool IsMainThread { get; set; }
    }

    public sealed class GetterFailureResult
    {
        public string Secret
        {
            get
            {
                throw new UnityCli.UnityCliBridge.BridgeCommandException(
                    "E_FIXTURE_GETTER_SECRET",
                    "official getter secret must not escape",
                    new Dictionary<string, object> { { "secret", "getter-sensitive-value" } },
                    new[] { "getter-sensitive-action" });
            }
            set { }
        }
    }

    [CliCommand("validation.official.sync", "Return a synchronous attributed result.", MainThreadRequired = true)]
    public static FixtureResult Sync(
        [CliArg("message", "Message to return.", Required = true)] string message,
        [CliArg("repeat", "Repeat count.")] int repeat = 1)
    {
        return new FixtureResult
        {
            Kind = "sync",
            Message = string.Join("|", System.Linq.Enumerable.Repeat(message, repeat)),
            Count = repeat,
            IsMainThread = true
        };
    }

    [CliCommand("validation.official.task", "Complete one non-generic attributed Task.", MainThreadRequired = false)]
    public static async Task TaskCommand([CliArg("delay_ms", "Delay before method return.", Required = true)] int delayMs)
    {
        await Task.Delay(delayMs).ConfigureAwait(false);
    }

    [CliCommand("validation.official.structured", "Return a Task result from structured input.", MainThreadRequired = true)]
    public static async Task<FixtureResult> Structured(
        [CliArg("payload", "Structured fixture payload.", Required = true)] StructuredInput payload)
    {
        await Task.Yield();
        return new FixtureResult
        {
            Kind = "structured",
            Message = payload.DisplayName,
            Count = payload.Count,
            IsMainThread = true
        };
    }

    [CliCommand("validation.official.any-output", "Return arbitrary JSON from arbitrary JSON input.")]
    public static object AnyOutput([CliArg("payload", "Arbitrary JSON payload.", Required = true)] object payload)
    {
        return new Dictionary<string, object> { { "kind", "any-output" }, { "payload", payload } };
    }

    [CliCommand("validation.official.dictionary", "Return a result from structured dictionary values.")]
    public static FixtureResult DictionaryInput(
        [CliArg("items", "Structured values keyed by name.", Required = true)] Dictionary<string, StructuredInput> items)
    {
        var first = System.Linq.Enumerable.First(items.Values);
        return new FixtureResult { Kind = "dictionary", Message = first.DisplayName, Count = first.Count, IsMainThread = true };
    }

    [CliCommand("validation.official.jobject", "Round-trip an arbitrary JSON object without Unity side effects.")]
    public static Dictionary<string, object> JObjectRoundTrip(
        [CliArg("payload", "Arbitrary JSON object payload.", Required = true)] Dictionary<string, object> payload)
    {
        payload["observed"] = true;
        return payload;
    }

    [CliCommand("test", "Return the attributed owner for command collision validation.")]
    public static FixtureResult AttributeTest()
    {
        return new FixtureResult { Kind = "attribute-test", Message = "attribute", Count = 1, IsMainThread = true };
    }

    [CliCommand("validation.official.result-getter-throw", "Return a result whose getter throws during serialization.")]
    public static GetterFailureResult ResultGetterThrow()
    {
        return new GetterFailureResult();
    }

    [CliCommand("validation.official.runtime", "Return a RuntimeOnly attributed result.", RuntimeOnly = true)]
    public static FixtureResult RuntimeOnly()
    {
        return new FixtureResult { Kind = "runtime", Message = "playing", Count = 1, IsMainThread = true };
    }

    [CliCommand("validation.official.timeout", "Delay beyond the caller timeout and then record completion.", MainThreadRequired = false)]
    public static async Task<FixtureResult> Timeout(
        [CliArg("delay_ms", "Delay before completion.", Required = true)] int delayMs)
    {
        await Task.Delay(delayMs).ConfigureAwait(false);
        var count = Interlocked.Increment(ref timeoutCompletionCount);
        return new FixtureResult { Kind = "timeout", Message = "completed", Count = count, IsMainThread = false };
    }

    [CliCommand("validation.official.timeout-status", "Read how many timed-out fixture methods eventually completed.")]
    public static FixtureResult TimeoutStatus()
    {
        return new FixtureResult { Kind = "timeout-status", Message = "observed", Count = timeoutCompletionCount, IsMainThread = true };
    }

    [CliCommand("validation.official.throw", "Throw after attributed method dispatch.")]
    public static FixtureResult Throw()
    {
        throw new UnityCli.UnityCliBridge.BridgeCommandException(
            "E_FIXTURE_SECRET",
            "official fixture secret must not escape",
            new Dictionary<string, object> { { "secret", "fixture-sensitive-value" } },
            new[] { "fixture-sensitive-action" });
    }
}

/// <summary>与CliCommand("test")构造真实重名，验证command精确native优先。</summary>
public sealed class UnityCliNativeTestTool : UnityCli.IUnityCliTool<UnityCliNativeTestTool.Request, UnityCliNativeTestTool.Result>
{
    public sealed class Request { }
    public sealed class Result { public string Owner { get; set; } }

    public UnityCli.ToolDescriptor Describe()
    {
        return new UnityCli.ToolDescriptor
        {
            Name = "test",
            Summary = "Return the native owner for command collision validation.",
            Description = "Read-only Dev fixture proving that command exact native names win over attributed original names.",
            Group = "validation",
            DestructiveHint = false,
            SideEffects = new string[0],
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[] { new UnityCli.ToolExample { Description = "Return the native collision owner.", Input = new Request() } },
            Completion = new UnityCli.ToolCompletionDescriptor
            {
                ExecutionClass = "atomic",
                CompletionPredicate = "typed-business-terminal",
                ProofSource = "typed-result-and-effect-readback",
                ReloadPolicy = "same-session-proof-recovery",
                TimeoutError = "E_EXECUTION_TIMEOUT",
                OutcomeUnknownPolicy = "caller-decides-after-timeout",
                CompletionDriverId = "atomic-typed-result-v1"
            }
        };
    }

    public ValueTask<Result> ExecuteAsync(Request request, UnityCli.UnityCliProjectToolContext ctx, CancellationToken ct = default)
    {
        return new ValueTask<Result>(new Result { Owner = "native" });
    }
}
#endif
