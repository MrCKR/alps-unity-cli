#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace UnityCli
{
    /// <summary>
    /// 一次 Hook Invocation 的宿主侧能力。由 host 实现，回调上下文只通过它读写当前调用。
    /// 定义在 core 程序集，因此动态编译的回调程序集与独立后端都能引用同一 ABI。
    /// </summary>
    public interface IUnityCliHookInvocation
    {
        /// <summary>被拦截方法的实例；静态方法为 null。</summary>
        object Instance { get; }

        /// <summary>当前调用的参数数组；Prefix 期间的改动会在本次调用生效。</summary>
        object[] Arguments { get; }

        /// <summary>当前结果值；Prefix 期间赋值表示替代结果，Postfix 期间赋值表示修改结果。</summary>
        object Result { get; set; }

        /// <summary>按真实参数类型检查后替换第 index 个参数；仅 Prefix 可用。</summary>
        void SetArgument(int index, object value);

        /// <summary>跳过原方法；void 返回类型无需替代值。</summary>
        void SkipOriginal();

        /// <summary>精确读写字段；declaringType 为空表示从实例类型向上解析。</summary>
        object ReadField(string declaringType, string fieldName);

        void WriteField(string declaringType, string fieldName, object value);

        /// <summary>写入有界业务事件。</summary>
        void Emit(string name, object value);

        /// <summary>该 hook 在当前 Play Session 或 Edit 运行段内的有界 JSON 字典。</summary>
        IDictionary<string, object> State { get; }

        string HookId { get; }

        string MethodIdentity { get; }

        string PlaySessionId { get; }

        string ClockSegment { get; }

        /// <summary>拦截点采集的单调时间（毫秒）；不是 CLI 收到响应的时间。</summary>
        double ElapsedMilliseconds { get; }

        int Frame { get; }

        bool IsPrefix { get; }
    }

    /// <summary>
    /// 回调代码可见的稳定上下文。只暴露能力，不暴露 host 内部状态、owner 身份或 patch 细节。
    /// </summary>
    public sealed class UnityCliHookContext
    {
        private readonly IUnityCliHookInvocation invocation;

        public UnityCliHookContext(IUnityCliHookInvocation invocation)
        {
            this.invocation = invocation ?? throw new ArgumentNullException("invocation");
        }

        public object Instance { get { return invocation.Instance; } }
        public object[] Arguments { get { return invocation.Arguments; } }
        public object Result { get { return invocation.Result; } set { invocation.Result = value; } }
        public string HookId { get { return invocation.HookId; } }
        public string MethodIdentity { get { return invocation.MethodIdentity; } }
        public string PlaySessionId { get { return invocation.PlaySessionId; } }
        public string ClockSegment { get { return invocation.ClockSegment; } }
        public double ElapsedMilliseconds { get { return invocation.ElapsedMilliseconds; } }
        public int Frame { get { return invocation.Frame; } }
        public bool IsPrefix { get { return invocation.IsPrefix; } }
        public IDictionary<string, object> State { get { return invocation.State; } }

        public void SetArgument(int index, object value) { invocation.SetArgument(index, value); }
        public void SetResult(object value) { invocation.Result = value; }
        public void SkipOriginal() { invocation.SkipOriginal(); }
        public void SkipOriginal(object result) { invocation.Result = result; invocation.SkipOriginal(); }
        public void Emit(string name, object value) { invocation.Emit(name, value); }

        public T ReadField<T>(string fieldName) { return Cast<T>(invocation.ReadField(null, fieldName), fieldName); }
        public T ReadField<T>(string declaringType, string fieldName) { return Cast<T>(invocation.ReadField(declaringType, fieldName), fieldName); }
        public void WriteField<T>(string fieldName, T value) { invocation.WriteField(null, fieldName, value); }
        public void WriteField<T>(string declaringType, string fieldName, T value) { invocation.WriteField(declaringType, fieldName, value); }

        /// <summary>读取结果值并做精确类型检查；类型不符时给出字段级可诊断错误。</summary>
        private static T Cast<T>(object value, string fieldName)
        {
            if (value == null)
            {
                if (default(T) == null) return default(T);
                throw new InvalidCastException("Field '" + fieldName + "' is null and cannot be read as " + typeof(T).FullName + ".");
            }
            if (value is T) return (T)value;
            var target = typeof(T);
            var underlying = Nullable.GetUnderlyingType(target);
            if (underlying != null && underlying.IsInstanceOfType(value)) return (T)value;
            throw new InvalidCastException("Field '" + fieldName + "' is " + value.GetType().FullName + " and cannot be read as " + target.FullName + ".");
        }
    }

    /// <summary>动态编译的回调必须实现的固定 wrapper ABI：同步 void，无 await、无返回值。</summary>
    public interface IUnityCliMethodHookCallback
    {
        void Prefix(UnityCliHookContext context);
        void Postfix(UnityCliHookContext context);
    }

    /// <summary>
    /// 后端适配层的最小接口。实现位于独立后端程序集，只负责 Harmony patch 安装/撤销与类型化桥接，
    /// 不注册公开 Tool、不启动 transport、不持久化定义。
    /// </summary>
    internal interface IUnityCliMethodHookBackend
    {
        bool Available { get; }

        string UnavailableReason { get; }

        string Version { get; }

        /// <summary>已加载依赖的 exact assembly identity。</summary>
        string DependencyIdentity { get; }

        /// <summary>已加载依赖的物理位置，供 core 核对 Package 来源。</summary>
        string DependencyLocation { get; }

        /// <summary>为 exact target 安装只属于本 CLI 的 Adapter；返回后端句柄。</summary>
        object Attach(MethodInfo target);

        /// <summary>撤销本 CLI 在该 exact target 上的 Adapter；不触碰其他 owner。</summary>
        void Detach(MethodInfo target);

        /// <summary>返回该 target 上有界的共存 patch owner 摘要。</summary>
        string OwnerSummary(MethodInfo target);

        /// <summary>返回本 CLI 在该 target 上的 patch 数量，用于 fresh readback。</summary>
        int OwnPatchCount(MethodInfo target);

        /// <summary>本 CLI 在该 exact target 上是否恰好只有一套 Adapter；重复 attach 必须被检出。</summary>
        bool OwnedExactlyOnce(MethodInfo target);
    }

    /// <summary>
    /// 拦截点入口。由后端生成的类型化 Adapter 在每次命中时调用，负责找到当前域的 dispatcher 并派发。
    /// 这些方法必须 public：动态生成的 Adapter 位于另一个程序集。
    /// </summary>
    public static class MethodHookInterception
    {
        private static IUnityCliMethodHookDispatcher dispatcher;

        internal static void Bind(IUnityCliMethodHookDispatcher value)
        {
            dispatcher = value;
        }

        /// <summary>供后端 IL 生成使用的 Prefix 入口；必须 public 才能被另一程序集的 Adapter 调用。</summary>
        public static MethodInfo PrefixEntry { get { return typeof(MethodHookInterception).GetMethod("Prefix", BindingFlags.Public | BindingFlags.Static); } }

        /// <summary>供后端 IL 生成使用的 Postfix 入口。</summary>
        public static MethodInfo PostfixEntry { get { return typeof(MethodHookInterception).GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static); } }

        /// <summary>Prefix 派发；返回是否继续执行原方法。host 缺席时保持原语义。</summary>
        public static bool Prefix(object instance, object[] arguments, MethodBase original, object result, out object resultOut, out bool writeResult)
        {
            writeResult = false;
            resultOut = result;
            var current = dispatcher;
            if (current == null) return true;
            return current.DispatchPrefix(instance, arguments, original, result, out resultOut, out writeResult);
        }

        /// <summary>Postfix 派发；只写回显式请求的结果替换。</summary>
        public static void Postfix(object instance, object[] arguments, MethodBase original, object result, out object resultOut, out bool writeResult)
        {
            writeResult = false;
            resultOut = result;
            var current = dispatcher;
            if (current == null) return;
            current.DispatchPostfix(instance, arguments, original, result, out resultOut, out writeResult);
        }

        /// <summary>内部 Finalizer：记录异常结束但不吞掉或替换原异常。</summary>
        public static Exception Finalizer(Exception exception, MethodBase original)
        {
            var current = dispatcher;
            if (current != null)
            {
                try { current.DispatchFinalizer(exception, original); }
                catch { /* 记录失败不得改变原异常传播 */ }
            }
            return exception;
        }
    }

    /// <summary>
    /// core 的派发接口，供 MethodHookInterception 调用的稳定形状；实现由 host 提供。
    /// </summary>
    internal interface IUnityCliMethodHookDispatcher
    {
        bool DispatchPrefix(object instance, object[] arguments, MethodBase original, object result, out object resultOut, out bool writeResult);

        void DispatchPostfix(object instance, object[] arguments, MethodBase original, object result, out object resultOut, out bool writeResult);

        void DispatchFinalizer(Exception exception, MethodBase original);
    }

    /// <summary>一次拦截点的宿主时间戳与身份；由 host 在派发时填充。</summary>
    internal sealed class MethodHookInterceptionStamp
    {
        internal string PlaySessionId;
        internal string ClockSegment;
        internal double ElapsedMilliseconds;
        internal int Frame;
    }

    /// <summary>exact target 的稳定键；重挂只按它重新解析，不复用旧域身份。</summary>
    internal sealed class MethodHookTargetKey
    {
        private MethodHookTargetKey(string text) { Text = text; }

        internal string Text { get; }

        internal static MethodHookTargetKey From(MethodBase method)
        {
            if (method == null) throw new ArgumentNullException("method");
            var declaring = method.DeclaringType;
            var parameters = method.GetParameters();
            var names = new List<string>();
            foreach (var parameter in parameters)
            {
                var type = parameter.ParameterType;
                names.Add(type.IsByRef ? type.GetElementType().FullName + "&" : type.FullName);
            }
            var returnType = method is MethodInfo info ? info.ReturnType.FullName : "System.Void";
            var assemblyName = declaring == null ? string.Empty : declaring.Assembly.GetName().Name;
            return new MethodHookTargetKey(assemblyName + "|" + (declaring == null ? string.Empty : declaring.FullName)
                + "|" + method.Name + "|" + string.Join(",", names.ToArray()) + "|" + returnType + "|" + (method.IsStatic ? "static" : "instance"));
        }

        public override string ToString() { return Text; }
    }

    /// <summary>把方法身份投影为公开可读文本，供 info/event 使用。</summary>
    internal static class MethodHookMethodText
    {
        internal static string Describe(MethodBase method)
        {
            if (method == null) return string.Empty;
            var declaring = method.DeclaringType;
            var builder = new System.Text.StringBuilder();
            builder.Append(declaring == null ? "<unknown>" : declaring.FullName);
            builder.Append('.');
            builder.Append(method.Name);
            builder.Append('(');
            var parameters = method.GetParameters();
            for (var index = 0; index < parameters.Length; index++)
            {
                if (index > 0) builder.Append(", ");
                var type = parameters[index].ParameterType;
                builder.Append(type.IsByRef ? type.GetElementType().Name : type.Name);
            }
            builder.Append(')');
            var info = method as MethodInfo;
            if (info != null && info.ReturnType != typeof(void)) builder.Append(" : " + info.ReturnType.Name);
            return builder.ToString();
        }

        internal static string AssemblyIdentity(MethodBase method)
        {
            return method == null || method.DeclaringType == null ? string.Empty : method.DeclaringType.Assembly.GetName().FullName;
        }
    }
}
#endif
