#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UnityEditor;
using UnityEngine;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace UnityCli
{
    /// <summary>
    /// 在项目首次同时检测到Pipeline与本package时接管默认Editor server。
    /// 成功标记落盘后永久尊重用户后续手动配置，不在reload或升级时重复覆盖。
    /// </summary>
    internal static class UnityCliPipelineHandoff
    {
        internal const string PipelinePackageName = "com.unity.pipeline";
        private const string ManagerTypeName = "Unity.Pipeline.Editor.EditorPipelineManager";
        private const string StartupTypeName = "Unity.Pipeline.Editor.PipelineServerStartup";
        private const string AutoTickTypeName = "Unity.Pipeline.Editor.Commands.AutoTickCommand";
        private const string DescriptorTypeName = "Unity.Pipeline.Models.InstanceDescriptor, Unity.Pipeline";
        private const string ManagerAssetPath = "Assets/Settings/Pipeline/EditorPipelineManager.asset";

        private sealed class ManagerSnapshot
        {
            internal string Path;
            internal bool AutoStart;
            internal bool CreatedByHandoff;
        }

        private sealed class DescriptorSnapshot
        {
            internal string Path;
            internal byte[] Raw;
            internal int Port;
            internal uint VolumeSerialNumber;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            internal uint Low;
            internal uint High;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal NativeFileTime CreationTime;
            internal NativeFileTime LastAccessTime;
            internal NativeFileTime LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        private enum FileInfoByHandleClass
        {
            FileDispositionInfo = 4
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct FileDispositionInfo
        {
            internal byte DeleteFile;
        }

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint DeleteAccess = 0x00010000;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint FileAttributeReparsePoint = 0x00000400;
        private const uint FileFlagOpenReparsePoint = 0x00200000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle fileHandle,
            out ByHandleFileInformation fileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle fileHandle,
            FileInfoByHandleClass fileInformationClass,
            ref FileDispositionInfo fileInformation,
            uint bufferSize);

        /// <summary>
        /// 执行一次性接管。Pipeline未安装不算失败且不写成功标记，后续安装后仍可完成接管；
        /// Pipeline已安装但无法完整配置时返回false，调用方必须拒绝启动第二个控制面。
        /// </summary>
        internal static bool EnsureAppliedOnce(out string state, out string error)
        {
            state = "unknown";
            error = null;
            var settings = UnityCliProjectSettings.Current;
            if (settings.PipelineHandoffApplied)
            {
                state = "already-applied";
                return true;
            }

            Type managerType = null;
            List<ManagerSnapshot> managerSnapshots = null;
            List<TcpListener> portGuards = null;
            try
            {
                var package = FindPipelinePackage();
                if (package == null)
                {
                    state = "pipeline-not-installed";
                    return true;
                }

                managerType = RequireOwnedManagerType();
                string createdManagerPath;
                var managers = LoadOrCreateManagers(managerType, out createdManagerPath);
                try
                {
                    managerSnapshots = PreflightManagers(managers, createdManagerPath);
                }
                catch (Exception preflightException)
                {
                    if (!string.IsNullOrEmpty(createdManagerPath))
                    {
                        var deleted = AssetDatabase.DeleteAsset(createdManagerPath);
                        if (!deleted || AssetDatabase.LoadMainAssetAtPath(createdManagerPath) != null)
                            throw new InvalidOperationException("Created Pipeline manager cleanup failed: " + createdManagerPath + ".", preflightException);
                    }
                    throw;
                }
                managers = SetAutoStartDisabledAndReload(managerType, managers, managerSnapshots);
                portGuards = StopAndGuardPipelineServer(managerType.Assembly);
                DisableAndVerifyPipelineAutoTick(managerType.Assembly);
                VerifyAutoStartDisabled(managers);
                VerifyPipelineShutdownStable(managerType.Assembly);

                settings.MarkPipelineHandoffApplied();
                managerSnapshots = null;
                state = "applied";
                return true;
            }
            catch (Exception exception)
            {
                state = "failed";
                error = ExceptionMessage(exception);
                if (managerSnapshots != null)
                {
                    string rollbackError;
                    if (!TryRestoreManagers(managerType, managerSnapshots, out rollbackError)) error += " Rollback failed: " + rollbackError;
                }
                return false;
            }
            finally
            {
                DisposePortGuards(portGuards);
            }
        }

        /// <summary>
        /// 在真正handoff前显式触发Pipeline InitializeOnLoad owner；Bridge再跨两轮delay，
        /// 确保该构造器排入的启动callback先完成，最终proof不依赖两个package的初始化顺序。
        /// </summary>
        internal static bool PrimePipelineStartup(out string state, out string error)
        {
            state = "unknown";
            error = null;
            if (UnityCliProjectSettings.Current.PipelineHandoffApplied)
            {
                state = "already-applied";
                return true;
            }

            try
            {
                if (FindPipelinePackage() == null)
                {
                    state = "pipeline-not-installed";
                    return true;
                }
                var managerType = RequireOwnedManagerType();
                var startup = RequireType(managerType.Assembly, StartupTypeName);
                var serverProperty = startup.GetProperty("Server", BindingFlags.Public | BindingFlags.Static);
                if (serverProperty == null) throw new InvalidOperationException("Pipeline Server status API is unavailable.");
                serverProperty.GetValue(null, null);
                state = "primed";
                return true;
            }
            catch (Exception exception)
            {
                state = "failed";
                error = ExceptionMessage(exception);
                return false;
            }
        }

        private static Type RequireOwnedManagerType()
        {
            var managerType = Type.GetType(ManagerTypeName + ", Unity.Pipeline.Editor", false);
            if (managerType == null)
                throw new InvalidOperationException("Installed com.unity.pipeline does not expose " + ManagerTypeName + ".");
            var owner = PackageManagerPackageInfo.FindForAssembly(managerType.Assembly);
            if (owner == null || !string.Equals(owner.name, PipelinePackageName, StringComparison.Ordinal))
                throw new InvalidOperationException("Pipeline manager assembly ownership cannot be proven.");
            return managerType;
        }

        private static Type RequireOwnedDescriptorType()
        {
            var descriptorType = Type.GetType(DescriptorTypeName, false);
            if (descriptorType == null) throw new InvalidOperationException("Pipeline InstanceDescriptor API is unavailable.");
            var owner = PackageManagerPackageInfo.FindForAssembly(descriptorType.Assembly);
            if (owner == null || !string.Equals(owner.name, PipelinePackageName, StringComparison.Ordinal))
                throw new InvalidOperationException("Pipeline descriptor assembly ownership cannot be proven.");
            return descriptorType;
        }

        /// <summary>按exact Package ID查找Pipeline；不存在时保持handoff待处理。</summary>
        private static PackageManagerPackageInfo FindPipelinePackage()
        {
            foreach (var package in PackageManagerPackageInfo.GetAllRegisteredPackages())
            {
                if (package != null && string.Equals(package.name, PipelinePackageName, StringComparison.Ordinal)) return package;
            }
            return null;
        }

        /// <summary>
        /// 加载base与全部派生manager资产。Unity 2022的base type查询不会稳定返回项目派生类型，
        /// 因此必须先由TypeCache枚举派生type，再按每个type查询并以GUID去重。
        /// </summary>
        private static List<UnityEngine.Object> LoadOrCreateManagers(Type managerType, out string createdManagerPath)
        {
            createdManagerPath = null;
            var candidateTypes = new List<Type> { managerType };
            foreach (var derivedType in TypeCache.GetTypesDerivedFrom(managerType))
            {
                if (derivedType != null && managerType.IsAssignableFrom(derivedType)) candidateTypes.Add(derivedType);
            }

            var guids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidateType in candidateTypes)
            {
                foreach (var guid in AssetDatabase.FindAssets("t:" + candidateType.Name)) guids.Add(guid);
            }

            var result = new List<UnityEngine.Object>();
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var value = AssetDatabase.LoadAssetAtPath(path, managerType);
                if (value != null && managerType.IsInstanceOfType(value)) result.Add(value);
            }
            if (result.Count > 0) return result;

            EnsureFolder("Assets/Settings");
            EnsureFolder("Assets/Settings/Pipeline");
            var occupied = AssetDatabase.LoadMainAssetAtPath(ManagerAssetPath);
            if (occupied != null)
                throw new InvalidOperationException("Pipeline settings path is occupied by " + occupied.GetType().FullName + ".");
            var created = ScriptableObject.CreateInstance(managerType);
            if (created == null) throw new InvalidOperationException("Pipeline settings asset could not be created.");
            AssetDatabase.CreateAsset(created, ManagerAssetPath);
            createdManagerPath = ManagerAssetPath;
            result.Add(created);
            return result;
        }

        /// <summary>
        /// mutation前完整校验全部manager。已有dirty状态会携带用户未保存字段，不能为了AutoStart把整份资产一并落盘。
        /// </summary>
        private static List<ManagerSnapshot> PreflightManagers(List<UnityEngine.Object> managers, string createdManagerPath)
        {
            var snapshots = new List<ManagerSnapshot>();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var manager in managers)
            {
                var path = AssetDatabase.GetAssetPath(manager);
                if (string.IsNullOrEmpty(path) || !paths.Add(path))
                    throw new InvalidOperationException("Pipeline manager asset path is unavailable or duplicated.");
                if (EditorUtility.IsDirty(manager) && !string.Equals(path, createdManagerPath, StringComparison.Ordinal))
                    throw new InvalidOperationException("Pipeline manager has unsaved user changes and cannot be handed off safely: " + path + ".");
                if (!AssetDatabase.IsOpenForEdit(path, StatusQueryOptions.UseCachedIfPossible))
                    throw new InvalidOperationException("Pipeline manager asset is not writable: " + path + ".");
                var serialized = new SerializedObject(manager);
                serialized.Update();
                var property = serialized.FindProperty("m_AutoStart");
                if (property == null || property.propertyType != SerializedPropertyType.Boolean)
                    throw new InvalidOperationException("Pipeline Editor AutoStart field is unavailable: " + path + ".");
                snapshots.Add(new ManagerSnapshot
                {
                    Path = path,
                    AutoStart = property.boolValue,
                    CreatedByHandoff = string.Equals(path, createdManagerPath, StringComparison.Ordinal)
                });
            }
            return snapshots;
        }

        /// <summary>
        /// 只保存本次manager资产；随后卸载并强制同步重导，返回值不能沿用已修改的内存对象。
        /// </summary>
        private static List<UnityEngine.Object> SetAutoStartDisabledAndReload(
            Type managerType,
            List<UnityEngine.Object> managers,
            List<ManagerSnapshot> snapshots)
        {
            foreach (var manager in managers)
            {
                var serialized = new SerializedObject(manager);
                serialized.Update();
                serialized.FindProperty("m_AutoStart").boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(manager);
            }
            foreach (var manager in managers) AssetDatabase.SaveAssetIfDirty(manager);
            foreach (var manager in managers) Resources.UnloadAsset(manager);
            return ReloadManagers(managerType, snapshots);
        }

        private static List<UnityEngine.Object> ReloadManagers(Type managerType, List<ManagerSnapshot> snapshots)
        {
            var reloaded = new List<UnityEngine.Object>();
            foreach (var snapshot in snapshots)
            {
                AssetDatabase.ImportAsset(snapshot.Path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var manager = AssetDatabase.LoadAssetAtPath(snapshot.Path, managerType);
                if (manager == null || !managerType.IsInstanceOfType(manager))
                    throw new InvalidOperationException("Pipeline manager asset could not be reloaded from disk: " + snapshot.Path + ".");
                reloaded.Add(manager);
            }
            return reloaded;
        }

        /// <summary>handoff后续proof失败时恢复原AutoStart；本次创建的默认manager直接删除。</summary>
        private static bool TryRestoreManagers(Type managerType, List<ManagerSnapshot> snapshots, out string error)
        {
            error = null;
            try
            {
                foreach (var snapshot in snapshots)
                {
                    if (snapshot.CreatedByHandoff)
                    {
                        if (!AssetDatabase.DeleteAsset(snapshot.Path) && AssetDatabase.LoadMainAssetAtPath(snapshot.Path) != null)
                            throw new InvalidOperationException("Created Pipeline manager could not be removed: " + snapshot.Path + ".");
                        continue;
                    }
                    var manager = AssetDatabase.LoadAssetAtPath(snapshot.Path, managerType);
                    if (manager == null) throw new InvalidOperationException("Pipeline manager could not be loaded for rollback: " + snapshot.Path + ".");
                    var serialized = new SerializedObject(manager);
                    serialized.Update();
                    var property = serialized.FindProperty("m_AutoStart");
                    if (property == null || property.propertyType != SerializedPropertyType.Boolean)
                        throw new InvalidOperationException("Pipeline manager rollback field is unavailable: " + snapshot.Path + ".");
                    property.boolValue = snapshot.AutoStart;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(manager);
                    AssetDatabase.SaveAssetIfDirty(manager);
                    Resources.UnloadAsset(manager);
                    AssetDatabase.ImportAsset(snapshot.Path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                }
                return true;
            }
            catch (Exception exception)
            {
                error = ExceptionMessage(exception);
                return false;
            }
        }

        /// <summary>
        /// 冻结当前进程的官方server与descriptor raw identity，停服后持续占用原端口直到marker落盘。
        /// descriptor只能在exact raw仍匹配时通过Windows文件句柄标记删除，不能路径级无条件删除。
        /// </summary>
        private static List<TcpListener> StopAndGuardPipelineServer(Assembly assembly)
        {
            var startup = RequireType(assembly, StartupTypeName);
            var serverProperty = startup.GetProperty("Server", BindingFlags.Public | BindingFlags.Static);
            var stopMethod = startup.GetMethod("StopServer", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (serverProperty == null || stopMethod == null)
                throw new InvalidOperationException("Pipeline server control API is unavailable.");

            var server = serverProperty.GetValue(null, null);
            var ports = new HashSet<int>();
            if (server != null)
            {
                var port = ReadIntProperty(server, "Port");
                var running = ReadBoolProperty(server, "IsRunning");
                if (running && port <= 0) throw new InvalidOperationException("Running Pipeline server did not expose a valid port.");
                if (port > 0) ports.Add(port);
            }

            string descriptorPath;
            var descriptorSnapshot = CaptureDescriptorSnapshot(out descriptorPath);
            if (descriptorSnapshot != null) ports.Add(descriptorSnapshot.Port);

            stopMethod.Invoke(null, null);
            var current = serverProperty.GetValue(null, null);
            if (current != null && ReadBoolProperty(current, "IsRunning"))
                throw new InvalidOperationException("Pipeline Editor server is still running after StopServer.");
            if (server != null && ReadBoolProperty(server, "IsRunning"))
                throw new InvalidOperationException("Captured Pipeline Editor server is still running after StopServer.");

            var guards = new List<TcpListener>();
            try
            {
                foreach (var port in ports) guards.Add(CreatePipelinePortGuard(port));
                DeleteExactDescriptor(descriptorPath, descriptorSnapshot);
                if (File.Exists(descriptorPath))
                    throw new InvalidOperationException("Pipeline instance descriptor reappeared during server shutdown proof.");
                return guards;
            }
            catch
            {
                DisposePortGuards(guards);
                throw;
            }
        }

        /// <summary>
        /// marker前在同一主线程stack做最终复核；Unity Editor callback不能在该同步区间插入新的启动动作。
        /// </summary>
        private static void VerifyPipelineShutdownStable(Assembly assembly)
        {
            var startup = RequireType(assembly, StartupTypeName);
            var serverProperty = startup.GetProperty("Server", BindingFlags.Public | BindingFlags.Static);
            if (serverProperty == null) throw new InvalidOperationException("Pipeline Server status API is unavailable.");
            var server = serverProperty.GetValue(null, null);
            if (server != null && ReadBoolProperty(server, "IsRunning"))
                throw new InvalidOperationException("Pipeline Editor server restarted before the handoff marker.");

            var descriptorType = RequireOwnedDescriptorType();
            var descriptorPathMethod = descriptorType.GetMethod("GetDescriptorFilePath", BindingFlags.Public | BindingFlags.Static);
            if (descriptorPathMethod == null) throw new InvalidOperationException("Pipeline descriptor path API is unavailable.");
            var projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var descriptorPath = Convert.ToString(descriptorPathMethod.Invoke(null, new object[] { projectPath }));
            if (string.IsNullOrEmpty(descriptorPath) || File.Exists(descriptorPath))
                throw new InvalidOperationException("Pipeline instance descriptor reappeared before the handoff marker.");
        }

        private static DescriptorSnapshot CaptureDescriptorSnapshot(out string descriptorPath)
        {
            var descriptorType = RequireOwnedDescriptorType();
            var descriptorPathMethod = descriptorType.GetMethod("GetDescriptorFilePath", BindingFlags.Public | BindingFlags.Static);
            if (descriptorPathMethod == null) throw new InvalidOperationException("Pipeline descriptor path API is unavailable.");

            var projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            descriptorPath = Convert.ToString(descriptorPathMethod.Invoke(null, new object[] { projectPath }));
            if (string.IsNullOrEmpty(descriptorPath)) throw new InvalidOperationException("Pipeline descriptor path is unavailable.");
            if (!File.Exists(descriptorPath)) return null;
            EnsureDescriptorPathHasNoReparsePoint(descriptorPath, projectPath);

            var handle = OpenDescriptorHandle(descriptorPath, GenericRead);
            byte[] raw;
            ByHandleFileInformation identity;
            try
            {
                using (var stream = new FileStream(handle, FileAccess.Read))
                {
                    identity = ReadFileIdentity(stream.SafeFileHandle);
                    raw = ReadAllBytes(stream);
                }
            }
            finally
            {
                handle.Dispose();
            }
            var descriptor = DeserializeDescriptor(raw, descriptorType);
            if (descriptor == null) throw new InvalidOperationException("Pipeline instance descriptor is unreadable.");
            var pid = ReadIntProperty(descriptor, "Pid");
            if (pid != Process.GetCurrentProcess().Id)
                throw new InvalidOperationException("Pipeline instance descriptor belongs to another Editor process: " + pid + ".");
            var port = ReadIntProperty(descriptor, "Port");
            if (port <= 0) throw new InvalidOperationException("Pipeline instance descriptor contains an invalid port.");
            return new DescriptorSnapshot
            {
                Path = descriptorPath,
                Raw = raw,
                Port = port,
                VolumeSerialNumber = identity.VolumeSerialNumber,
                FileIndexHigh = identity.FileIndexHigh,
                FileIndexLow = identity.FileIndexLow
            };
        }

        private static object DeserializeDescriptor(byte[] raw, Type descriptorType)
        {
            var jsonConvert = Type.GetType("Newtonsoft.Json.JsonConvert, Newtonsoft.Json", false);
            var deserialize = jsonConvert == null ? null : jsonConvert.GetMethod(
                "DeserializeObject",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(Type) },
                null);
            if (deserialize == null) throw new InvalidOperationException("Pipeline descriptor JSON parser is unavailable.");
            return deserialize.Invoke(null, new object[] { Encoding.UTF8.GetString(raw), descriptorType });
        }

        /// <summary>以exclusive TCP socket持续占用旧端口，不依赖HTTP.sys URL ACL。</summary>
        private static TcpListener CreatePipelinePortGuard(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Server.ExclusiveAddressUse = true;
                listener.Start();
                return listener;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Pipeline HTTP listener still owns port " + port + ".", exception);
            }
        }

        private static void DeleteExactDescriptor(string descriptorPath, DescriptorSnapshot snapshot)
        {
            if (!File.Exists(descriptorPath)) return;
            if (snapshot == null)
                throw new InvalidOperationException("A new Pipeline descriptor appeared after the initial ownership snapshot.");
            if (Application.platform != RuntimePlatform.WindowsEditor)
                throw new InvalidOperationException("Exact stale Pipeline descriptor deletion is supported only on Windows Editor.");

            var projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            EnsureDescriptorPathHasNoReparsePoint(descriptorPath, projectPath);
            var handle = OpenDescriptorHandle(descriptorPath, GenericRead | GenericWrite | DeleteAccess);
            try
            {
                using (var stream = new FileStream(handle, FileAccess.ReadWrite))
                {
                    var identity = ReadFileIdentity(stream.SafeFileHandle);
                    if (identity.VolumeSerialNumber != snapshot.VolumeSerialNumber
                        || identity.FileIndexHigh != snapshot.FileIndexHigh
                        || identity.FileIndexLow != snapshot.FileIndexLow)
                    {
                        throw new InvalidOperationException("Pipeline descriptor file identity changed before exact deletion.");
                    }
                    var current = ReadAllBytes(stream);
                    if (!BytesEqual(current, snapshot.Raw))
                        throw new InvalidOperationException("Pipeline descriptor payload changed before exact deletion.");
                    var disposition = new FileDispositionInfo { DeleteFile = 1 };
                    var dispositionSize = (uint)Marshal.SizeOf(typeof(FileDispositionInfo));
                    if (dispositionSize != 1u) throw new InvalidOperationException("FILE_DISPOSITION_INFO ABI size must be one byte.");
                    if (!SetFileInformationByHandle(
                        stream.SafeFileHandle,
                        FileInfoByHandleClass.FileDispositionInfo,
                        ref disposition,
                        dispositionSize))
                    {
                        throw new InvalidOperationException("Pipeline descriptor exact deletion failed with Win32 error " + Marshal.GetLastWin32Error() + ".");
                    }
                }
            }
            finally
            {
                handle.Dispose();
            }
            if (File.Exists(snapshot.Path))
                throw new InvalidOperationException("Pipeline descriptor exact deletion did not remove the frozen file.");
        }

        private static SafeFileHandle OpenDescriptorHandle(string path, uint desiredAccess)
        {
            var handle = CreateFileW(
                path,
                desiredAccess,
                0,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal | FileFlagOpenReparsePoint,
                IntPtr.Zero);
            if (handle != null && !handle.IsInvalid) return handle;
            var error = Marshal.GetLastWin32Error();
            if (handle != null) handle.Dispose();
            throw new InvalidOperationException("Pipeline descriptor exclusive open failed with Win32 error " + error + ".");
        }

        private static ByHandleFileInformation ReadFileIdentity(SafeFileHandle handle)
        {
            ByHandleFileInformation information;
            if (!GetFileInformationByHandle(handle, out information))
                throw new InvalidOperationException("Pipeline descriptor identity read failed with Win32 error " + Marshal.GetLastWin32Error() + ".");
            if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
                throw new InvalidOperationException("Pipeline descriptor must not be a reparse point.");
            return information;
        }

        private static void EnsureDescriptorPathHasNoReparsePoint(string descriptorPath, string projectPath)
        {
            var root = Path.GetFullPath(projectPath);
            var pathRoot = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(pathRoot)) throw new InvalidOperationException("Pipeline project root is invalid.");
            if (root.Length > pathRoot.Length) root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetFullPath(descriptorPath);
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || root.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase)
                && !current.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Pipeline descriptor path escapes the selected project.");
            }

            while (true)
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & System.IO.FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException("Pipeline descriptor path contains a reparse point: " + current + ".");
                }
                if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(current)) throw new InvalidOperationException("Pipeline descriptor path ancestry is invalid.");
            }
        }

        private static byte[] ReadAllBytes(Stream stream)
        {
            if (stream.Length < 0 || stream.Length > 1024 * 1024)
                throw new InvalidOperationException("Pipeline descriptor size is outside the bounded range.");
            var bytes = new byte[(int)stream.Length];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read <= 0) throw new EndOfStreamException("Pipeline descriptor ended before the frozen length.");
                offset += read;
            }
            return bytes;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null || left.Length != right.Length) return false;
            for (var i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        private static void DisposePortGuards(List<TcpListener> guards)
        {
            if (guards == null) return;
            foreach (var guard in guards)
            {
                try { guard.Stop(); }
                catch { }
            }
        }

        /// <summary>清理Pipeline server附带的autotick并读取私有状态，防止残留update callback。</summary>
        private static void DisableAndVerifyPipelineAutoTick(Assembly assembly)
        {
            var type = RequireType(assembly, AutoTickTypeName);
            var method = type.GetMethod("SetAutoTick", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool), typeof(int) }, null);
            if (method == null) throw new InvalidOperationException("Pipeline SetAutoTick API is unavailable.");
            var result = Convert.ToString(method.Invoke(null, new object[] { false, 16 }));
            if (!string.IsNullOrEmpty(result) && result.StartsWith("Error:", StringComparison.Ordinal))
                throw new InvalidOperationException("Pipeline auto-tick could not be disabled: " + result);
            var enabledField = type.GetField("s_Enabled", BindingFlags.NonPublic | BindingFlags.Static);
            var pumpField = type.GetField("s_Pump", BindingFlags.NonPublic | BindingFlags.Static);
            if (enabledField == null || pumpField == null || Convert.ToBoolean(enabledField.GetValue(null)) || pumpField.GetValue(null) != null)
                throw new InvalidOperationException("Pipeline auto-tick callback is still active after disable.");
        }

        /// <summary>读取磁盘重导后的全部manager，防止保存失败后错误写入handoff成功标记。</summary>
        private static void VerifyAutoStartDisabled(List<UnityEngine.Object> managers)
        {
            foreach (var manager in managers)
            {
                var serialized = new SerializedObject(manager);
                serialized.Update();
                var property = serialized.FindProperty("m_AutoStart");
                if (property == null || property.boolValue)
                    throw new InvalidOperationException("Pipeline Editor AutoStart did not persist as false.");
            }
        }

        private static int ReadIntProperty(object target, string name)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null) throw new InvalidOperationException("Pipeline property is unavailable: " + name + ".");
            return Convert.ToInt32(property.GetValue(target, null));
        }

        private static bool ReadBoolProperty(object target, string name)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null) throw new InvalidOperationException("Pipeline property is unavailable: " + name + ".");
            return Convert.ToBoolean(property.GetValue(target, null));
        }

        /// <summary>只创建固定的Assets设置目录，不写package或项目根临时目录。</summary>
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var separator = path.LastIndexOf('/');
            var parent = path.Substring(0, separator);
            var name = path.Substring(separator + 1);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static Type RequireType(Assembly assembly, string name)
        {
            var type = assembly.GetType(name, false);
            if (type == null) throw new InvalidOperationException("Pipeline API type is unavailable: " + name + ".");
            return type;
        }

        private static string ExceptionMessage(Exception exception)
        {
            var current = exception;
            while (current is TargetInvocationException && current.InnerException != null) current = current.InnerException;
            return current.GetType().Name + ": " + current.Message;
        }
    }
}
#endif
