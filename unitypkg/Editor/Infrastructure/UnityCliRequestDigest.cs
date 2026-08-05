#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UnityCli
{
    /// <summary>请求摘要：把 instanceId 与 HTTP 收到的原始字节绑定为确定性 SHA-256，供 lease 与幂等校验使用。</summary>
    internal static class UnityCliRequestDigest
    {
        /// <summary>digest 严格绑定 instanceId 与原始请求字节，绝不规范化 JSON。</summary>
        internal static string Compute(byte[] rawBytes, string instanceId)
        {
            if (rawBytes == null) throw new ArgumentNullException("rawBytes");
            if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Instance ID is required.", "instanceId");
            var prefix = Encoding.UTF8.GetBytes("unity-cli-request-v1");
            var instance = Encoding.UTF8.GetBytes(instanceId);
            var bytes = new byte[prefix.Length + 1 + instance.Length + 1 + rawBytes.Length];
            Buffer.BlockCopy(prefix, 0, bytes, 0, prefix.Length);
            Buffer.BlockCopy(instance, 0, bytes, prefix.Length + 1, instance.Length);
            Buffer.BlockCopy(rawBytes, 0, bytes, prefix.Length + 1 + instance.Length + 1, rawBytes.Length);
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes));
        }

        private static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }
}
#endif
