#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Reflection;

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

        public static bool Available => UtilityType != null && StateType != null && EventCountProp != null && EnabledProp != null && EnableMethod != null;

        public static void SetEnabled(bool value)
        {
            EnableMethod.Invoke(null, new object[] { value, 0 });
        }

        public static bool GetEnabled()
        {
            try
            {
                if (EnabledProp != null) return (bool)EnabledProp.GetValue(null, null);
                throw new MissingMemberException("Frame Debugger enabled readback is unavailable.");
            }
            catch (Exception exception) { throw new InvalidOperationException("Frame Debugger enabled getter failed.", exception); }
        }

        public static int GetEventCount(UnityCliRequestBudget budget)
        {
            if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            try { return Convert.ToInt32(EventCountProp.GetValue(null, null), CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public static string EventName(int index, UnityCliRequestBudget budget)
        {
            if (EventNameMethod == null) return null;
            if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            try { return EventNameMethod.Invoke(null, new object[] { index }) as string; }
            catch { return null; }
        }

        public static object EventData(int index, UnityCliRequestBudget budget)
        {
            if (GetEventDataMethod == null || EventDataType == null) return null;
            if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            try
            {
                var value = Activator.CreateInstance(EventDataType);
                var args = new object[] { index, value };
                var ok = GetEventDataMethod.Invoke(null, args);
                return ok is true ? args[1] : null;
            }
            catch { return null; }
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
