#if UNITY_EDITOR
using System;

namespace UnityCli
{
    /// <summary>
    /// 命名了 pauseId 的值读写：现场操作在暂停线程上执行。
    ///
    /// 规则：
    /// - 编译与对象访问都不新增 owner：读写复用 <see cref="DebugValueSupport"/> 的同一解析与写入路径；
    /// - 请求预算是本次现场操作自己的账本，不共用调用方请求的账本，也不新建第二种预算语义；
    /// - 本类不注册任何 update 回调，也不解暂现场：不返回时调用方只能得到 pending。
    /// </summary>
    internal static class DebugPauseValue
    {
        /// <summary>在暂停线程上读取值；失败抛出与普通读取相同的稳定业务错误。</summary>
        internal static DebugValueReadResult Read(DebugPauseOperation operation)
        {
            var request = operation == null || operation.ValueReadRequest == null
                ? new DebugValueReadRequest()
                : operation.ValueReadRequest;
            return DebugValueSupport.Read(request, NewBudget(operation));
        }

        /// <summary>在暂停线程上写入值；准备阶段失败时不产生任何写入。</summary>
        internal static DebugValueWriteResult Write(DebugPauseOperation operation)
        {
            var request = operation == null || operation.ValueWriteRequest == null
                ? new DebugValueWriteRequest()
                : operation.ValueWriteRequest;
            var budget = NewBudget(operation);
            var plan = DebugValueSupport.PrepareWrite(request, budget);
            return DebugValueSupport.ApplyWrite(plan, request);
        }

        /// <summary>现场操作的读取预算；现场终结时随操作取消源一起中止。</summary>
        private static UnityCliRequestBudget NewBudget(DebugPauseOperation operation)
        {
            var token = operation == null ? default(System.Threading.CancellationToken) : operation.Cancellation.Token;
            return new UnityCliRequestBudget(
                new UnityCliReadBudgetInput { MaxGetters = DebugLimits.ValueReadGetters() },
                DebugLimits.PauseOperationTimeoutMs,
                token);
        }
    }
}
#endif
