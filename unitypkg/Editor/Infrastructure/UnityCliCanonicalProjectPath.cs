using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UnityCli
{
    /// <summary>
    /// CanonicalPath v1 的唯一 Win32 实现。Unity Bridge 与 TS CLI helper 必须复用本类，禁止分别近似规范化。
    /// </summary>
    public static class UnityCliCanonicalProjectPath
    {
        public const int CanonicalizationVersion = 1;
        private const uint FileReadAttributes = 0x80;
        private const uint FileShareRead = 0x1;
        private const uint FileShareWrite = 0x2;
        private const uint FileShareDelete = 0x4;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint LcmapLowercase = 0x00000100;
        private const uint FileCsFlagCaseSensitiveDir = 0x00000001;
        private const int FileCaseSensitiveInfo = 23;
        private const uint DriveUnknown = 0;
        private const uint DriveNoRootDir = 1;
        private const uint DriveRemote = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileCaseSensitiveInformation
        {
            public uint Flags;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(
            SafeFileHandle file,
            [Out] char[] filePath,
            uint filePathLength,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int fileInformationClass,
            out FileCaseSensitiveInformation fileInformation,
            uint bufferSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetDriveTypeW(string rootPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformationW(
            string rootPathName,
            StringBuilder volumeNameBuffer,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer,
            int fileSystemNameSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int LCMapStringEx(
            string localeName,
            uint mapFlags,
            string source,
            int sourceLength,
            [Out] char[] destination,
            int destinationLength,
            IntPtr versionInformation,
            IntPtr reserved,
            IntPtr sortHandle);

        /// <summary>
        /// 从任意项目子路径解析物理 Unity project root，并生成跨进程稳定 identity。
        /// 任何不能证明为本机、case-insensitive、可访问物理目录的输入都 fail closed。
        /// </summary>
        public static CanonicalProjectPathResult Resolve(string inputPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(inputPath))
                    return CanonicalProjectPathResult.Fail("E_INSTANCE_REQUIRED", "A Unity project path is required.");

                var lexicalPath = Path.GetFullPath(inputPath.Trim());
                var existingDirectory = FindNearestExistingDirectory(lexicalPath);
                if (existingDirectory == null)
                    return CanonicalProjectPathResult.Fail("E_INSTANCE_PATH_UNAVAILABLE", "No accessible directory exists for the requested path.");

                if (ContainsBrokenReparsePoint(lexicalPath, existingDirectory))
                    return CanonicalProjectPathResult.Fail("E_INSTANCE_PATH_UNAVAILABLE", "The requested path contains a broken or inaccessible reparse point.");

                var physicalInput = FinalDirectoryPath(existingDirectory);
                var projectRoot = FindUnityProjectRoot(physicalInput);
                if (projectRoot == null)
                    return CanonicalProjectPathResult.Fail("E_INSTANCE_REQUIRED", "The requested path is not inside a Unity project.");

                var displayProjectPath = NormalizeDosPath(FinalDirectoryPath(projectRoot));
                var rootPath = Path.GetPathRoot(displayProjectPath);
                if (string.IsNullOrEmpty(rootPath) || IsUncPath(displayProjectPath))
                    return CanonicalProjectPathResult.Fail("E_INSTANCE_REMOTE_PATH_UNSUPPORTED", "UNC and non-DOS-volume Unity project paths are not supported.");

                var driveType = GetDriveTypeW(EnsureTrailingSeparator(rootPath));
                if (driveType == DriveRemote)
                    return CanonicalProjectPathResult.Fail("E_INSTANCE_REMOTE_PATH_UNSUPPORTED", "Remote Unity project volumes are not supported.");
                if (driveType == DriveUnknown || driveType == DriveNoRootDir || !CanReadVolumeInformation(rootPath))
                    return CanonicalProjectPathResult.Fail("E_INSTANCE_PATH_UNAVAILABLE", "The Unity project volume cannot be verified as a local volume.");

                if (HasCaseSensitiveDirectoryInChain(displayProjectPath, rootPath))
                    return CanonicalProjectPathResult.Fail("E_INSTANCE_PATH_CASE_SENSITIVE_UNSUPPORTED", "Case-sensitive Windows directories are not supported for Unity project identity.");

                var canonicalProjectPath = InvariantLowercase(displayProjectPath);
                var identityHash = Sha256Utf8(canonicalProjectPath);
                return new CanonicalProjectPathResult
                {
                    Success = true,
                    CanonicalizationVersion = CanonicalizationVersion,
                    CanonicalProjectPath = canonicalProjectPath,
                    DisplayProjectPath = displayProjectPath,
                    IdentityHash = identityHash,
                    InstanceId = "unity-" + identityHash
                };
            }
            catch (UnauthorizedAccessException exception)
            {
                return CanonicalProjectPathResult.Fail("E_INSTANCE_PATH_UNAVAILABLE", exception.Message);
            }
            catch (IOException exception)
            {
                return CanonicalProjectPathResult.Fail("E_INSTANCE_PATH_UNAVAILABLE", exception.Message);
            }
            catch (Exception exception)
            {
                return CanonicalProjectPathResult.Fail("E_INSTANCE_PATH_UNAVAILABLE", exception.Message);
            }
        }

        /// <summary>查找最近的已存在目录；文件输入从其父目录开始。</summary>
        private static string FindNearestExistingDirectory(string path)
        {
            var current = File.Exists(path) ? Path.GetDirectoryName(path) : path;
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(current)) return current;
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal)) break;
                current = parent;
            }
            return null;
        }

        /// <summary>检测未解析尾部中的 broken junction/symlink，禁止退回父目录身份。</summary>
        private static bool ContainsBrokenReparsePoint(string lexicalPath, string existingDirectory)
        {
            var current = lexicalPath;
            while (!string.IsNullOrEmpty(current) && !string.Equals(current, existingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var attributes = File.GetAttributes(current);
                    if ((attributes & FileAttributes.ReparsePoint) != 0 && !Directory.Exists(current) && !File.Exists(current)) return true;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal)) break;
                current = parent;
            }
            return false;
        }

        /// <summary>从物理输入路径向上发现最近 Unity project root。</summary>
        private static string FindUnityProjectRoot(string startDirectory)
        {
            var current = startDirectory;
            while (!string.IsNullOrEmpty(current))
            {
                if (Directory.Exists(Path.Combine(current, "Assets"))
                    && File.Exists(Path.Combine(current, "Packages", "manifest.json"))
                    && File.Exists(Path.Combine(current, "ProjectSettings", "ProjectVersion.txt")))
                    return current;
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal)) break;
                current = parent;
            }
            return null;
        }

        /// <summary>通过 directory handle 获取 Win32 final DOS path。</summary>
        private static string FinalDirectoryPath(string directory)
        {
            using (var handle = CreateFileW(directory, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero))
            {
                if (handle == null || handle.IsInvalid)
                    throw new IOException("CreateFileW failed for directory path. Win32=" + Marshal.GetLastWin32Error());

                var buffer = new char[512];
                while (true)
                {
                    var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
                    if (length == 0) throw new IOException("GetFinalPathNameByHandleW failed. Win32=" + Marshal.GetLastWin32Error());
                    if (length < buffer.Length) return NormalizeDosPath(new string(buffer, 0, (int)length));
                    buffer = new char[length + 1];
                }
            }
        }

        /// <summary>检查 volume root 到 project root 的每一级目录 case-sensitive flag。</summary>
        private static bool HasCaseSensitiveDirectoryInChain(string projectRoot, string volumeRoot)
        {
            var current = projectRoot;
            while (!string.IsNullOrEmpty(current))
            {
                using (var handle = CreateFileW(current, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero))
                {
                    if (handle == null || handle.IsInvalid)
                        throw new IOException("CreateFileW failed while checking case sensitivity. Win32=" + Marshal.GetLastWin32Error());
                    FileCaseSensitiveInformation information;
                    if (!GetFileInformationByHandleEx(handle, FileCaseSensitiveInfo, out information, (uint)Marshal.SizeOf(typeof(FileCaseSensitiveInformation))))
                        throw new IOException("GetFileInformationByHandleEx(FileCaseSensitiveInfo) failed. Win32=" + Marshal.GetLastWin32Error());
                    if ((information.Flags & FileCsFlagCaseSensitiveDir) != 0) return true;
                }

                if (string.Equals(EnsureTrailingSeparator(current), EnsureTrailingSeparator(volumeRoot), StringComparison.OrdinalIgnoreCase)) break;
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal)) break;
                current = parent;
            }
            return false;
        }

        /// <summary>读取 volume information，拒绝无法证明为可访问本机卷的路径。</summary>
        private static bool CanReadVolumeInformation(string rootPath)
        {
            uint serial;
            uint maximumComponentLength;
            uint flags;
            return GetVolumeInformationW(EnsureTrailingSeparator(rootPath), new StringBuilder(261), 261, out serial, out maximumComponentLength, out flags, new StringBuilder(261), 261);
        }

        /// <summary>调用 Win32 invariant locale lowercase，禁止使用 .NET ToLowerInvariant。</summary>
        private static string InvariantLowercase(string value)
        {
            var required = LCMapStringEx(string.Empty, LcmapLowercase, value, value.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (required <= 0) throw new IOException("LCMapStringEx length query failed. Win32=" + Marshal.GetLastWin32Error());
            var buffer = new char[required];
            var written = LCMapStringEx(string.Empty, LcmapLowercase, value, value.Length, buffer, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (written <= 0) throw new IOException("LCMapStringEx failed. Win32=" + Marshal.GetLastWin32Error());
            return new string(buffer, 0, written);
        }

        /// <summary>规范化 Win32 extended path 表示，不改变 Unicode normalization。</summary>
        private static string NormalizeDosPath(string value)
        {
            var normalized = value.Replace('/', '\\');
            if (normalized.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
                normalized = "\\\\" + normalized.Substring(8);
            else if (normalized.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(4);
            else if (normalized.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(4);

            var root = Path.GetPathRoot(normalized);
            while (normalized.Length > (root == null ? 0 : root.Length) && normalized.EndsWith("\\", StringComparison.Ordinal))
                normalized = normalized.Substring(0, normalized.Length - 1);
            return normalized;
        }

        private static bool IsUncPath(string value)
        {
            return value.StartsWith("\\\\", StringComparison.Ordinal);
        }

        private static string EnsureTrailingSeparator(string value)
        {
            return value.EndsWith("\\", StringComparison.Ordinal) ? value : value + "\\";
        }

        private static string Sha256Utf8(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(new UTF8Encoding(false).GetBytes(value));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var item in bytes) builder.Append(item.ToString("x2"));
                return builder.ToString();
            }
        }
    }

    /// <summary>PowerShell 与 Unity Bridge 共享的 CanonicalPath v1 DTO。</summary>
    public sealed class CanonicalProjectPathResult
    {
        public bool Success { get; set; }
        public int CanonicalizationVersion { get; set; }
        public string CanonicalProjectPath { get; set; }
        public string DisplayProjectPath { get; set; }
        public string IdentityHash { get; set; }
        public string InstanceId { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }

        public static CanonicalProjectPathResult Fail(string code, string message)
        {
            return new CanonicalProjectPathResult
            {
                Success = false,
                CanonicalizationVersion = UnityCliCanonicalProjectPath.CanonicalizationVersion,
                ErrorCode = code,
                ErrorMessage = message
            };
        }
    }
}
