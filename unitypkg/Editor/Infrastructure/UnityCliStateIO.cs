#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private const uint StateIOMoveFileReplaceExisting = 0x1;
        private const uint StateIOMoveFileWriteThrough = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);

        /// <summary>临时文件落盘后 write-through 替换；供 coordinator 的 durable 状态写入复用。</summary>
        private static void DurableWriteText(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.Write(content); writer.Flush(); stream.Flush(true); }
                if (!MoveFileExW(temporary, path, StateIOMoveFileReplaceExisting | StateIOMoveFileWriteThrough))
                    throw new IOException("MoveFileExW failed: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>小写 hex 编码。</summary>
        private static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }

        /// <summary>SHA-256 的小写 hex。</summary>
        private static string Sha256Hex(byte[] bytes) { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }

        /// <summary>64 位小写 hex 校验。</summary>
        private static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64) return false;
            foreach (var ch in value) if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
            return true;
        }

        /// <summary>requestId 安全字符集校验（字母数字、点、下划线、连字符）。</summary>
        private static bool IsSafeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200) return false;
            foreach (var ch in value) if (!(char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-')) return false;
            return true;
        }

        /// <summary>带前缀的小写 hex 格式校验（如 session- 后 64 hex）。</summary>
        private static bool IsPrefixedLowerHex(string value, string prefix, int hexLength)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var hex = value.Substring(prefix.Length);
            if (hex.Length != hexLength) return false;
            foreach (var ch in hex) if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'))) return false;
            return true;
        }
    }
}
#endif
