#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

namespace UnityCli
{
    internal static class FrameDebuggerReflection
    {
        public static readonly Type UtilityType = ProfilerToolSupport.FindType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility, UnityEditor")
            ?? ProfilerToolSupport.FindType("UnityEditorInternal.FrameDebuggerUtility, UnityEditor");
        public static readonly PropertyInfo EventCountProp = UtilityType?.GetProperty("count", BindingFlags.Public | BindingFlags.Static)
            ?? UtilityType?.GetProperty("eventsCount", BindingFlags.Public | BindingFlags.Static);
        public static readonly PropertyInfo EnabledProp = UtilityType?.GetProperty("enabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? UtilityType?.GetProperty("isEnabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? UtilityType?.GetProperty("isLocalEnabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        public static readonly MethodInfo EnabledMethod = UtilityType?.GetMethod("IsLocalEnabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null)
            ?? UtilityType?.GetMethod("GetEnabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
        public static readonly MethodInfo EnableMethod = UtilityType?.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool), typeof(int) }, null)
            ?? UtilityType?.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static);
        public static readonly MethodInfo GetEventsMethod = UtilityType?.GetMethod("GetFrameEvents", BindingFlags.Public | BindingFlags.Static);
        public static readonly MethodInfo EventNameMethod = UtilityType?.GetMethod("GetFrameEventInfoName", BindingFlags.Public | BindingFlags.Static);
        public static readonly Type EventDataType = ProfilerToolSupport.FindType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData, UnityEditor")
            ?? ProfilerToolSupport.FindType("UnityEditorInternal.FrameDebuggerEventData, UnityEditor");
        public static readonly MethodInfo GetEventDataMethod = EventDataType != null
            ? UtilityType?.GetMethod("GetFrameEventData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), EventDataType }, null)
            : UtilityType?.GetMethod("GetFrameEventData", BindingFlags.Public | BindingFlags.Static);

        public static bool Available => UtilityType != null && EventCountProp != null && (EnabledProp != null || EnabledMethod != null) && EnableMethod != null;

        // SetEnabled 有 (bool,int) 或 (bool) 两种重载,按参数个数分派
        public static void SetEnabled(bool value)
        {
            var paramCount = EnableMethod.GetParameters().Length;
            if (paramCount == 2) { EnableMethod.Invoke(null, new object[] { value, 0 }); }
            else if (paramCount == 1) { EnableMethod.Invoke(null, new object[] { value }); }
            else { throw new InvalidOperationException("SetEnabled has unexpected " + paramCount + " parameters."); }
        }

        public static bool GetEnabled()
        {
            try
            {
                if (EnabledProp != null) return (bool)EnabledProp.GetValue(null, null);
                if (EnabledMethod != null) return (bool)EnabledMethod.Invoke(null, null);
                throw new MissingMemberException("Frame Debugger enabled readback is unavailable.");
            }
            catch (Exception exception) { throw new InvalidOperationException("Frame Debugger enabled getter failed.", exception); }
        }

        public static int GetEventCount()
        {
            try { return (int)EventCountProp.GetValue(null); }
            catch { return 0; }
        }

        // 反射读取 Frame Debugger 事件对象的字段/属性值(字符串化);不可用返回 null
        public static string ReadField(object obj, string fieldName)
        {
            try
            {
                var type = obj.GetType();
                var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance)
                         ?? type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                var prop = type.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance)
                        ?? type.GetProperty(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
                object val = field != null ? field.GetValue(obj) : prop != null ? prop.GetValue(obj) : null;
                return val != null ? val.ToString() : null;
            }
            catch { return null; }
        }
    }
}
#endif
