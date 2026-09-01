using System;

namespace UnityCli
{
    /// <summary>WorkItem完成的纯finally裁决；settlement异常不能阻止移除与完成信号。</summary>
    public static class UnityCliQueueSettlement
    {
        public static Exception Complete(Action settle, Action remove, Action signal)
        {
            if (settle == null || remove == null || signal == null) throw new ArgumentNullException("completion action");
            Exception failure = null;
            try { settle(); }
            catch (Exception exception) { failure = exception; }
            finally
            {
                try { remove(); }
                finally { signal(); }
            }
            return failure;
        }
    }
}
