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
        private static int LargeOutputPageSize(UnityCli.Protocol.BridgeRequest request, int fallback, int max)
        {
            return Math.Max(1, Math.Min(request.IntArg("pageSize", fallback), max));
        }

        /// <summary>解析统一分页游标，负数游标按 0 处理。</summary>
        private static int LargeOutputCursor(UnityCli.Protocol.BridgeRequest request)
        {
            return Math.Max(0, request.IntArg("cursor", 0));
        }

        // 分页元数据保持为 typed 数据，最终 response 统一在协议层序列化。
        private static Dictionary<string, object> PageInfoData(int pageSize, int cursor, int totalCount, int returnedCount)
        {
            var next = cursor + returnedCount < totalCount ? (object)(long)(cursor + returnedCount) : null;
            return new Dictionary<string, object>
            {
                { "total", (long)totalCount },
                { "totalCount", (long)totalCount },
                { "returnedCount", (long)returnedCount },
                { "pageSize", (long)pageSize },
                { "cursor", (long)cursor },
                { "nextCursor", next },
                { "hasMore", next != null },
                { "truncated", next != null }
            };
        }

        /// <summary>通用文本筛选；query 为空时视为匹配。</summary>
        private static bool LargeOutputTextMatches(string query, params string[] values)
        {
            if (string.IsNullOrEmpty(query)) return true;
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) return true;
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

        /// <summary>返回轻量组件类型摘要，供层级节点默认输出。</summary>
        private static List<object> ComponentTypesData(GameObject gameObject)
        {
            var types = new List<object>();
            foreach (var component in gameObject.GetComponents<Component>()) types.Add(component == null ? "MissingScript" : component.GetType().Name);
            return types;
        }

        // 图片字段与告警直接写入业务数据，避免把局部 JSON 再嵌入 response。
        private static void AppendInlineScreenshotBytes(Dictionary<string, object> data, UnityCli.Protocol.BridgeRequest request, byte[] bytes, int width, int height, string mimeType = "image/png")
        {
            if (!request.BoolArg("inlineBase64", false) || bytes == null || bytes.Length == 0) return;
            if (bytes.Length > InlineImageMaxBytes)
            {
                AddInlineScreenshotTooLargeWarning(data, bytes.Length);
                return;
            }
            data["imageBase64"] = Convert.ToBase64String(bytes);
            data["imageWidth"] = (long)width;
            data["imageHeight"] = (long)height;
            data["imageMimeType"] = mimeType;
        }

        private static bool ShouldInlineScreenshotBase64(Dictionary<string, object> data, string imageBase64)
        {
            if (string.IsNullOrEmpty(imageBase64)) return false;
            var approximateBytes = (long)Math.Ceiling(imageBase64.Length * 3.0 / 4.0);
            if (approximateBytes <= InlineImageMaxBytes) return true;
            AddInlineScreenshotTooLargeWarning(data, approximateBytes);
            return false;
        }

        private static void AddInlineScreenshotTooLargeWarning(Dictionary<string, object> data, long sizeBytes)
        {
            object rawWarnings;
            var warnings = data.TryGetValue("warnings", out rawWarnings) ? rawWarnings as List<object> : null;
            if (warnings == null)
            {
                warnings = new List<object>();
                data["warnings"] = warnings;
            }
            warnings.Add(new Dictionary<string, object>
            {
                { "code", "INLINE_IMAGE_TOO_LARGE" },
                { "message", "inlineBase64 was requested, but the image exceeds the internal inline limit; use the artifact path instead." },
                { "sizeBytes", sizeBytes },
                { "maxInlineBytes", (long)InlineImageMaxBytes }
            });
        }
    }
}
#endif
