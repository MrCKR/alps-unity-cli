#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private const int InlineImageMaxBytes = 512 * 1024;

        /// <summary>解析统一分页大小，避免大列表默认返回过多条目。</summary>
        private static int LargeOutputPageSize(BridgeRequest request, int fallback, int max)
        {
            return Math.Max(1, Math.Min(request.IntArg("pageSize", fallback), max));
        }

        /// <summary>解析统一分页游标，负数游标按 0 处理。</summary>
        private static int LargeOutputCursor(BridgeRequest request)
        {
            return Math.Max(0, request.IntArg("cursor", 0));
        }

        /// <summary>生成统一 pageInfo，同时保留旧 total/truncated 字段语义以降低兼容风险。</summary>
        private static string PageInfoJson(int pageSize, int cursor, int totalCount, int returnedCount)
        {
            var next = cursor + returnedCount < totalCount ? (cursor + returnedCount).ToString(CultureInfo.InvariantCulture) : null;
            return new UnityCliJsonWriter().BeginObject()
                .Prop("total", (long)totalCount)
                .Prop("totalCount", (long)totalCount)
                .Prop("returnedCount", (long)returnedCount)
                .Prop("pageSize", (long)pageSize)
                .Prop("cursor", (long)cursor)
                .Prop("nextCursor", next)
                .Prop("hasMore", next != null)
                .Prop("truncated", next != null)
                .EndObject().ToString();
        }

        /// <summary>通用文本筛选；query 为空时视为匹配。</summary>
        private static bool LargeOutputTextMatches(string query, params string[] values)
        {
            if (string.IsNullOrEmpty(query)) return true;
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>判断 GameObject 是否拥有指定 Component 类型；空 componentType 表示不过滤。</summary>
        private static bool LargeOutputComponentMatches(GameObject gameObject, string componentType)
        {
            if (gameObject == null || string.IsNullOrEmpty(componentType)) return true;
            var resolved = ResolveComponentType(componentType);
            if (resolved != null) return gameObject.GetComponent(resolved) != null;
            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (LargeOutputComponentTypeMatches(component, componentType)) return true;
            }
            return false;
        }

        /// <summary>判断单个 Component 是否匹配类型筛选；空 componentType 表示不过滤。</summary>
        private static bool LargeOutputComponentTypeMatches(Component component, string componentType)
        {
            if (string.IsNullOrEmpty(componentType)) return true;
            if (component == null) return string.Equals(componentType, "MissingScript", StringComparison.OrdinalIgnoreCase);
            var type = component.GetType();
            return string.Equals(type.Name, componentType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type.FullName, componentType, StringComparison.OrdinalIgnoreCase)
                || type.FullName.IndexOf(componentType, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>写出组件类型摘要，供层级节点默认返回轻量 componentTypes。</summary>
        private static void WriteComponentTypes(UnityCliJsonWriter writer, GameObject gameObject)
        {
            writer.PropArray("componentTypes", arr =>
            {
                foreach (var component in gameObject.GetComponents<Component>())
                {
                    arr.Value(component == null ? "MissingScript" : component.GetType().Name);
                }
            });
        }

        /// <summary>只在显式请求且图片小于固定上限时内联 base64，防止图像撑爆 JSON。</summary>
        private static void AppendInlineScreenshotBytes(UnityCliJsonWriter writer, BridgeRequest request, byte[] bytes, int width, int height, string mimeType = "image/png")
        {
            if (!request.BoolArg("inlineBase64", false)) return;
            if (bytes == null || bytes.Length == 0) return;
            if (bytes.Length > InlineImageMaxBytes)
            {
                WriteInlineScreenshotTooLargeWarning(writer, bytes.Length);
                return;
            }
            AppendInlineScreenshot(writer, Convert.ToBase64String(bytes), width, height, mimeType);
        }

        /// <summary>拦截已经编码的 base64 图片，避免旧调用路径输出超大 imageBase64 字段。</summary>
        private static bool ShouldInlineScreenshotBase64(UnityCliJsonWriter writer, string imageBase64)
        {
            if (string.IsNullOrEmpty(imageBase64)) return false;
            var approximateBytes = (long)Math.Ceiling(imageBase64.Length * 3.0 / 4.0);
            if (approximateBytes <= InlineImageMaxBytes) return true;
            WriteInlineScreenshotTooLargeWarning(writer, approximateBytes);
            return false;
        }

        private static void WriteInlineScreenshotTooLargeWarning(UnityCliJsonWriter writer, long sizeBytes)
        {
            writer.PropArray("warnings", arr => arr.BeginObject()
                .Prop("code", "INLINE_IMAGE_TOO_LARGE")
                .Prop("message", "inlineBase64 was requested, but the image exceeds the internal inline limit; use the artifact path instead.")
                .Prop("sizeBytes", sizeBytes)
                .Prop("maxInlineBytes", (long)InlineImageMaxBytes)
                .EndObject());
        }
    }
}
#endif
