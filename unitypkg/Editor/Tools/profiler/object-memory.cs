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
    public class ProfilerObjectMemoryTool : IUnityCliTool<ProfilerObjectMemoryTool.Request, ProfilerObjectMemoryTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Path { get; set; } }
        public sealed class Result { public string Command { get; set; } public string Target { get; set; } public List<ObjectMemoryEntry> Objects { get; set; } public long TotalBytes { get; set; } }
        public sealed class ObjectMemoryEntry { public string Name { get; set; } public long InstanceId { get; set; } public long SizeBytes { get; set; } }

        public ToolDescriptor Describe() => new()
        {
            Name = "profiler.object-memory", Summary = "Query memory size of components on a GameObject via Profiler.GetRuntimeMemorySizeLong.",
            Description = "Query runtime memory size of components on a GameObject via Profiler.GetRuntimeMemorySizeLong. target resolves by name/path/id.",
            Group = "profiler", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Query object memory", Input = new Request { Target = "Player" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var target = request.Target ?? request.Path;
            var go = string.IsNullOrEmpty(target) ? null : UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(target);
            var result = new Result { Command = "profiler.object-memory", Target = target, Objects = new List<ObjectMemoryEntry>() };
            if (go != null)
            {
                long total = 0;
                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var size = Profiler.GetRuntimeMemorySizeLong(c);
                    total += size;
                    result.Objects.Add(new ObjectMemoryEntry { Name = c.GetType().Name, InstanceId = c.GetInstanceID(), SizeBytes = size });
                }
                result.TotalBytes = total;
            }
            return new System.Threading.Tasks.ValueTask<Result>(result);
        }
    }
}
#endif
