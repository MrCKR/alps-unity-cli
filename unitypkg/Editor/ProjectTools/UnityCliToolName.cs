#if UNITY_EDITOR
using System;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityCli
{
    /// <summary>按公开 contract profile 校验精确 Tool identity 与统一 UTF-8 byte 预算。</summary>
    internal static class UnityCliToolName
    {
        internal const int MaxUtf8Bytes = 128;
        private const string OfficialPrefix = "UnityPipeline_";
        // native Tool 名固定两段：group.tool；tool 段内部细分只用 '-'，不允许第二个点号。
        private static readonly Regex NativePattern = new Regex("^[a-z][a-z0-9-]*\\.[a-z0-9][a-z0-9-]*\\z", RegexOptions.CultureInvariant);
        private static readonly Regex OfficialSuffixPattern = new Regex("^[a-z][a-z0-9_-]*(?:\\.[a-z0-9][a-z0-9_-]*)*\\z", RegexOptions.CultureInvariant);

        internal static void Validate(string name, string contractProfile)
        {
            var validProfile = string.Equals(contractProfile, "native", StringComparison.Ordinal)
                || string.Equals(contractProfile, "official-attribute", StringComparison.Ordinal);
            var validSyntax = string.Equals(contractProfile, "official-attribute", StringComparison.Ordinal)
                ? name != null && name.StartsWith(OfficialPrefix, StringComparison.Ordinal)
                    && OfficialSuffixPattern.IsMatch(name.Substring(OfficialPrefix.Length))
                : string.Equals(contractProfile, "native", StringComparison.Ordinal)
                    && name != null && NativePattern.IsMatch(name);
            if (!validProfile || string.IsNullOrEmpty(name) || Encoding.UTF8.GetByteCount(name) > MaxUtf8Bytes || !validSyntax)
                throw new InvalidOperationException("Tool name must use the registered Tool syntax and fit 128 UTF-8 bytes.");
        }
    }
}
#endif
