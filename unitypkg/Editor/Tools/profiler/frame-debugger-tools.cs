#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// profiler.frame-debugger.enable/disable/events — Frame Debugger 操作工具。
    /// 迁移自旧 RegisterCommand(ProfilerCommands.ProfilerFrameDebugger*)为 IUnityCliTool 自包含。
    /// 反射 UnityEditorInternal.FrameDebuggerUtility;事件字段(type/shaderName/vertexCount 等)动态反射读取。
    /// </summary>
    public class ProfilerFrameDebuggerEnableTool : IUnityCliTool<ProfilerFrameDebuggerEnableTool.Request, ProfilerFrameDebuggerEnableTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public string Command { get; set; } public bool Enabled { get; set; } public long EventCount { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.frame-debugger.enable",
            Summary = "Enable the Unity Frame Debugger to capture draw call events.",
            Description = "Enable the Unity Frame Debugger to capture draw call events for the current Editor session. In Play Mode the game must be paused first with editor.pause; this opens the Frame Debugger window and returns the captured event count.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Enable Frame Debugger", Input = new Request { } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!FrameDebuggerReflection.Available)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_UNAVAILABLE",
                    "FrameDebuggerUtility not found via reflection.",
                    "Run in a Unity Editor with Frame Debugger support.");
            }
            // 打开 Frame Debugger 窗口(事件采集所需)
            EditorApplication.ExecuteMenuItem("Window/Analysis/Frame Debugger");
            // Play Mode 下必须暂停才能采集事件
            if (EditorApplication.isPlaying && !EditorApplication.isPaused)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_REQUIRES_PAUSE",
                    "Game must be paused before enabling Frame Debugger.",
                    "Pause the editor (editor.pause) first, then retry profiler.frame-debugger.enable.");
            }
            try { FrameDebuggerReflection.SetEnabled(true); }
            catch (Exception ex)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_FAILED",
                    "Failed to enable Frame Debugger: " + ex.Message,
                    "Check Unity Console.");
            }
            return new Result
            {
                Command = "profiler.frame-debugger.enable",
                Enabled = true,
                EventCount = FrameDebuggerReflection.GetEventCount()
            };
        }
    }

    public class ProfilerFrameDebuggerDisableTool : IUnityCliTool<ProfilerFrameDebuggerDisableTool.Request, ProfilerFrameDebuggerDisableTool.Result>
    {
        public sealed class Request { }
        public sealed class Result { public string Command { get; set; } public bool Enabled { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.frame-debugger.disable",
            Summary = "Disable the Unity Frame Debugger.",
            Description = "Disable the Unity Frame Debugger for the current Editor session. Use this after profiler.frame-debugger.events when draw-call inspection is complete; it returns enabled=false without modifying project assets.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Disable Frame Debugger", Input = new Request { } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!FrameDebuggerReflection.Available)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_UNAVAILABLE",
                    "FrameDebuggerUtility not found via reflection.",
                    "Run in a Unity Editor with Frame Debugger support.");
            }
            try { FrameDebuggerReflection.SetEnabled(false); }
            catch (Exception ex)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_FAILED",
                    "Failed to disable Frame Debugger: " + ex.Message,
                    "Check Unity Console.");
            }
            return new Result { Command = "profiler.frame-debugger.disable", Enabled = false };
        }
    }

    public class ProfilerFrameDebuggerEventsTool : IUnityCliTool<ProfilerFrameDebuggerEventsTool.Request, ProfilerFrameDebuggerEventsTool.Result>
    {
        public sealed class Request { public int? PageSize { get; set; } public int? Cursor { get; set; } }

        public sealed class Result
        {
            public string Command { get; set; }
            public long TotalEvents { get; set; }
            public long PageSize { get; set; }
            public long Cursor { get; set; }
            public List<FrameDebuggerEvent> Events { get; set; }
            public string Note { get; set; }
            public long? NextCursor { get; set; }
        }

        // 单个 draw call 事件:字段按 FrameDebuggerUtility 反射可用性填充(不可用字段省略)
        public sealed class FrameDebuggerEvent
        {
            public long Index { get; set; }
            public string Name { get; set; }
            public string EventType { get; set; }
            public string GameObjectInstanceID { get; set; }
            public string ShaderName { get; set; }
            public string PassName { get; set; }
            public string VertexCount { get; set; }
            public string IndexCount { get; set; }
            public string InstanceCount { get; set; }
            public string MeshName { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.frame-debugger.events",
            Summary = "Read draw call events captured by the Frame Debugger, paginated.",
            Description = "Read draw call events captured by the Frame Debugger, paginated. Returns shaderName, vertexCount, indexCount and similar fields per event when available.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read first page of events", Input = new Request { PageSize = 50, Cursor = 0 } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!FrameDebuggerReflection.Available)
            {
                throw new UnityCliBridge.BridgeCommandException("E_FRAME_DEBUGGER_UNAVAILABLE",
                    "FrameDebuggerUtility not found via reflection.",
                    "Run in a Unity Editor with Frame Debugger support.");
            }
            var pageSize = request.PageSize ?? 50;
            var cursor = request.Cursor ?? 0;
            var totalEvents = FrameDebuggerReflection.GetEventCount();

            if (totalEvents == 0)
            {
                return new Result
                {
                    Command = "profiler.frame-debugger.events",
                    TotalEvents = 0,
                    PageSize = pageSize,
                    Cursor = cursor,
                    Events = new List<FrameDebuggerEvent>(),
                    Note = "Frame Debugger has no events. Is it enabled?"
                };
            }

            // 反射读取事件描述数组(含 type/name 信息)
            object[] frameEvents = null;
            if (FrameDebuggerReflection.GetEventsMethod != null)
            {
                try
                {
                    var raw = FrameDebuggerReflection.GetEventsMethod.Invoke(null, null);
                    if (raw is Array arr) { frameEvents = new object[arr.Length]; arr.CopyTo(frameEvents, 0); }
                }
                catch { /* 忽略,逐个读取 */ }
            }

            var end = Math.Min(cursor + pageSize, totalEvents);
            var events = new List<FrameDebuggerEvent>();
            for (int i = cursor; i < end; i++)
            {
                var entry = new FrameDebuggerEvent { Index = i };
                if (FrameDebuggerReflection.EventNameMethod != null)
                {
                    try { entry.Name = (string)FrameDebuggerReflection.EventNameMethod.Invoke(null, new object[] { i }); }
                    catch { /* 跳过 name */ }
                }
                if (frameEvents != null && i < frameEvents.Length)
                {
                    entry.EventType = FrameDebuggerReflection.ReadField(frameEvents[i], "type");
                    entry.GameObjectInstanceID = FrameDebuggerReflection.ReadField(frameEvents[i], "gameObjectInstanceID");
                }
                if (FrameDebuggerReflection.GetEventDataMethod != null)
                {
                    try
                    {
                        var paramInfos = FrameDebuggerReflection.GetEventDataMethod.GetParameters();
                        object eventData;
                        if (paramInfos.Length == 2 && FrameDebuggerReflection.EventDataType != null)
                        {
                            eventData = Activator.CreateInstance(FrameDebuggerReflection.EventDataType);
                            var args = new object[] { i, eventData };
                            var ok = FrameDebuggerReflection.GetEventDataMethod.Invoke(null, args);
                            eventData = (ok is true) ? args[1] : null;
                        }
                        else
                        {
                            eventData = FrameDebuggerReflection.GetEventDataMethod.Invoke(null, new object[] { i });
                        }
                        if (eventData != null)
                        {
                            entry.ShaderName = FrameDebuggerReflection.ReadField(eventData, "shaderName");
                            entry.PassName = FrameDebuggerReflection.ReadField(eventData, "passName");
                            entry.VertexCount = FrameDebuggerReflection.ReadField(eventData, "vertexCount");
                            entry.IndexCount = FrameDebuggerReflection.ReadField(eventData, "indexCount");
                            entry.InstanceCount = FrameDebuggerReflection.ReadField(eventData, "instanceCount");
                            entry.MeshName = FrameDebuggerReflection.ReadField(eventData, "meshName");
                        }
                    }
                    catch { /* 跳过该事件详细数据 */ }
                }
                events.Add(entry);
            }

            return new Result
            {
                Command = "profiler.frame-debugger.events",
                TotalEvents = totalEvents,
                PageSize = pageSize,
                Cursor = cursor,
                Events = events,
                NextCursor = end < totalEvents ? (long?)end : null
            };
        }
    }

    /// <summary>
    /// Frame Debugger 反射缓存(helper):缓存 FrameDebuggerUtility 的类型/方法/属性,
    /// 供 enable/disable/events 三个工具复用。仅本文件内使用。
    /// </summary>
    internal static class FrameDebuggerReflection
    {
        public static readonly Type UtilityType = ProfilerToolSupport.FindType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility, UnityEditor")
            ?? ProfilerToolSupport.FindType("UnityEditorInternal.FrameDebuggerUtility, UnityEditor");
        public static readonly PropertyInfo EventCountProp = UtilityType?.GetProperty("count", BindingFlags.Public | BindingFlags.Static)
            ?? UtilityType?.GetProperty("eventsCount", BindingFlags.Public | BindingFlags.Static);
        public static readonly MethodInfo EnableMethod = UtilityType?.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool), typeof(int) }, null)
            ?? UtilityType?.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static);
        public static readonly MethodInfo GetEventsMethod = UtilityType?.GetMethod("GetFrameEvents", BindingFlags.Public | BindingFlags.Static);
        public static readonly MethodInfo EventNameMethod = UtilityType?.GetMethod("GetFrameEventInfoName", BindingFlags.Public | BindingFlags.Static);
        public static readonly Type EventDataType = ProfilerToolSupport.FindType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData, UnityEditor")
            ?? ProfilerToolSupport.FindType("UnityEditorInternal.FrameDebuggerEventData, UnityEditor");
        public static readonly MethodInfo GetEventDataMethod = EventDataType != null
            ? UtilityType?.GetMethod("GetFrameEventData", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), EventDataType }, null)
            : UtilityType?.GetMethod("GetFrameEventData", BindingFlags.Public | BindingFlags.Static);

        public static bool Available => UtilityType != null && EventCountProp != null && EnableMethod != null;

        // SetEnabled 有 (bool,int) 或 (bool) 两种重载,按参数个数分派
        public static void SetEnabled(bool value)
        {
            var paramCount = EnableMethod.GetParameters().Length;
            if (paramCount == 2) { EnableMethod.Invoke(null, new object[] { value, 0 }); }
            else if (paramCount == 1) { EnableMethod.Invoke(null, new object[] { value }); }
            else { throw new InvalidOperationException("SetEnabled has unexpected " + paramCount + " parameters."); }
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
