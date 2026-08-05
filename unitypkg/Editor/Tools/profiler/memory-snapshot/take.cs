#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// profiler.memory-snapshot.take — 采集内存快照(.snap)。
    /// MemoryProfiler.TakeSnapshot 的未来帧回调通过统一 wait scheduler 承接。
    /// </summary>
    public class ProfilerMemorySnapshotTakeTool : IUnityCliTool<ProfilerMemorySnapshotTakeTool.Request, ProfilerMemorySnapshotTakeTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
        }

        public sealed class Result
        {
            public string Path { get; set; }
            public long SizeBytes { get; set; }
            public List<ProfilerPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.memory-snapshot.take",
            Summary = "Capture a memory snapshot (.snap) via MemoryProfiler using the unified callback wait path.",
            Description = "Capture a memory snapshot (.snap) via MemoryProfiler. Requires package com.unity.memoryprofiler. The tool waits for TakeSnapshot's future-frame callback through the framework wait scheduler instead of a custom deferred interface.",
            Group = "profiler",DestructiveHint = true, SideEffects = new[] { "memory-snapshot-write" }, Target = "editor", PlayModeSupport = "supported",
            RequiresPackages = new[] { "com.unity.memoryprofiler" },
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Take a memory snapshot to a custom path", Input = new Request { Path = "Artifacts/snap1.snap" } } }
        };

        public async ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            var profilerType = ProfilerToolSupport.MemoryProfilerType();
            if (profilerType == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_PACKAGE_MISSING",
                    "Package com.unity.memoryprofiler is required for memory snapshots.",
                    "Install com.unity.memoryprofiler via the Package Manager.");
            }

            var outputPath = request.Path;
            if (string.IsNullOrEmpty(outputPath))
            {
                var dir = Path.Combine(Application.temporaryCachePath, "MemoryCaptures");
                Directory.CreateDirectory(dir);
                outputPath = Path.Combine(dir, "snapshot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".snap");
            }

            var callbackType = typeof(Action<string, bool>);
            var captureFlagsType = ProfilerToolSupport.FindType("Unity.Profiling.Memory.CaptureFlags")
                ?? ProfilerToolSupport.FindType("UnityEngine.Profiling.Memory.Experimental.CaptureFlags");
            MethodInfo takeMethod = null;
            object[] invokeArgs = null;
            if (captureFlagsType != null)
            {
                takeMethod = profilerType.GetMethod("TakeSnapshot", new[] { typeof(string), callbackType, captureFlagsType });
            }
            if (takeMethod != null)
            {
                invokeArgs = new object[] { outputPath, null, Enum.ToObject(captureFlagsType, 0) };
            }
            else
            {
                takeMethod = profilerType.GetMethod("TakeSnapshot", new[] { typeof(string), callbackType });
            }
            if (takeMethod == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_API_UNSUPPORTED",
                    "MemoryProfiler.TakeSnapshot API not found.",
                    "The installed com.unity.memoryprofiler version may be incompatible.");
            }

            ctx.CommitEffect();
            return await ctx.Wait.ForCallback<Result>((complete, fail) =>
            {
                var callback = new Action<string, bool>((capturedPath, captureResult) =>
                {
                    try
                    {
                        if (!captureResult)
                        {
                            fail(new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_FAILED",
                                "Snapshot capture reported failure for path: " + outputPath,
                                "Check Unity Console for Memory Profiler errors."));
                            return;
                        }

                        var finalPath = capturedPath ?? outputPath;
                        var info = new FileInfo(finalPath);
                        if (!info.Exists || info.Length <= 0)
                        {
                            fail(new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_ARTIFACT_MISSING",
                                "MemoryProfiler reported success without a non-empty snapshot artifact: " + finalPath,
                                "Check available disk space and the Unity Console, then retry the snapshot."));
                            return;
                        }
                        complete(new Result
                        {
                            Path = finalPath,
                            SizeBytes = info.Length,
                            PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.memory-snapshot.take", Path = finalPath } }
                        });
                    }
                    catch (Exception exception)
                    {
                        fail(exception);
                    }
                });

                if (invokeArgs != null)
                {
                    invokeArgs[1] = callback;
                }
                else
                {
                    invokeArgs = new object[] { outputPath, callback };
                }

                try
                {
                    takeMethod.Invoke(null, invokeArgs);
                }
                catch (TargetInvocationException exception)
                {
                    fail(exception.InnerException ?? exception);
                }
                catch (Exception exception)
                {
                    fail(new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_FAILED",
                        "Snapshot capture failed: " + exception.Message,
                        "Check Unity Console and the Memory Profiler package."));
                }
            }, TimeSpan.FromSeconds(30), ct);
        }
    }
}
#endif
