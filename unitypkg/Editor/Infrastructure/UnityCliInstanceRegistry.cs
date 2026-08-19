#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private const int RegistryMutexTimeoutMs = 5000;
        private const uint MutexAllAccess = 0x001F0001;
        private const uint WaitObject0 = 0x00000000;
        private const uint WaitAbandoned = 0x00000080;
        private const uint WaitTimeout = 0x00000102;
        private const uint WaitFailed = 0xFFFFFFFF;
        private const uint SecurityDescriptorRevision = 1;
        private const uint TokenQuery = 0x0008;
        private const int TokenUser = 1;
        private const int ErrorInsufficientBuffer = 122;
        private static CanonicalProjectPathResult canonicalIdentity;
        private static string editorSessionId;
        private static string lastRegistrationSignature;
        private static long listenerGeneration;
        private static long publicationSequence;

        /// <summary>当前 listener generation 只用于 transport attestation 与 registry 内部一致性校验。</summary>
        private static long CurrentListenerGeneration()
        {
            return listenerGeneration;
        }

        /// <summary>返回共享 CanonicalPath v1 解析结果；失败时禁止 listener/registry 继续启动。</summary>
        private static CanonicalProjectPathResult ProjectIdentity()
        {
            if (canonicalIdentity != null) return canonicalIdentity;
            canonicalIdentity = UnityCliCanonicalProjectPath.Resolve(Application.dataPath);
            if (!canonicalIdentity.Success)
                throw new InvalidOperationException(canonicalIdentity.ErrorCode + ": " + canonicalIdentity.ErrorMessage);
            return canonicalIdentity;
        }

        private static string ProjectRoot()
        {
            return ProjectIdentity().DisplayProjectPath;
        }

        private static string InstanceId()
        {
            return ProjectIdentity().InstanceId;
        }

        private static string ProjectHash()
        {
            return ProjectIdentity().IdentityHash;
        }

        /// <summary>Unity.exe 生命周期 identity；domain reload 不改变，进程重启必然改变。</summary>
        internal static string EditorSessionId()
        {
            if (!string.IsNullOrEmpty(editorSessionId)) return editorSessionId;
            var process = Process.GetCurrentProcess();
            var seed = InstanceId() + "\0" + process.Id.ToString(CultureInfo.InvariantCulture) + "\0" + ProcessStartedAt(process);
            editorSessionId = "session-" + Sha256Utf8(seed);
            return editorSessionId;
        }

        private static string ProcessStartedAt(Process process = null)
        {
            return (process ?? Process.GetCurrentProcess()).StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        private static string DataDir()
        {
            var configured = Environment.GetEnvironmentVariable("UNITY_CLI_DATA_DIR");
            if (!string.IsNullOrEmpty(configured)) return configured;
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(localAppData)) throw new InvalidOperationException("LOCALAPPDATA is required for Unity CLI registry state.");
            return Path.Combine(localAppData, "UnityCli");
        }

        private static string InstancesDir()
        {
            return Path.Combine(DataDir(), "instances");
        }

        private static string InstancePath()
        {
            return Path.Combine(InstancesDir(), InstanceId() + ".json");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
        }

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
        private static extern IntPtr GetCurrentProcessHandle();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, uint tokenInformationLength, out uint returnLength);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
            string stringSecurityDescriptor,
            uint stringSDRevision,
            out IntPtr securityDescriptor,
            out uint securityDescriptorSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateMutexExW(
            ref SecurityAttributes mutexAttributes,
            string name,
            uint flags,
            uint desiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReleaseMutex(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        /// <summary>从 process token 读取当前 Windows user SID；避免 Unity Mono 未实现的 WindowsIdentity.User。</summary>
        private static string CurrentUserSidString()
        {
            IntPtr token = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;
            IntPtr sidText = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcessHandle(), TokenQuery, out token))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the current process token.");
                uint required;
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out required);
                if (required == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not determine the current token user size.");
                buffer = Marshal.AllocHGlobal((int)required);
                if (!GetTokenInformation(token, TokenUser, buffer, required, out required))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the current token user.");
                var sid = Marshal.ReadIntPtr(buffer);
                if (sid == IntPtr.Zero || !ConvertSidToStringSidW(sid, out sidText))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not format the current user SID.");
                return Marshal.PtrToStringUni(sidText);
            }
            finally
            {
                if (sidText != IntPtr.Zero) LocalFree(sidText);
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        /// <summary>在 native current-user DACL named mutex 内执行临界区；abandoned 表示已取得所有权。</summary>
        private static void WithCurrentUserMutex(string name, int timeoutMs, Action action)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Mutex name is required.", "name");
            if (action == null) throw new ArgumentNullException("action");
            var sddl = "D:P(A;;GA;;;" + CurrentUserSidString() + ")";
            IntPtr descriptor;
            uint descriptorSize;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SecurityDescriptorRevision, out descriptor, out descriptorSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not build the current-user mutex DACL.");

            IntPtr handle = IntPtr.Zero;
            var acquired = false;
            try
            {
                var attributes = new SecurityAttributes
                {
                    Length = Marshal.SizeOf(typeof(SecurityAttributes)),
                    SecurityDescriptor = descriptor,
                    InheritHandle = false
                };
                handle = CreateMutexExW(ref attributes, name, 0, MutexAllAccess);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the current-user mutex.");

                var wait = WaitForSingleObject(handle, (uint)timeoutMs);
                if (wait == WaitTimeout) throw new TimeoutException("Timed out waiting for current-user mutex.");
                if (wait == WaitFailed) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not wait for current-user mutex.");
                if (wait != WaitObject0 && wait != WaitAbandoned) throw new InvalidOperationException("Unexpected current-user mutex wait result: " + wait.ToString(CultureInfo.InvariantCulture));
                acquired = true;
                action();
            }
            finally
            {
                if (acquired && !ReleaseMutex(handle)) WriteLog("current_user_mutex_release_failed win32=" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
                if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            }
        }

        /// <summary>保留 registry 原有锁名、超时和调用语义。</summary>
        private static void WithRegistryMutex(Action action)
        {
            WithCurrentUserMutex("Local\\UnityCli.Registry." + InstanceId(), RegistryMutexTimeoutMs, action);
        }

        /// <summary>固定temp完整写入并flush后原子替换；并发reader只会看到完整旧版或新版。</summary>
        private static void WriteRegistryTextAtomically(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporaryPath = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(content);
                    writer.Flush();
                    stream.Flush(true);
                }
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                        else File.Move(temporaryPath, path);
                        break;
                    }
                    catch (IOException)
                    {
                        if (attempt >= 4) throw;
                        Thread.Sleep(10 * (attempt + 1));
                    }
                }
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        /// <summary>写入或更新当前 editor session claim；同 identity 的其他存活 claim 必须保留以形成 conflict。</summary>
        private static bool RegisterInstance(string requestedState)
        {
            // AutoStart=false不即时停当前domain，但compile/reload边界开始后不得再把旧listener发布成transitioning。
            if (!UnityCliProjectSettings.Current.BridgeAutoStart
                && (domainReloading || EditorApplication.isCompiling || EditorApplication.isUpdating)) return true;
            try
            {
                WithRegistryMutex(() =>
                {
                    var registry = ReadCurrentRegistry();
                    var sessions = ReadSessions(registry);
                    var previous = sessions.FirstOrDefault(IsCurrentSession);
                    EnsureListenerGeneration(registry);
                    var nextSequence = Interlocked.Increment(ref publicationSequence);
                    if (IsStalePublication(previous, listenerGeneration, nextSequence))
                    {
                        WriteLog("instance_publication_skipped_stale session=" + EditorSessionId() + " generation=" + listenerGeneration.ToString(CultureInfo.InvariantCulture) + " sequence=" + nextSequence.ToString(CultureInfo.InvariantCulture));
                        return;
                    }
                    sessions = sessions.Where(IsRetainedForeignClaim).ToList();
                    sessions.Add(CurrentSessionData(NormalizeSessionLifecycle(requestedState), nextSequence));
                    var revision = ReadLong(registry, "revision") + 1L;
                    var data = RegistryData(revision, sessions);
                    WriteRegistryTextAtomically(InstancePath(), UnityCliSerializer.Serialize(data) + "\n");
                });
                // Registry是低频publication，不是heartbeat；日志只记录生命周期或listener变化。
                var registrationSignature = NormalizeSessionLifecycle(requestedState) + "|" + ListenerId + "|" + listenerGeneration.ToString(CultureInfo.InvariantCulture);
                if (!string.Equals(registrationSignature, lastRegistrationSignature, StringComparison.Ordinal))
                {
                    lastRegistrationSignature = registrationSignature;
                    WriteLog("instance_registered id=" + InstanceId() + " session=" + EditorSessionId() + " lifecycle=" + NormalizeSessionLifecycle(requestedState) + " listenerId=" + ListenerId + " generation=" + listenerGeneration.ToString(CultureInfo.InvariantCulture));
                }
                return true;
            }
            catch (Exception exception)
            {
                WriteLog("instance_register_failed " + exception);
                return false;
            }
        }

        /// <summary>Editor 退出只移除当前 session claim，保留 offline project identity 记录。</summary>
        private static void DeleteInstanceRegistration()
        {
            try
            {
                WithRegistryMutex(() =>
                {
                    var registry = ReadCurrentRegistry();
                    var sessions = ReadSessions(registry).Where(session => !IsCurrentListenerSession(session)).Where(IsProcessClaimAliveOrUnverifiable).ToList();
                    var data = RegistryData(ReadLong(registry, "revision") + 1L, sessions);
                    WriteRegistryTextAtomically(InstancePath(), UnityCliSerializer.Serialize(data) + "\n");
                });
                WriteLog("instance_session_removed id=" + InstanceId() + " session=" + EditorSessionId());
            }
            catch (Exception exception)
            {
                WriteLog("instance_unregister_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        /// <summary>只消费当前v3；旧机制状态不迁移，下一次publication直接建立当前snapshot。</summary>
        private static Dictionary<string, object> ReadCurrentRegistry()
        {
            if (!File.Exists(InstancePath())) return new Dictionary<string, object>();
            var parsed = UnityCliSerializer.Parse(File.ReadAllText(InstancePath(), Encoding.UTF8)) as Dictionary<string, object>;
            if (parsed == null) throw new InvalidDataException("Instance registry is not a JSON object.");
            if (ReadLong(parsed, "version") != 3L) return new Dictionary<string, object>();
            var identity = ReadDictionary(parsed, "identity");
            if (identity == null
                || !string.Equals(ReadString(identity, "instanceId"), InstanceId(), StringComparison.Ordinal)
                || !string.Equals(ReadString(identity, "canonicalProjectPath"), ProjectIdentity().CanonicalProjectPath, StringComparison.Ordinal))
                throw new InvalidDataException("Instance registry identity does not match CanonicalPath v1.");
            return parsed;
        }

        /// <summary>读取当前 session 上一次 listener 代次；domain reload 后严格递增。</summary>
        private static void EnsureListenerGeneration(Dictionary<string, object> registry)
        {
            if (listenerGeneration > 0) return;
            var previous = ReadSessions(registry).FirstOrDefault(IsCurrentSession);
            var listener = ReadDictionary(previous, "listener");
            listenerGeneration = Math.Max(1L, ReadLong(listener, "generation") + 1L);
        }

        /// <summary>供 deterministic port 选择读取本 session 的 previous actual port。</summary>
        private static int? PreviousActualPort()
        {
            try
            {
                int? result = null;
                WithRegistryMutex(() =>
                {
                    var previous = ReadSessions(ReadCurrentRegistry()).FirstOrDefault(IsCurrentSession);
                    var endpoint = ReadDictionary(ReadDictionary(previous, "listener"), "endpoint");
                    var value = ReadLong(endpoint, "actualPort");
                    if (value >= 1024 && value <= 65535) result = (int)value;
                });
                return result;
            }
            catch (Exception exception)
            {
                WriteLog("instance_previous_port_failed " + exception.GetType().Name + ": " + exception.Message);
                return null;
            }
        }

        private static Dictionary<string, object> RegistryData(long revision, List<Dictionary<string, object>> sessions)
        {
            return new Dictionary<string, object>
            {
                { "version", 3L },
                { "revision", revision },
                { "canonicalizationVersion", (long)UnityCliCanonicalProjectPath.CanonicalizationVersion },
                { "identity", new Dictionary<string, object>
                    {
                        { "instanceId", InstanceId() },
                        { "canonicalProjectPath", ProjectIdentity().CanonicalProjectPath },
                        { "displayProjectPath", ProjectIdentity().DisplayProjectPath }
                    }
                },
                { "sessions", sessions.Cast<object>().ToList() },
                { "publishedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }
            };
        }

        private static Dictionary<string, object> CurrentSessionData(string lifecycle, long sequence)
        {
            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var session = new Dictionary<string, object>
            {
                { "target", "editor" },
                { "sessionId", EditorSessionId() },
                { "editorSessionId", EditorSessionId() },
                { "processId", (long)Process.GetCurrentProcess().Id },
                { "processStartedAt", ProcessStartedAt() },
                { "publishedAt", now },
                { "publicationSequence", sequence },
                { "lifecycle", lifecycle },
                { "lastUnitySnapshot", SampleEditorStateSnapshot(now) },
                { "listener", ListenerData(now) },
                { "progress", SnapshotProgress() },
                { "blockers", new List<object>() },
                { "unityVersion", Application.unityVersion },
                { "buildStamp", BridgeBuildStamp() },
                { "toolCatalogFingerprint", runtimeToolCatalogFingerprint ?? string.Empty }
            };
            return session;
        }

        private static Dictionary<string, object> ListenerData(string now)
        {
            var preferred = PreferredPort();
            return new Dictionary<string, object>
            {
                { "listenerId", ListenerId },
                { "generation", listenerGeneration },
                { "startedAt", BridgeLoadedAtUtc.ToString("O", CultureInfo.InvariantCulture) },
                { "endpoint", new Dictionary<string, object>
                    {
                        { "host", "127.0.0.1" },
                        { "preferredPort", (long)preferred },
                        { "actualPort", (long)port },
                        { "fallback", port != preferred },
                        { "url", "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) }
                    }
                }
            };
        }

        private static string NormalizeSessionLifecycle(string requested)
        {
            if (string.Equals(requested, "blocked", StringComparison.Ordinal)) return "blocked";
            if (domainReloading) return "domain-reloading";
            if (EditorApplication.isCompiling) return "compiling";
            if (EditorApplication.isUpdating) return "importing";
            if (BuildPipeline.isBuildingPlayer) return "building";
            if (IsPlayModeChanging()) return "playmode-changing";
            if (string.Equals(requested, "transitioning", StringComparison.Ordinal)) return "transitioning";
            return "ready";
        }

        /// <summary>mutex内按listener generation和publication sequence拒绝旧domain反向覆盖。</summary>
        private static bool IsStalePublication(Dictionary<string, object> previous, long generation, long sequence)
        {
            if (previous == null) return false;
            var listener = ReadDictionary(previous, "listener");
            var previousGeneration = ReadLong(listener, "generation");
            var previousSequence = ReadLong(previous, "publicationSequence");
            return previousGeneration > generation || (previousGeneration == generation && previousSequence >= sequence);
        }

        /// <summary>当前claim由新publication替换；其他claim只有OS证明进程生命周期失效才移除。</summary>
        private static bool IsRetainedForeignClaim(Dictionary<string, object> session)
        {
            return !IsCurrentSession(session) && IsProcessClaimAliveOrUnverifiable(session);
        }

        private static bool IsCurrentSession(Dictionary<string, object> session)
        {
            return session != null
                && string.Equals(ReadString(session, "target"), "editor", StringComparison.Ordinal)
                && string.Equals(ReadString(session, "sessionId"), EditorSessionId(), StringComparison.Ordinal);
        }

        private static bool IsCurrentListenerSession(Dictionary<string, object> session)
        {
            var listener = ReadDictionary(session, "listener");
            return IsCurrentSession(session)
                && string.Equals(ReadString(listener, "listenerId"), ListenerId, StringComparison.Ordinal)
                && ReadLong(listener, "generation") == listenerGeneration;
        }

        private static bool IsProcessClaimAliveOrUnverifiable(Dictionary<string, object> session)
        {
            if (session == null) return false;
            var pid = (int)ReadLong(session, "processId");
            var expectedStart = ReadString(session, "processStartedAt");
            if (pid <= 0 || string.IsNullOrEmpty(expectedStart)) return true;
            try
            {
                using (var process = Process.GetProcessById(pid))
                {
                    return string.Equals(ProcessStartedAt(process), expectedStart, StringComparison.Ordinal);
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Exception) { return true; }
        }

        private static List<Dictionary<string, object>> ReadSessions(Dictionary<string, object> registry)
        {
            object value;
            var raw = registry != null && registry.TryGetValue("sessions", out value) ? value as List<object> : null;
            return (raw ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
        }

        private static Dictionary<string, object> ReadDictionary(Dictionary<string, object> value, string key)
        {
            object raw;
            return value != null && value.TryGetValue(key, out raw) ? raw as Dictionary<string, object> : null;
        }

        private static string ReadString(Dictionary<string, object> value, string key)
        {
            object raw;
            return value != null && value.TryGetValue(key, out raw) ? raw as string : null;
        }

        private static long ReadLong(Dictionary<string, object> value, string key)
        {
            object raw;
            if (value == null || !value.TryGetValue(key, out raw) || raw == null) return 0L;
            try { return Convert.ToInt64(raw, CultureInfo.InvariantCulture); }
            catch { return 0L; }
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

        private static Dictionary<string, object> TargetContextData()
        {
            return new Dictionary<string, object>
            {
                { "id", InstanceId() },
                { "canonicalProjectPath", ProjectIdentity().CanonicalProjectPath },
                { "displayProjectPath", ProjectIdentity().DisplayProjectPath },
                { "editorSessionId", EditorSessionId() }
            };
        }

        private static Dictionary<string, object> EditorStateSummaryData()
        {
            return new Dictionary<string, object> { { "busy", EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer || domainReloading }, { "compiling", EditorApplication.isCompiling }, { "updating", EditorApplication.isUpdating }, { "building", BuildPipeline.isBuildingPlayer }, { "domainReloading", domainReloading } };
        }

        private static Dictionary<string, object> PlayModeStateData()
        {
            return new Dictionary<string, object> { { "isPlaying", EditorApplication.isPlaying }, { "isPaused", EditorApplication.isPaused }, { "isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode } };
        }
    }
}
#endif
