#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace UnityCli
{
    /// <summary>profiler.hierarchy — 使用 RawFrameDataView child-at API 做全局节点续页。</summary>
    public class ProfilerHierarchyTool : IUnityCliTool<ProfilerHierarchyTool.Request, ProfilerHierarchyTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRange(-1L, int.MaxValue)] public int? Frame { get; set; }
            [UnityCliRange(0L, 1024L)] public int? Thread { get; set; }
            [UnityCliRange(1L, 500L)] public int? PageSize { get; set; }
            [UnityCliRange(0L, 32L)] public int? Depth { get; set; }
            public string Cursor { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public long Frame { get; set; }
            public long Thread { get; set; }
            [UnityCliItemsRange(0, 500)] public List<HierarchyItem> Items { get; set; }
            public PageInfo PageInfo { get; set; }
            public UnityCliRequestBudgetUsage Budget { get; set; }
        }

        public sealed class HierarchyItem
        {
            public long Id { get; set; }
            public long ParentId { get; set; }
            public string Name { get; set; }
            public long Depth { get; set; }
            public double Ms { get; set; }
            public long ChildCount { get; set; }
            public bool ChildrenTruncated { get; set; }
        }

        public sealed class PageInfo
        {
            public long PageSize { get; set; }
            public long ReturnedCount { get; set; }
            public string Cursor { get; set; }
            public string NextCursor { get; set; }
            public bool Complete { get; set; }
            public bool Truncated { get; set; }
            public string Generation { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.hierarchy",
            Summary = "Page one profiler frame/thread call hierarchy with a global raw-node budget and stable continuation.",
            Description = "Read a flat preorder page from Unity RawFrameDataView without copying the full hierarchy. frame defaults to the latest recorded frame, thread is 0..1024, pageSize is 1..500 (default 50), and depth is 0..32 (default 5). The DFS stack is carried by opaque nextCursor, so later pages resume in O(depth). All nodes share request scan/node/reflection/time/UTF-8 budgets; defaults are 10000/4096/4096/250 ms/1 MiB and public maxima are 100000/20000/20000/2000 ms/8 MiB. complete=false identifies truncation. Read-only in Edit and Play Mode.",
            Group = "profiler", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Read latest-frame nodes", Input = new Request { PageSize = 50, Depth = 5 } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            request = request ?? new Request();
            var budget = ctx.UseReadBudget(request.Budget);
            if (!request.Frame.HasValue && (!budget.TryConsumeReflectionCall() || !budget.TryConsumeGetter())) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var frame = request.Frame ?? ProfilerToolSupport.ProfilerDriverIntProperty("lastFrameIndex", -1);
            var threadIndex = request.Thread ?? 0;
            var pageSize = request.PageSize ?? 50;
            var maxDepth = request.Depth ?? 5;
            var accessor = RawHierarchyAccessor.Open(frame, threadIndex, budget);
            try
            {
                var generation = UnityCliContinuationToken.Fingerprint(UnityCliReadGeneration.Session("profiler.hierarchy") + "\0" + accessor.CaptureIdentity);
                var stack = string.IsNullOrEmpty(request.Cursor)
                    ? new List<TraversalFrame> { new TraversalFrame { ParentId = 0, NextChild = 0, Depth = 0 } }
                    : ParseCursor(request.Cursor, generation, accessor, maxDepth, budget);
                var items = new List<HierarchyItem>();
                while (stack.Count > 0 && items.Count < pageSize)
                {
                    var before = EncodeStack(stack);
                    if (!TryNext(accessor, stack, maxDepth, budget, out var item))
                    {
                        if (budget.IsExhausted) stack = DecodeStack(before);
                        break;
                    }
                    items.Add(item);
                }
                if (items.Count == 0 && stack.Count > 0) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension ?? "scannedRecords");
                var nextCursor = stack.Count == 0 ? null : UnityCliContinuationToken.Create("profiler.hierarchy", generation, EncodeStack(stack));
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Frame = frame,
                    Thread = threadIndex,
                    Items = items,
                    PageInfo = new PageInfo
                    {
                        PageSize = pageSize, ReturnedCount = items.Count, Cursor = request.Cursor, NextCursor = nextCursor,
                        Complete = nextCursor == null, Truncated = nextCursor != null, Generation = generation
                    },
                    Budget = budget.Usage()
                });
            }
            finally { accessor.Dispose(); }
        }

        private static bool TryNext(RawHierarchyAccessor accessor, List<TraversalFrame> stack, int maxDepth, UnityCliRequestBudget budget, out HierarchyItem item)
        {
            item = null;
            while (stack.Count > 0)
            {
                var frame = stack[stack.Count - 1];
                var childCount = accessor.ChildCount(frame.ParentId, budget);
                if (budget.IsExhausted) return false;
                if (frame.NextChild >= childCount) { stack.RemoveAt(stack.Count - 1); continue; }
                if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeNode()) return false;
                var child = accessor.ChildAt(frame.ParentId, frame.NextChild, budget);
                if (budget.IsExhausted) return false;
                frame.NextChild++;
                var grandChildren = accessor.ChildCount(child, budget);
                if (budget.IsExhausted) return false;
                var name = accessor.Name(child, budget) ?? "(unknown)";
                var milliseconds = accessor.Milliseconds(child, budget);
                if (budget.IsExhausted || !budget.TryReserveSerializedUtf8(name)) return false;
                item = new HierarchyItem
                {
                    Id = child, ParentId = frame.ParentId, Name = name, Depth = frame.Depth,
                    Ms = milliseconds, ChildCount = grandChildren,
                    ChildrenTruncated = grandChildren > 0 && frame.Depth >= maxDepth
                };
                if (grandChildren > 0 && frame.Depth < maxDepth)
                    stack.Add(new TraversalFrame { ParentId = child, NextChild = 0, Depth = frame.Depth + 1 });
                return true;
            }
            return false;
        }

        private static List<TraversalFrame> ParseCursor(string cursor, string generation, RawHierarchyAccessor accessor, int maxDepth, UnityCliRequestBudget budget)
        {
            string position;
            if (!UnityCliContinuationToken.TryParse(cursor, "profiler.hierarchy", generation, out position)) throw Stale();
            try
            {
                var stack = DecodeStack(position);
                if (stack.Count == 0 || stack.Count > maxDepth + 1) throw Stale();
                for (var index = 0; index < stack.Count; index++)
                {
                    var frame = stack[index];
                    if (frame.Depth != index || frame.ParentId < 0 || frame.ParentId >= accessor.SampleCount || frame.NextChild < 0) throw Stale();
                    var childCount = accessor.ChildCount(frame.ParentId, budget);
                    if (frame.NextChild > childCount) throw Stale();
                }
                return stack;
            }
            catch (UnityCliRequestBudgetExceededException) { throw; }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch { throw Stale(); }
        }

        private static string EncodeStack(List<TraversalFrame> stack)
        {
            var values = new List<string>();
            foreach (var frame in stack) values.Add(frame.ParentId.ToString(CultureInfo.InvariantCulture) + "," + frame.NextChild.ToString(CultureInfo.InvariantCulture) + "," + frame.Depth.ToString(CultureInfo.InvariantCulture));
            return string.Join(";", values.ToArray());
        }

        private static List<TraversalFrame> DecodeStack(string value)
        {
            var result = new List<TraversalFrame>();
            if (string.IsNullOrEmpty(value)) return result;
            foreach (var token in value.Split(';'))
            {
                var parts = token.Split(',');
                if (parts.Length != 3) throw new FormatException();
                result.Add(new TraversalFrame
                {
                    ParentId = int.Parse(parts[0], CultureInfo.InvariantCulture),
                    NextChild = int.Parse(parts[1], CultureInfo.InvariantCulture),
                    Depth = int.Parse(parts[2], CultureInfo.InvariantCulture)
                });
            }
            if (result.Count > 33) throw new FormatException();
            return result;
        }

        private static UnityCliBridge.BridgeCommandException Stale()
        {
            return new UnityCliBridge.BridgeCommandException("E_CONTINUATION_STALE", "The profiler hierarchy continuation does not match the same frame/thread generation.", "Start a new profiler.hierarchy read without cursor.");
        }

        private sealed class TraversalFrame { internal int ParentId; internal int NextChild; internal int Depth; }

        internal sealed class RawHierarchyAccessor : IDisposable
        {
            private readonly object view;
            private readonly PropertyInfo validProperty;
            private readonly int sampleCount;
            private readonly MethodInfo childCountMethod;
            private readonly MethodInfo childAtMethod;
            private readonly MethodInfo nameMethod;
            private readonly MethodInfo millisecondsMethod;
            private readonly string captureIdentity;

            private RawHierarchyAccessor(object view, Type type, UnityCliRequestBudget budget)
            {
                this.view = view;
                validProperty = ReflectProperty(type, "valid", budget);
                var sampleCountProperty = ReflectProperty(type, "sampleCount", budget);
                var frameIndexProperty = ReflectProperty(type, "frameIndex", budget);
                var threadIndexProperty = ReflectProperty(type, "threadIndex", budget);
                var frameStartTimeProperty = ReflectProperty(type, "frameStartTimeMs", budget);
                var frameTimeProperty = ReflectProperty(type, "frameTimeMs", budget);
                childCountMethod = ReflectMethod(type, "GetSampleChildrenCount", new[] { typeof(int) }, budget);
                childAtMethod = ReflectMethod(type, "GetSampleChildAt", new[] { typeof(int), typeof(int) }, budget);
                nameMethod = ReflectMethod(type, "GetSampleName", new[] { typeof(int) }, budget);
                millisecondsMethod = ReflectMethod(type, "GetSampleTimeMs", new[] { typeof(int) }, budget);
                if (sampleCountProperty == null || frameIndexProperty == null || threadIndexProperty == null || frameStartTimeProperty == null || frameTimeProperty == null
                    || childCountMethod == null || childAtMethod == null || nameMethod == null || millisecondsMethod == null)
                    throw Unavailable("RawFrameDataView capture identity or child-at members are unavailable.");
                if (validProperty != null && !ReadBool(validProperty, view, budget)) throw NoData();
                var identityValues = new object[5];
                var identityProperties = new[] { frameIndexProperty, threadIndexProperty, frameStartTimeProperty, frameTimeProperty, sampleCountProperty };
                for (var index = 0; index < identityProperties.Length; index++)
                {
                    if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    identityValues[index] = identityProperties[index].GetValue(view, null);
                }
                sampleCount = Convert.ToInt32(identityValues[4], CultureInfo.InvariantCulture);
                if (sampleCount <= 0) throw NoData();
                captureIdentity = string.Join("\0", Array.ConvertAll(identityValues, value => Convert.ToString(value, CultureInfo.InvariantCulture)));
                if (!budget.TryReserveSerializedUtf8(captureIdentity)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            }

            internal int SampleCount { get { return sampleCount; } }
            internal string CaptureIdentity { get { return captureIdentity; } }

            internal static RawHierarchyAccessor Open(int frame, int thread, UnityCliRequestBudget budget)
            {
                var driver = UnityCliBridge.TypeSchemaGenerator.ResolveType("UnityEditorInternal.ProfilerDriver", budget);
                if (driver == null) throw Unavailable("ProfilerDriver is unavailable.");
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var method = driver.GetMethod("GetRawFrameDataView", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(int), typeof(int) }, null);
                if (method == null) throw Unavailable("ProfilerDriver.GetRawFrameDataView is unavailable.");
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                object view;
                try { view = method.Invoke(null, new object[] { frame, thread }); }
                catch (Exception exception) { throw Unavailable("Failed to open RawFrameDataView: " + exception.Message); }
                return InitializeOwnedView(view, value => new RawHierarchyAccessor(value, value.GetType(), budget));
            }

            /// <summary>raw view 初始化成功才转移 ownership；成员绑定失败必须立即释放原 view。</summary>
            internal static T InitializeOwnedView<T>(object view, Func<object, T> initialize)
            {
                if (view == null) throw NoData();
                if (initialize == null) throw new ArgumentNullException("initialize");
                try { return initialize(view); }
                catch
                {
                    (view as IDisposable)?.Dispose();
                    throw;
                }
            }

            internal int ChildCount(int sample, UnityCliRequestBudget budget) { return Convert.ToInt32(Invoke(childCountMethod, new object[] { sample }, budget), CultureInfo.InvariantCulture); }
            internal int ChildAt(int sample, int index, UnityCliRequestBudget budget) { return Convert.ToInt32(Invoke(childAtMethod, new object[] { sample, index }, budget), CultureInfo.InvariantCulture); }
            internal string Name(int sample, UnityCliRequestBudget budget) { return Invoke(nameMethod, new object[] { sample }, budget) as string; }
            internal double Milliseconds(int sample, UnityCliRequestBudget budget) { return Convert.ToDouble(Invoke(millisecondsMethod, new object[] { sample }, budget), CultureInfo.InvariantCulture); }

            public void Dispose() { (view as IDisposable)?.Dispose(); }

            private object Invoke(MethodInfo method, object[] args, UnityCliRequestBudget budget)
            {
                if (!budget.TryConsumeReflectionCall()) return null;
                try { return method.Invoke(view, args); }
                catch (TargetInvocationException exception)
                {
                    var inner = exception.InnerException ?? exception;
                    throw new UnityCliBridge.BridgeCommandException("E_PROFILER_HIERARCHY_READ_FAILED", "Raw profiler hierarchy read failed: " + inner.GetType().Name + ".", "Record a fresh frame and restart pagination.");
                }
                catch (Exception exception)
                {
                    throw new UnityCliBridge.BridgeCommandException("E_PROFILER_HIERARCHY_READ_FAILED", "Raw profiler hierarchy read failed: " + exception.GetType().Name + ".", "Record a fresh frame and restart pagination.");
                }
            }

            private static PropertyInfo ReflectProperty(Type type, string name, UnityCliRequestBudget budget)
            {
                if (!budget.TryConsumeReflectionCall()) return null;
                return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }

            private static MethodInfo ReflectMethod(Type type, string name, Type[] parameters, UnityCliRequestBudget budget)
            {
                if (!budget.TryConsumeReflectionCall()) return null;
                return type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null);
            }

            private static bool ReadBool(PropertyInfo property, object target, UnityCliRequestBudget budget)
            {
                if (!budget.TryConsumeGetter() || !budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                return Convert.ToBoolean(property.GetValue(target, null), CultureInfo.InvariantCulture);
            }

            private static UnityCliBridge.BridgeCommandException Unavailable(string message)
            {
                return new UnityCliBridge.BridgeCommandException("E_PROFILER_HIERARCHY_UNAVAILABLE", message, "Use an Editor exposing RawFrameDataView and record profiler frames before retrying.");
            }

            private static UnityCliBridge.BridgeCommandException NoData()
            {
                return new UnityCliBridge.BridgeCommandException("E_PROFILER_HIERARCHY_NO_DATA", "No raw hierarchy data is available for the requested frame/thread.", "Record profiler frames, then query a valid frame and thread.");
            }
        }
    }
}
#endif
