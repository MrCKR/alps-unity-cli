#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// Register context.* internal bridge commands.
        ///
        /// Context commands are read-only observations of Unity project and editor state.
        /// They are always allowed in Play Mode because they never mutate state.
        /// If a capability has side effects, it must be a Tool, not a Context.
        ///
        /// Built-in context registry with context definitions and read handlers.
        /// Registration is handled by ProjectContextRegistry which unifies built-in
        /// and project custom context commands into a single handler.
        /// </summary>

        // ── Built-in context definition ──

        internal sealed class BuiltInContextDefinition
        {
            public string Name;
            public string Summary;
            public string Description;
            public string Category;
            public string Source;
            public string RefreshBehavior;
            public string Target;
            public string AvailabilityScope;
            public string Handler;
            public string InputSchema;
            public string OutputSchema;
            public string[] RequiresPackages;
            public Func<BridgeRequest, string> ReadHandler;
        }

        private static readonly BuiltInContextDefinition[] BuiltInContexts = BuildBuiltInContextTable();

        private static BuiltInContextDefinition[] BuildBuiltInContextTable()
        {
            return Array.Empty<BuiltInContextDefinition>();
        }

        // ── Built-in context lookup (used by ProjectContextRegistry) ──

        internal static BuiltInContextDefinition FindBuiltInContext(string name)
        {
            if (InterfaceBuiltInContexts.ContainsKey(name))
            {
                return InterfaceBuiltInContexts[name];
            }
            foreach (var ctx in BuiltInContexts)
            {
                if (string.Equals(ctx.Name, name, StringComparison.Ordinal))
                {
                    return ctx;
                }
            }
            return null;
        }

        private static string ContextDescriptorJson(BuiltInContextDefinition ctx)
        {
            var w = new UnityCliJsonWriter().BeginObject()
                .Prop("name", ctx.Name)
                .Prop("summary", string.IsNullOrEmpty(ctx.Summary) ? ctx.Description : ctx.Summary)
                .Prop("description", ctx.Description)
                .Prop("source", ctx.Source)
                .Prop("category", ctx.Category)
                .Prop("refreshBehavior", ctx.RefreshBehavior)
                .Prop("target", ctx.Target)
                .Prop("availabilityScope", ctx.AvailabilityScope)
                .Prop("playModeSupport", "supported")
                .Prop("supportStatus", string.Equals(ctx.Handler, "offline", StringComparison.Ordinal) ? "cli-only" : "live-bridge");
            w.PropRaw("inputSchema", ctx.InputSchema ?? "{}")
                .PropRaw("outputSchema", ctx.OutputSchema ?? "{}")
                .Prop("cliCommand", "context read " + ctx.Name);
            if (ctx.RequiresPackages != null && ctx.RequiresPackages.Length > 0)
            {
                w.PropArray("requiresPackages", arr => { foreach (var pkg in ctx.RequiresPackages) { arr.Value(pkg); } });
            }
            w.EndObject();
            return w.ToString();
        }

        // ── Helper methods ──

        // 统计顶层 JSON 数组元素数量，用于复用已有数组序列化 helper 后补稳定 count 字段。
        private static int CountTopLevelJsonArrayItems(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return 0;
            var trimmed = json.Trim();
            if (trimmed == "[]") return 0;
            if (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal)) return 0;
            var depth = 0;
            var count = 0;
            var inString = false;
            var escaped = false;
            var hasValue = false;
            for (var i = 1; i < trimmed.Length - 1; i++)
            {
                var ch = trimmed[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; hasValue = true; continue; }
                if (ch == '{' || ch == '[') { depth++; hasValue = true; continue; }
                if (ch == '}' || ch == ']') { depth--; continue; }
                if (depth == 0 && ch == ',') { count++; continue; }
                if (!char.IsWhiteSpace(ch)) hasValue = true;
            }
            return hasValue ? count + 1 : 0;
        }

        private static string ExtractJsonProperty(string json, string name)
        {
            var marker = "\"" + name + "\":";
            var index = (json ?? string.Empty).IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) return null;
            var start = index + marker.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var i = start; i < json.Length; i++)
            {
                var ch = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; continue; }
                if (ch == '{' || ch == '[') depth++;
                if (ch == '}' || ch == ']')
                {
                    depth--;
                    if (depth == 0) return json.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        private static string StringMemberValue(Type type, object instance, string memberName)
        {
            var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                try { return prop.GetValue(instance, null) as string; } catch { return null; }
            }
            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { return field.GetValue(instance) as string; } catch { return null; }
            }
            return null;
        }
    }
}
#endif
