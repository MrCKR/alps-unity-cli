#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// scriptable-object.list-types — 列出当前 Editor domain 中可创建的 ScriptableObject 类型。只读。
    /// </summary>
    public class ScriptableObjectListTypesTool : IUnityCliTool<ScriptableObjectListTypesTool.Request, ScriptableObjectListTypesTool.Result>
    {
        public sealed class Request
        {
            public string NamePattern { get; set; }
            public string AssemblyPattern { get; set; }
            public bool IncludeAbstract { get; set; }
            public int PageSize { get; set; } = 50;
            public int Cursor { get; set; } = 0;
        }

        public sealed class Result
        {
            public long Count { get; set; }
            public List<ScriptableObjectTypeEntry> Items { get; set; }
            public PageInfo PageInfo { get; set; }
        }

        public sealed class PageInfo
        {
            public long PageSize { get; set; }
            public long Cursor { get; set; }
            public long? NextCursor { get; set; }
            public long Total { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scriptable-object.list-types",
            Summary = "List discoverable ScriptableObject types in the current Editor domain with pagination.",
            Description = "List ScriptableObject types available in the current Editor domain. Supports namePattern, assemblyPattern, includeAbstract, pageSize and cursor pagination. Use the returned type names with scriptable-object.create.",
            Group = "scriptable-object",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List ScriptableObject types", Input = new Request { } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var namePattern = request.NamePattern;
            var assemblyPattern = request.AssemblyPattern;
            var includeAbstract = request.IncludeAbstract;
            var pageSize = Math.Max(1, Math.Min(request.PageSize <= 0 ? 50 : request.PageSize, 200));
            var cursor = Math.Max(0, request.Cursor);

            var types = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.IsNullOrEmpty(assemblyPattern) && assembly.GetName().Name.IndexOf(assemblyPattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Type[] candidates;
                try { candidates = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException exception) { candidates = exception.Types; }
                catch { continue; }
                foreach (var candidate in candidates)
                {
                    if (candidate == null || candidate == typeof(ScriptableObject) || !typeof(ScriptableObject).IsAssignableFrom(candidate)) continue;
                    if (!includeAbstract && candidate.IsAbstract) continue;
                    if (!string.IsNullOrEmpty(namePattern) && candidate.FullName.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) < 0 && candidate.Name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    types.Add(candidate);
                }
            }
            types.Sort((left, right) => string.Compare(left.FullName, right.FullName, StringComparison.Ordinal));
            var total = types.Count;
            var end = Math.Min(total, cursor + pageSize);
            var items = new List<ScriptableObjectTypeEntry>();
            for (var i = cursor; i < end; i++)
            {
                items.Add(ScriptableObjectToolSupport.BuildTypeEntry(types[i]));
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Count = total,
                Items = items,
                PageInfo = new PageInfo
                {
                    PageSize = pageSize,
                    Cursor = cursor,
                    NextCursor = end < total ? (long?)end : null,
                    Total = total
                }
            });
        }
    }
}
#endif
