#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// profiler.memory-snapshot.take — 采集内存快照(.snap)。
    /// 实现 IUnityCliDeferredTool:MemoryProfiler.TakeSnapshot 回调在未来帧触发,TryDefer 总是挂起 deferred。
    /// 需要 com.unity.memoryprofiler 包,缺包抛 E_PROFILER_PACKAGE_MISSING。
    /// 迁移自旧 RegisterCommand(ProfilerCommands.ProfilerMemorySnapshotTake)。
    /// </summary>
    public class ProfilerMemorySnapshotTakeTool : IUnityCliDeferredTool<ProfilerMemorySnapshotTakeTool.Request, ProfilerMemorySnapshotTakeTool.Result>
    {
        /// <summary>快照参数。path 可选,默认临时目录。</summary>
        public sealed class Request
        {
            /// <summary>输出 .snap 路径。默认 &lt;tempCachePath&gt;/MemoryCaptures/snapshot_&lt;timestamp&gt;.snap。</summary>
            public string Path { get; set; }
        }

        /// <summary>快照结果:最终路径、字节数、计划副作用。</summary>
        public sealed class Result
        {
            public string Command { get; set; }
            public string Path { get; set; }
            public long SizeBytes { get; set; }
            public List<ProfilerPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.memory-snapshot.take",
            Summary = "Capture a memory snapshot (.snap) via MemoryProfiler (deferred callback wait).",
            Description = "Capture a memory snapshot (.snap) via MemoryProfiler. Requires package com.unity.memoryprofiler. TakeSnapshot's callback fires on a future frame, so capture is deferred via non-blocking frame polling and may take several seconds.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            RequiresPackages = new[] { "com.unity.memoryprofiler" },
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Take a memory snapshot to a custom path", Input = new Request { Path = "Artifacts/snap1.snap" } } }
        };

        /// <summary>
        /// 总是挂起为 deferred:校验包 → 解析路径 → 反射调 TakeSnapshot(注册回调)→ 返回 completion。
        /// completion.IsComplete 轮询回调标志;OnComplete 校验结果并构造 Result。
        /// 缺包抛 E_PROFILER_PACKAGE_MISSING(由框架捕获转结构化错误)。
        /// </summary>
        public bool TryDefer(Request request, UnityCliProjectToolContext ctx, out DeferredCompletion<Result> completion)
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

            // 查找 TakeSnapshot 重载:优先 3 参(string, Action<string,bool>, CaptureFlags),回退 2 参
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

            // 闭包状态:回调在未来帧设置 flag + 路径/结果
            string capturedPath = null;
            bool captureResult = false;
            bool callbackFired = false;
            Action<string, bool> callback = (path, result) =>
            {
                capturedPath = path;
                captureResult = result;
                callbackFired = true;
            };
            if (invokeArgs != null) { invokeArgs[1] = callback; }
            else { invokeArgs = new object[] { outputPath, callback }; }
            try { takeMethod.Invoke(null, invokeArgs); }
            catch (Exception ex)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_FAILED",
                    "Snapshot capture failed: " + ex.Message,
                    "Check Unity Console and the Memory Profiler package.");
            }

            completion = new DeferredCompletion<Result>
            {
                TimeoutMs = 30000,
                IsComplete = () => callbackFired,
                OnComplete = () =>
                {
                    if (!captureResult)
                    {
                        throw new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_FAILED",
                            "Snapshot capture reported failure for path: " + outputPath,
                            "Check Unity Console for Memory Profiler errors.");
                    }
                    var finalPath = capturedPath ?? outputPath;
                    var fi = new FileInfo(finalPath);
                    return new Result
                    {
                        Command = "profiler.memory-snapshot.take",
                        Path = finalPath,
                        SizeBytes = fi.Exists ? fi.Length : 0L,
                        PlannedEffects = new List<ProfilerPlannedEffect> { new ProfilerPlannedEffect { Type = "profiler.memory-snapshot.take", Path = finalPath } }
                    };
                },
                TimeoutErrorCode = "E_PROFILER_SNAPSHOT_TIMEOUT",
                TimeoutErrorMessage = "Memory snapshot timed out after 30 seconds.",
                TimeoutNextAction = "Reduce scene complexity or retry."
            };
            return true;
        }

        /// <summary>同步路径:memory-snapshot.take 总是 deferred,Execute 不会被调用。保留以满足接口契约。</summary>
        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            throw new UnityCliBridge.BridgeCommandException("E_PROFILER_SNAPSHOT_FAILED",
                "profiler.memory-snapshot.take is always deferred; Execute should not be reached.",
                "This is an internal error; report it to the bridge maintainer.");
        }
    }
}
#endif
