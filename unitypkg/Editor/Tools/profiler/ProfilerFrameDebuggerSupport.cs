#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Reflection;
using System.Threading;

namespace UnityCli
{
    /// <summary>Frame Debugger 的集中反射边界；读取调用显式消费调用方请求预算。</summary>
    internal static class FrameDebuggerReflection
    {
        public static readonly Type UtilityType = ProfilerToolSupport.FindType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility, UnityEditor");
        public static readonly Type StateType = ProfilerToolSupport.FindType("UnityEngine.FrameDebugger, UnityEngine.CoreModule");
        public static readonly PropertyInfo EventCountProp = UtilityType?.GetProperty("count", BindingFlags.Public | BindingFlags.Static);
        public static readonly PropertyInfo EnabledProp = StateType?.GetProperty("enabled", BindingFlags.Public | BindingFlags.Static);
        public static readonly MethodInfo EnableMethod = UtilityType?.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool), typeof(int) }, null);
        public static readonly MethodInfo EventNameMethod = UtilityType?.GetMethod("GetFrameEventInfoName", BindingFlags.Public | BindingFlags.Static);
        public static readonly Type EventDataType = ProfilerToolSupport.FindType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData, UnityEditor");
        public static readonly MethodInfo GetEventDataMethod = EventDataType == null
            ? null
            : UtilityType?.GetMethod("GetFrameEventData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), EventDataType.MakeByRefType() }, null)
                ?? UtilityType?.GetMethod("GetFrameEventData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), EventDataType }, null);
        private static long captureGeneration = 1L;

        internal static bool? AvailableOverrideForTests;
        internal static Func<bool> EnabledOverrideForTests;
        internal static Action<bool> SetEnabledOverrideForTests;
        internal static Func<int> EventCountOverrideForTests;
        internal static Func<int, string> EventNameOverrideForTests;
        internal static Func<int, object> EventDataOverrideForTests;
        internal static Func<string> GenerationOverrideForTests;

        public static bool Available => AvailableOverrideForTests ?? (UtilityType != null && StateType != null && EventCountProp != null && EnabledProp != null && EnableMethod != null);

        public static void SetEnabled(bool value)
        {
            var overrideAction = SetEnabledOverrideForTests;
            if (overrideAction != null) overrideAction(value);
            else EnableMethod.Invoke(null, new object[] { value, 0 });
            Interlocked.Increment(ref captureGeneration);
        }

        public static bool GetEnabled()
        {
            try
            {
                var overrideGetter = EnabledOverrideForTests;
                if (overrideGetter != null) return overrideGetter();
                if (EnabledProp != null) return (bool)EnabledProp.GetValue(null, null);
                throw new MissingMemberException("Frame Debugger enabled readback is unavailable.");
            }
            catch (Exception exception) { throw new InvalidOperationException("Frame Debugger enabled getter failed.", exception); }
        }

        public static int GetEventCount(UnityCliRequestBudget budget)
        {
            if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var overrideGetter = EventCountOverrideForTests;
            if (overrideGetter != null) return overrideGetter();
            try { return Convert.ToInt32(EventCountProp.GetValue(null, null), CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public static string EventName(int index, UnityCliRequestBudget budget)
        {
            if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var overrideGetter = EventNameOverrideForTests;
            if (overrideGetter != null) return overrideGetter(index);
            if (EventNameMethod == null) return null;
            try { return EventNameMethod.Invoke(null, new object[] { index }) as string; }
            catch { return null; }
        }

        public static object EventData(int index, UnityCliRequestBudget budget)
        {
            if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var overrideGetter = EventDataOverrideForTests;
            if (overrideGetter != null) return overrideGetter(index);
            if (GetEventDataMethod == null || EventDataType == null) return null;
            try
            {
                var value = Activator.CreateInstance(EventDataType);
                var args = new object[] { index, value };
                var ok = GetEventDataMethod.Invoke(null, args);
                return ok is true ? args[1] : null;
            }
            catch { return null; }
        }

        /// <summary>用有界常量工作量绑定当前捕获；CLI 切换、listener、数量或首尾事件变化都会使旧游标失效。</summary>
        internal static string CaptureGeneration(int eventCount, UnityCliRequestBudget budget)
        {
            string sourceIdentity;
            var overrideGetter = GenerationOverrideForTests;
            if (overrideGetter != null) sourceIdentity = overrideGetter();
            else
            {
                if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var enabled = GetEnabled();
                var first = eventCount > 0 ? EventName(0, budget) : string.Empty;
                var last = eventCount > 1 ? EventName(eventCount - 1, budget) : first;
                sourceIdentity = enabled.ToString(CultureInfo.InvariantCulture) + "\0" + eventCount.ToString(CultureInfo.InvariantCulture)
                    + "\0" + first + "\0" + last + "\0" + Interlocked.Read(ref captureGeneration).ToString(CultureInfo.InvariantCulture);
            }
            return UnityCliContinuationToken.Fingerprint(
                UnityCliReadGeneration.Session("profiler.frame-debugger-events") + "\0" + eventCount.ToString(CultureInfo.InvariantCulture)
                + "\0" + Interlocked.Read(ref captureGeneration).ToString(CultureInfo.InvariantCulture) + "\0" + sourceIdentity);
        }

        internal static void ResetTestOverrides()
        {
            AvailableOverrideForTests = null;
            EnabledOverrideForTests = null;
            SetEnabledOverrideForTests = null;
            EventCountOverrideForTests = null;
            EventNameOverrideForTests = null;
            EventDataOverrideForTests = null;
            GenerationOverrideForTests = null;
            Interlocked.Increment(ref captureGeneration);
        }

        public static string ReadField(object value, string fieldName, UnityCliRequestBudget budget)
        {
            if (value == null) return null;
            var type = value.GetType();
            if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                try { var result = field.GetValue(value); return result == null ? null : Convert.ToString(result, CultureInfo.InvariantCulture); } catch { return null; }
            }
            if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var property = type.GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (property == null) return null;
            if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            try { var result = property.GetValue(value, null); return result == null ? null : Convert.ToString(result, CultureInfo.InvariantCulture); } catch { return null; }
        }
    }
}
#endif
