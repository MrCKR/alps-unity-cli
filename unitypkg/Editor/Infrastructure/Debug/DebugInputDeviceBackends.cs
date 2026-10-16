#if UNITY_EDITOR
using System;
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// 可选设备后端契约：把“现场探测可用性、建立/移除会话拥有的虚拟设备、提交键或指针状态、读取确认”
    /// 收敛为一组可以拒绝的操作。
    ///
    /// 规则：
    /// - 所有权只属于本 debug 域：后端只能操作由它自己建立的设备，不得 reset 真实设备或其他 owner 的设备。
    /// - 每次调用都必须返回稳定原因而不是抛出：后端缺失、API 形状不符、设备被外部移除都是正常事实。
    /// - 读取确认是“实际确认”的唯一来源；没有确认就不能声称已释放。
    /// </summary>
    internal interface IUnityCliDebugInputDeviceBackend
    {
        /// <summary>后端稳定身份；未探测成功时为空。</summary>
        string Name { get; }

        /// <summary>当前是否可用；只有现场探测成功才为 true。</summary>
        bool Available { get; }

        /// <summary>不可用的稳定原因码；可用时为空。</summary>
        string UnavailableReason { get; }

        /// <summary>不可用的有界诊断文本；可用时为空。</summary>
        string UnavailableDetail { get; }

        /// <summary>探测到的实现身份（程序集与版本）；未知为空。</summary>
        string ApiIdentity { get; }

        /// <summary>探测时缺失的 API 成员（有界）；可用时为空。</summary>
        IList<string> MissingApiMembers { get; }

        /// <summary>建立一个本 owner 拥有的虚拟设备；失败时返回原因且不得留下任何设备。</summary>
        bool TryCreateDevice(string layout, string deviceName, out object device, out string reason);

        /// <summary>移除本 owner 建立的一个虚拟设备。</summary>
        bool TryRemoveDevice(object device, out string reason);

        /// <summary>提交键状态；keys 为空表示全部释放。</summary>
        bool TryQueueKeys(object device, IList<string> keys, out string reason);

        /// <summary>提交指针状态；buttons 为空表示全部释放，位置始终是目标坐标空间内的像素值。</summary>
        bool TryQueuePointer(object device, IList<string> buttons, double x, double y, out string reason);

        /// <summary>读取该设备上仍然按下的控制名；这是释放确认的唯一依据。</summary>
        bool TryReadHeldControls(object device, out IList<string> held, out string reason);

        /// <summary>读取后端当前全部设备名；用于确认本 owner 的设备确实不存在。</summary>
        bool TryReadDeviceNames(out IList<string> names, out string reason);

        /// <summary>把公开键名或按键名校验为该后端接受的取值。</summary>
        bool IsKnownControl(string name);
    }

    /// <summary>
    /// 设备后端的唯一解析点：每个 domain 首次使用时现场探测一次并缓存结果。
    ///
    /// 规则：
    /// - 探测只读类型与成员形状，不建立设备、不改写任何状态，也不安装任何包。
    /// - 探测失败时解析为不可用后端，并保留精确原因与缺失成员，供 describe 与拒绝路径如实报告。
    /// - Reset 让机制状态随 domain 失效；产品路径只经 Resolve 探测，测试用固定后端驱动确定性的 owner 语义。
    /// </summary>
    internal static class DebugInputDeviceBackends
    {
        private static readonly object Sync = new object();
        private static IUnityCliDebugInputDeviceBackend provider;
        private static bool resolved;

        /// <summary>当前 domain 的设备后端；不可用时返回不可用后端而不是 null。</summary>
        internal static IUnityCliDebugInputDeviceBackend Resolve()
        {
            lock (Sync)
            {
                if (!resolved)
                {
                    provider = DebugInputBackendInputSystem.Probe();
                    resolved = true;
                }
                return provider;
            }
        }

        /// <summary>安装一个固定后端；只用于让 Unity 测试确定性地驱动 owner 语义。</summary>
        internal static void UseProvider(IUnityCliDebugInputDeviceBackend value)
        {
            lock (Sync)
            {
                provider = value ?? DebugInputBackendInputSystem.Probe();
                resolved = true;
            }
        }

        /// <summary>本次 Unity 启动的设备后端解析作废；domain 重载与测试复位共用这里。</summary>
        internal static void Reset()
        {
            lock (Sync)
            {
                provider = null;
                resolved = false;
            }
        }

        /// <summary>不可用后端的唯一实现：所有操作都以稳定原因拒绝，绝不假装成功。</summary>
        internal static IUnityCliDebugInputDeviceBackend Unavailable(string reason, string detail, IList<string> missing)
        {
            return new UnavailableBackend(reason, detail, missing);
        }

        private sealed class UnavailableBackend : IUnityCliDebugInputDeviceBackend
        {
            private readonly string reason;
            private readonly string detail;
            private readonly IList<string> missing;

            internal UnavailableBackend(string reason, string detail, IList<string> missing)
            {
                this.reason = string.IsNullOrEmpty(reason) ? UnityCliDebugInputReasons.BackendUnsupported : reason;
                this.detail = detail ?? string.Empty;
                this.missing = missing ?? new List<string>();
            }

            public string Name { get { return UnityCliDebugInputBackends.None; } }

            public bool Available { get { return false; } }

            public string UnavailableReason { get { return reason; } }

            public string UnavailableDetail { get { return detail; } }

            public string ApiIdentity { get { return string.Empty; } }

            public IList<string> MissingApiMembers { get { return missing; } }

            public bool TryCreateDevice(string layout, string deviceName, out object device, out string deviceReason) { device = null; deviceReason = reason; return false; }

            public bool TryRemoveDevice(object device, out string removeReason) { removeReason = reason; return false; }

            public bool TryQueueKeys(object device, IList<string> keys, out string queueReason) { queueReason = reason; return false; }

            public bool TryQueuePointer(object device, IList<string> buttons, double x, double y, out string queueReason) { queueReason = reason; return false; }

            public bool TryReadHeldControls(object device, out IList<string> held, out string readReason) { held = new List<string>(); readReason = reason; return false; }

            public bool TryReadDeviceNames(out IList<string> names, out string readReason) { names = new List<string>(); readReason = reason; return false; }

            public bool IsKnownControl(string name) { return false; }
        }
    }
}
#endif
