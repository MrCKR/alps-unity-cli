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
    public class ProfilerFrameDebuggerEventsTool : IUnityCliTool<ProfilerFrameDebuggerEventsTool.Request, ProfilerFrameDebuggerEventsTool.Result>
    {
        public sealed class Request { public int? PageSize { get; set; } public int? Cursor { get; set; } }

        public sealed class Result
        {
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
            Group = "profiler",DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read first page of events", Input = new Request { PageSize = 50, Cursor = 0 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
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
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    TotalEvents = 0,
                    PageSize = pageSize,
                    Cursor = cursor,
                    Events = new List<FrameDebuggerEvent>(),
                    Note = "Frame Debugger has no events. Is it enabled?"
                });
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

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                TotalEvents = totalEvents,
                PageSize = pageSize,
                Cursor = cursor,
                Events = events,
                NextCursor = end < totalEvents ? (long?)end : null
            });
        }
    }
}
#endif
