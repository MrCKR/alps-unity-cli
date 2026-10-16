#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// 主线程暂停控制通道唯一的桥接面。
        ///
        /// 这些成员只把既有的同一 registry、descriptor、schema 校验、安全判定与 envelope 交给
        /// <see cref="DebugPauseControl"/>，不新建第二套 dispatcher、schema 或 envelope 形状。
        /// 公开调用可见的能力清单仍然只有一份：由 live registry 发布的同一 catalog。
        /// </summary>
        internal static long OffMainListenerGeneration()
        {
            return CurrentListenerGeneration();
        }

        /// <summary>与正常终态完全相同的成功 envelope。</summary>
        internal static string SerializeOffMainSuccess(string requestId, object data)
        {
            return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(requestId, data));
        }

        /// <summary>与正常终态完全相同的失败 envelope；错误对象仍是闭集。</summary>
        internal static string SerializeOffMainFailure(string requestId, BridgeCommandException exception)
        {
            return SerializeBridgeExceptionEnvelope(requestId, exception);
        }

        /// <summary>
        /// 暂停期间一个可达 Tool 的成功终态数据。
        ///
        /// 公开 CLI 只把 catalog.editor.run 的成功响应按 data.result 投影成业务 DTO，因此 off-main 通道
        /// 必须产出与正常执行路径完全相同的终态形状；这里只把结果交给那个唯一构造点
        /// （<see cref="CompleteProjectToolSuccess"/>），既不复制包装也不引入第三种 envelope 形状。
        /// 失败仍走 <see cref="SerializeOffMainFailure"/> 的既有路径。
        /// </summary>
        internal static object OffMainToolTerminalData(BridgeRequest request, RegisteredTool definition, object result)
        {
            return CompleteProjectToolSuccess(request, definition, result);
        }

        /// <summary>
        /// 在主线程暂停期间用同一 registry handler 服务一个只读目录命令。
        /// 可用性判定改为读取主线程 tick 已冻结的 mode 缓存：后台线程不得调用 Unity API，
        /// 而主线程停住时 mode 不可能再变化，因此该缓存仍是当前 mode。
        /// 执行带一个由本次请求剩余预算推导的有界取消源，不引入第三份预算。
        /// </summary>
        internal static object ExecuteRegistryCommandOffMain(BridgeRequest request, bool offMainAvailability, int timeoutMs)
        {
            var previous = CatalogOffMainRead;
            CatalogOffMainRead = offMainAvailability;
            using (var bounded = new CancellationTokenSource(Math.Max(1, timeoutMs)))
            {
                try
                {
                    var pending = CommandRegistry.ExecuteAsync(request, bounded.Token);
                    return pending.IsCompletedSuccessfully ? pending.Result : pending.AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    CatalogOffMainRead = previous;
                }
            }
        }

        /// <summary>
        /// 以主线程冻结的 mode 缓存服务一次暂停期间的 Tool 请求。
        ///
        /// 存在的唯一原因：暂停线程一旦停住就不再推进，后台线程调用 EditorApplication.isPlaying 一类
        /// 主线程专属 API 会直接抛出异常。目录命令路径（<see cref="ExecuteRegistryCommandOffMain"/>）已经使用这条冻结缓存，
        /// 而 Tool 路径原先没有设置它，于是 PlayMode 门禁在真正服务请求之前就抛出 UnityException：
        /// 调用方拿不到任何 envelope（只看到 requestId 为空的错配响应），resume 也永远到不了现场。
        /// 主线程停住时 mode 不可能再变化，因此这里读取的缓存仍是当前 mode，门禁语义不变。
        /// </summary>
        internal static object ServeOffMainTool(Func<object> serve)
        {
            var previous = CatalogOffMainRead;
            CatalogOffMainRead = true;
            try { return serve(); }
            finally { CatalogOffMainRead = previous; }
        }

        /// <summary>暂停期间按同一 published catalog 取得可执行 Tool，并复用同一 PlayMode/目标门禁。</summary>
        internal static RegisteredTool RequireOffMainTool(string name)
        {
            var catalog = ReadPublishedProjectToolCatalog();
            RegisteredTool definition;
            if (string.IsNullOrEmpty(name) || !catalog.ExecutableTools.TryGetValue(name, out definition))
            {
                var unavailable = CatalogToolUnavailable(catalog, name);
                if (unavailable != null) throw unavailable;
                throw new BridgeCommandException("E_TOOL_NOT_FOUND", "Tool not found: " + name, "Run unitycli tools group --instance <id|projectPath|alias> or tools search <keyword> to inspect the connected instance catalog.");
            }
            var playing = IsPlayingNow();
            if (playing && string.Equals(definition.PlayModeSupport, "editor-only", StringComparison.OrdinalIgnoreCase))
                throw new BridgeCommandException(
                    "E_TOOL_PLAYMODE_UNAVAILABLE",
                    "Tool " + name + " is editor-only and cannot run while Unity is in Play Mode.",
                    new Dictionary<string, object> { { "toolName", name }, { "playModeSupport", definition.PlayModeSupport }, { "isPlaying", true } },
                    new[] { "Exit Play Mode before running this tool, or use a runtime tool with PlayModeSupport=runtime-only." });
            if (!playing && string.Equals(definition.PlayModeSupport, "runtime-only", StringComparison.OrdinalIgnoreCase))
                throw new BridgeCommandException(
                    "E_TOOL_PLAYMODE_UNAVAILABLE",
                    "Tool " + name + " is runtime-only and cannot run outside Play Mode.",
                    new Dictionary<string, object> { { "toolName", name }, { "playModeSupport", definition.PlayModeSupport }, { "isPlaying", false } },
                    new[] { "Enter Play Mode before running this Tool." });
            if (string.Equals(definition.Target, "runtime", StringComparison.Ordinal))
                throw new BridgeCommandException(
                    "E_RUNTIME_TOOL_TARGET_MISMATCH",
                    "Tool " + name + " has Target=runtime and must be executed via the runtime path (catalog.player.run).",
                    new Dictionary<string, object> { { "toolName", name }, { "toolTarget", definition.Target } },
                    new[] { "Use catalog.player.run <name> or tools run <name> with a runtime target to execute this tool in Play Mode." });
            return definition;
        }

        /// <summary>暂停期间与正常路径使用同一输入 schema 校验；失败形状也完全相同。</summary>
        internal static void RequireOffMainToolInput(RegisteredTool definition, string inputJson)
        {
            var schemaError = ValidateProjectToolInput(definition, inputJson);
            if (schemaError != null) throw SchemaValidationException(schemaError);
        }

        /// <summary>暂停期间按同一 serializer 反序列化业务参数。</summary>
        internal static T DeserializeOffMain<T>(string inputJson)
        {
            return UnityCliSerializer.Deserialize<T>(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
        }

        /// <summary>请求自身剩余预算；暂停控制通道只在它之内工作，不创建第三份预算。</summary>
        internal static long OffMainDeadlineMs(BridgeRequest request)
        {
            int remainingMs;
            if (request == null || !request.TryDeadlineRemainingMs(out remainingMs)) return DebugLimits.PauseControlMaxWaitMs;
            return remainingMs;
        }

        /// <summary>当前请求 id；暂停控制通道用它构造现场操作的来源标记。</summary>
        internal static string OffMainRequestId(BridgeRequest request)
        {
            return request == null || string.IsNullOrEmpty(request.RequestId) ? "control" : request.RequestId;
        }
    }
}
#endif
