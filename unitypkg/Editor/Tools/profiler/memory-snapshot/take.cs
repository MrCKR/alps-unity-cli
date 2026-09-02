#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace UnityCli
{
    /// <summary>profiler.memory-snapshot.take — governed pending publication 与唯一 callback terminal。</summary>
    public class ProfilerMemorySnapshotTakeTool : IUnityCliTool<ProfilerMemorySnapshotTakeTool.Request, ProfilerMemorySnapshotTakeTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            [UnityCliRange(1048576L, ProfilerMemorySnapshotSupport.AbsoluteSnapshotBytes)] public long? MaxBytes { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public string Path { get; set; }
            public long SizeBytes { get; set; }
            public long RetainedCountLimit { get; set; }
            public long RetainedBytesLimit { get; set; }
            public List<ProfilerPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.memory-snapshot.take",
            Summary = "Capture one governed Memory Profiler snapshot with bounded retention and one callback terminal.",
            Description = "Capture a .snap artifact only under Library/UnityCli/memory-snapshots. path is a file name, not a directory. maxBytes defaults to and cannot exceed 4 GiB. Successful publication keeps the newest 100 snapshots while enforcing an 8 GiB directory ceiling. A pending same-root file is atomically moved only after a successful non-empty callback; timeout, cancellation, domain reload, late callback and write faults share one terminal and clean pending/orphan files. Requires com.unity.memoryprofiler. Available in Edit and Play Mode; it writes a project-local Library artifact but no Assets content.",
            Group = "profiler", DestructiveHint = true, SideEffects = new[] { "memory-snapshot-write" }, Target = "editor", PlayModeSupport = "supported",
            RequiresPackages = new[] { "com.unity.memoryprofiler" }, DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Take a governed snapshot", Input = new Request { Path = "capture.snap", MaxBytes = 1073741824L } } }
        };

        public async ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            request = request ?? new Request();
            var profilerType = ProfilerToolSupport.MemoryProfilerType();
            if (profilerType == null) throw new UnityCliBridge.BridgeCommandException("E_PROFILER_PACKAGE_MISSING", "Package com.unity.memoryprofiler is required for memory snapshots.", "Install com.unity.memoryprofiler through Package Manager.");
            var finalPath = ProfilerMemorySnapshotSupport.ResolveFinalPath(request.Path, ctx.RequestId);
            var pendingPath = ProfilerMemorySnapshotSupport.PendingPath(ctx.RequestId);
            if (File.Exists(finalPath)) throw new UnityCliBridge.BridgeCommandException("E_OUTPUT_EXISTS", "Snapshot output already exists: " + finalPath, "Choose another .snap file name.");
            var maxBytes = request.MaxBytes ?? ProfilerMemorySnapshotSupport.AbsoluteSnapshotBytes;
            var binding = ResolveTakeSnapshot(profilerType, pendingPath);
            var requestBudget = ctx.UseReadBudget(request.Budget);
            var terminal = new MemorySnapshotTerminal();

            ctx.CommitEffect();
            ProfilerMemorySnapshotSupport.PrepareRoot();
            try
            {
                var result = await ctx.Wait.ForCallback<Result>((complete, fail) =>
                {
                    var callback = new Action<string, bool>((capturedPath, succeeded) =>
                    {
                        if (!terminal.TryOwnCallback())
                        {
                            ProfilerMemorySnapshotSupport.DeleteNoThrow(capturedPath ?? pendingPath);
                            ProfilerMemorySnapshotSupport.DeleteNoThrow(pendingPath);
                            return;
                        }
                        try
                        {
                            requestBudget.ResumeAfterWait();
                            requestBudget.CheckpointOrThrow();
                            if (!succeeded) throw ProfilerMemorySnapshotSupport.Error("E_PROFILER_SNAPSHOT_FAILED", "MemoryProfiler reported snapshot failure.", "Inspect Unity Console before retrying.");
                            var info = ProfilerMemorySnapshotSupport.Publish(capturedPath, pendingPath, finalPath);
                            if (info.Length > maxBytes)
                            {
                                ProfilerMemorySnapshotSupport.DeleteNoThrow(finalPath);
                                throw ProfilerMemorySnapshotSupport.Error("E_PROFILER_SNAPSHOT_LIMIT_EXCEEDED", "Snapshot exceeds the requested maxBytes ceiling.", "Increase maxBytes within 4 GiB or reduce captured state.");
                            }
                            complete(new Result
                            {
                                Path = finalPath,
                                SizeBytes = info.Length,
                                RetainedCountLimit = ProfilerMemorySnapshotSupport.RetainedSnapshotCount,
                                RetainedBytesLimit = ProfilerMemorySnapshotSupport.RetainedSnapshotBytes,
                                PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.memory-snapshot.take", Path = finalPath } }
                            });
                        }
                        catch (Exception exception)
                        {
                            ProfilerMemorySnapshotSupport.DeleteNoThrow(pendingPath);
                            fail(exception);
                        }
                    });
                    binding.SetCallback(callback);
                    try { binding.Invoke(); }
                    catch (TargetInvocationException exception)
                    {
                        if (terminal.TryOwnCallback()) fail(exception.InnerException ?? exception);
                        ProfilerMemorySnapshotSupport.DeleteNoThrow(pendingPath);
                    }
                    catch (Exception exception)
                    {
                        if (terminal.TryOwnCallback()) fail(ProfilerMemorySnapshotSupport.Error("E_PROFILER_SNAPSHOT_FAILED", "Snapshot capture failed: " + exception.Message, "Inspect Unity Console and the Memory Profiler package."));
                        ProfilerMemorySnapshotSupport.DeleteNoThrow(pendingPath);
                    }
                }, TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)), ct);
                requestBudget.ResumeAfterWait();
                return result;
            }
            catch
            {
                terminal.TryAbandon();
                ProfilerMemorySnapshotSupport.DeleteNoThrow(pendingPath);
                throw;
            }
        }

        private static SnapshotBinding ResolveTakeSnapshot(Type profilerType, string pendingPath)
        {
            var callbackType = typeof(Action<string, bool>);
            var captureFlagsType = ProfilerToolSupport.FindType("Unity.Profiling.Memory.CaptureFlags")
                ?? ProfilerToolSupport.FindType("UnityEngine.Profiling.Memory.Experimental.CaptureFlags");
            MethodInfo method = null;
            object[] args = null;
            if (captureFlagsType != null) method = profilerType.GetMethod("TakeSnapshot", new[] { typeof(string), callbackType, captureFlagsType });
            if (method != null) args = new object[] { pendingPath, null, Enum.ToObject(captureFlagsType, 0) };
            else
            {
                method = profilerType.GetMethod("TakeSnapshot", new[] { typeof(string), callbackType });
                if (method != null) args = new object[] { pendingPath, null };
            }
            if (method == null) throw new UnityCliBridge.BridgeCommandException("E_PROFILER_API_UNSUPPORTED", "MemoryProfiler.TakeSnapshot API is unavailable.", "Install a supported com.unity.memoryprofiler version.");
            return new SnapshotBinding(method, args);
        }

        private sealed class SnapshotBinding
        {
            private readonly MethodInfo method;
            private readonly object[] args;
            internal SnapshotBinding(MethodInfo method, object[] args) { this.method = method; this.args = args; }
            internal void SetCallback(Action<string, bool> callback) { args[1] = callback; }
            internal void Invoke() { method.Invoke(null, args); }
        }
    }
}
#endif
