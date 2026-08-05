#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace UnityCli
{
    /// <summary>跨 domain reload 续接的单 scope 进行中操作状态。</summary>
    internal static class UnityCliActiveOperation
    {
        internal static Dictionary<string, object> Load(string scope)
        {
            var path = ActiveOperationPath(scope);
            if (!File.Exists(path)) return null;

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
                throw new InvalidDataException("Active operation state must not contain a UTF-8 BOM.");
            var state = UnityCliSerializer.Parse(new UTF8Encoding(false, true).GetString(bytes)) as Dictionary<string, object>;
            if (state == null) throw new InvalidDataException("Active operation state root must be an object.");
            return state;
        }

        internal static void Save(string scope, Dictionary<string, object> state)
        {
            if (state == null) throw new ArgumentNullException("state");
            DurableWriteText(ActiveOperationPath(scope), UnityCliSerializer.Serialize(state) + "\n");
        }

        internal static void Clear(string scope)
        {
            var path = ActiveOperationPath(scope);
            if (File.Exists(path)) File.Delete(path);
        }

        internal static bool Exists(string scope)
        {
            return File.Exists(ActiveOperationPath(scope));
        }

        private static string ActiveOperationPath(string scope)
        {
            if (string.IsNullOrWhiteSpace(scope) || scope.Length > 80)
                throw new ArgumentException("Active operation scope is required.", "scope");
            foreach (var character in scope)
                if (!(char.IsLetterOrDigit(character) || character == '-'))
                    throw new ArgumentException("Active operation scope contains invalid characters.", "scope");
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            if (string.IsNullOrWhiteSpace(projectRoot)) throw new InvalidOperationException("Unity project root is unavailable.");
            return Path.Combine(projectRoot, "Library", "UnityCli", "active-" + scope + ".json");
        }

        private const uint MoveFileReplaceExisting = 0x1;
        private const uint MoveFileWriteThrough = 0x8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileExW(string existingFileName, string newFileName, uint flags);

        /// <summary>临时文件落盘后使用 write-through 替换，旧状态始终保持完整。</summary>
        private static void DurableWriteText(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.Write(content); writer.Flush(); stream.Flush(true); }
                if (!MoveFileExW(temporary, path, MoveFileReplaceExisting | MoveFileWriteThrough))
                    throw new IOException("MoveFileExW failed: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
#endif
