#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// component.list-types — 枚举当前项目可用的 Component 类型,支持 keyword 过滤与分页。
    /// 对齐旧 ComponentListTypes handler;只读。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ComponentListTypesTool : IUnityCliTool<ComponentListTypesTool.Request, ComponentListTypesTool.Result>
    {
        public sealed class Request
        {
            public string Keyword { get; set; }
            public string Search { get; set; }
            public int? PageSize { get; set; }
            public int? Cursor { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public long Count { get; set; }
            public List<ComponentTypeInfo> Types { get; set; }
            public GoPageInfo PageInfo { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "component.list-types",
            Summary = "Enumerate Component types available in the current project across loaded assemblies.",
            Description = "Enumerate Component types available in the current project across loaded assemblies. Use keyword plus pageSize/cursor to bound token usage before calling component.add/get/set/modify. Returns fullName/name/assembly/namespace and pageInfo.nextCursor.",
            Group = "component",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List Rigidbody types", Input = new Request { Keyword = "Rigidbody", PageSize = 20 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var keyword = request.Keyword ?? request.Search;
            var pageSize = Math.Max(1, Math.Min(request.PageSize ?? 100, 500));
            var cursor = Math.Max(0, request.Cursor ?? 0);
            var types = new List<Type>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] candidates;
                try { candidates = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException e) { candidates = e.Types.Where(t => t != null).ToArray(); }
                foreach (var t in candidates)
                {
                    if (t == null || !typeof(Component).IsAssignableFrom(t)) continue;
                    if (t.IsAbstract) continue;
                    if (!string.IsNullOrEmpty(keyword) && t.FullName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    types.Add(t);
                }
            }
            types.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
            var total = types.Count;
            var page = types.Skip(cursor).Take(pageSize).ToList();
            var nextCursor = cursor + page.Count < total ? (cursor + page.Count).ToString(CultureInfo.InvariantCulture) : null;
            var result = new Result
            {
                Command = "component.list-types",
                Count = total,
                Types = page.ConvertAll(t => new ComponentTypeInfo { FullName = t.FullName, Name = t.Name, Assembly = t.Assembly.GetName().Name, Namespace = t.Namespace }),
                PageInfo = new GoPageInfo { PageSize = pageSize, Cursor = cursor, NextCursor = nextCursor, Total = total }
            };
            return result;
        }
    }

    /// <summary>Component 类型信息摘要。</summary>
    public sealed class ComponentTypeInfo
    {
        public string FullName { get; set; }
        public string Name { get; set; }
        public string Assembly { get; set; }
        public string Namespace { get; set; }
    }
}
#endif
