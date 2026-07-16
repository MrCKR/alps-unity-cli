#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityCli
{
    public class ProfilerHierarchyTool : IUnityCliTool<ProfilerHierarchyTool.Request, ProfilerHierarchyTool.Result>
    {
        public sealed class Request
        {
            public int? Frame { get; set; }
            public int? Thread { get; set; }
            public int? Top { get; set; }
            public int? Depth { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public long Frame { get; set; }
            public long Thread { get; set; }
            public long Top { get; set; }
            public long Depth { get; set; }
            public List<HierarchyItem> RootItems { get; set; }
        }

        public sealed class HierarchyItem
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public long Depth { get; set; }
            public double Ms { get; set; }
            public long SiblingCount { get; set; }
            public long ChildCount { get; set; }
            public bool ChildrenTruncated { get; set; }
            public List<HierarchyItem> Children { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.hierarchy",
            Summary = "Read the per-function CPU call hierarchy (marker tree) for one profiler frame/thread.",
            Description = "Read the CPU call hierarchy for a single profiler frame and thread via ProfilerDriver.GetHierarchyFrameDataView, recursing GetItemChildren to emit a structured marker tree. Defaults are bounded (top=20, depth=5); each node reports childCount and childrenTruncated so AI can see when detail was omitted.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Latest frame main-thread top markers", Input = new Request { Top = 20, Depth = 5 } },
                new ToolExample { Description = "Latest frame on the main thread", Input = new Request { Frame = -1, Thread = 0 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var driverType = ProfilerToolSupport.FindType("UnityEditorInternal.ProfilerDriver");
            if (driverType == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_HIERARCHY_UNAVAILABLE",
                    "UnityEditorInternal.ProfilerDriver is not available; hierarchy requires the Editor Profiler.",
                    "Run this in the Unity Editor (not batchmode without profiler support).");
            }

            // 省略 frame 时读取最新已采集帧
            var frame = request.Frame ?? ProfilerToolSupport.ProfilerDriverIntProperty("lastFrameIndex", -1);
            var threadIndex = request.Thread ?? 0;
            var top = Math.Max(1, Math.Min(request.Top ?? 20, 5000));
            var maxDepth = Math.Max(1, Math.Min(request.Depth ?? 5, 32));

            var hierarchyViewType = ProfilerToolSupport.FindType("UnityEditor.Profiling.HierarchyFrameDataView")
                ?? ProfilerToolSupport.FindType("UnityEditorInternal.HierarchyFrameDataView")
                ?? ProfilerToolSupport.FindType("UnityEngine.Profiling.HierarchyFrameDataView");
            if (hierarchyViewType == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_HIERARCHY_UNAVAILABLE",
                    "HierarchyFrameDataView type not available in this Unity version.",
                    "Use an Editor version that exposes UnityEditor.Profiling.HierarchyFrameDataView, then record profiler frames before retrying.");
            }

            var viewModesType = hierarchyViewType.GetTypeInfo().GetDeclaredNestedType("ViewModes")?.AsType();
            var getHierarchy = viewModesType == null
                ? null
                : driverType.GetMethod("GetHierarchyFrameDataView", new[] { typeof(int), typeof(int), viewModesType, typeof(int), typeof(bool) });
            // 不同 Unity 版本签名不同;退回 (int,int) 最简重载
            if (getHierarchy == null)
            {
                getHierarchy = driverType.GetMethod("GetHierarchyFrameDataView", new[] { typeof(int), typeof(int) });
            }
            if (getHierarchy == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_HIERARCHY_UNAVAILABLE",
                    "ProfilerDriver.GetHierarchyFrameDataView overload not found.",
                    "Unity version may not expose this API via reflection.");
            }

            object view = null;
            try
            {
                view = getHierarchy.GetParameters().Length == 2
                    ? getHierarchy.Invoke(null, new object[] { frame, threadIndex })
                    : getHierarchy.Invoke(null, new object[] { frame, threadIndex, 0, 0, false });
            }
            catch (Exception exception)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_HIERARCHY_UNAVAILABLE",
                    "Failed to acquire hierarchy frame data: " + exception.Message,
                    HierarchyDetails(frame, threadIndex),
                    new[] { "Make sure the Profiler has recorded frames; pass profiler.start then profiler.stop, then query a recorded frame (or omit frame to use the latest)." });
            }
            if (view == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_HIERARCHY_NO_DATA",
                    "No hierarchy data available for the requested frame/thread.",
                    "Record profiler frames first (profiler.start → play → profiler.stop), then query a valid frame.");
            }

            var validProp = hierarchyViewType.GetProperty("valid");
            if (validProp != null && !(bool)validProp.GetValue(view, null))
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_HIERARCHY_NO_DATA",
                    "HierarchyFrameDataView is invalid for the requested frame/thread.",
                    "Record profiler frames first, then query a valid frame.");
            }

            var resolvedFrame = frame < 0 ? (int)hierarchyViewType.GetProperty("frameIndex").GetValue(view, null) : frame;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "profiler.hierarchy",
                Frame = resolvedFrame,
                Thread = threadIndex,
                Top = top,
                Depth = maxDepth,
                RootItems = BuildHierarchyItems(view, hierarchyViewType, 0, top, maxDepth, 0)
            });
        }

        // 递归构建调用树节点:按 top 限制每层子项数量、depth 限制最大深度,避免单帧树过大
        private static List<HierarchyItem> BuildHierarchyItems(object view, Type viewType, int itemId, int top, int maxDepth, int depth)
        {
            var result = new List<HierarchyItem>();
            if (depth > maxDepth) return result;

            var childrenMethod = viewType.GetMethod("GetItemChildren", new[] { typeof(int), typeof(List<int>) });
            var nameMethod = viewType.GetMethod("GetItemName", new[] { typeof(int) });
            var msMethod = viewType.GetMethod("GetItemThreadMS", new[] { typeof(int) });

            var childIds = new List<int>();
            try { if (childrenMethod != null) childrenMethod.Invoke(view, new object[] { itemId, childIds }); }
            catch { return result; }

            var emitted = 0;
            foreach (var childId in childIds)
            {
                if (emitted >= top) break;
                string name = null;
                double ms = 0;
                try { if (nameMethod != null) name = nameMethod.Invoke(view, new object[] { childId }) as string; } catch { }
                try { if (msMethod != null) { var v = msMethod.Invoke(view, new object[] { childId }); if (v is double d) ms = d; else if (v is float f) ms = f; } } catch { }

                var grandChildren = new List<int>();
                try { if (childrenMethod != null) childrenMethod.Invoke(view, new object[] { childId, grandChildren }); } catch { }

                var item = new HierarchyItem
                {
                    Id = childId,
                    Name = name ?? "(unknown)",
                    Depth = depth,
                    Ms = ms,
                    SiblingCount = childIds.Count,
                    ChildCount = grandChildren.Count,
                    ChildrenTruncated = grandChildren.Count > top || (grandChildren.Count > 0 && depth + 1 > maxDepth)
                };
                if (grandChildren.Count > 0 && depth + 1 <= maxDepth)
                {
                    item.Children = BuildHierarchyItems(view, viewType, childId, top, maxDepth, depth + 1);
                }
                result.Add(item);
                emitted++;
            }
            return result;
        }

        private static ProfilerHierarchyDetails HierarchyDetails(int frame, int threadIndex)
        {
            return new ProfilerHierarchyDetails
            {
                Command = "profiler.hierarchy",
                Frame = frame,
                Thread = threadIndex,
                ProfilerRecording = ProfilerToolSupport.GetProfilerSessionEnabled()
            };
        }
    }
}
#endif
