using System;
using System.Collections.Generic;
using Process = System.Diagnostics.Process;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;

namespace UnityCli
{
    /// <summary>Development Player 的v3 registry publication；只写当前runtime session并保留可验证外部claim。</summary>
    internal sealed class UnityCliRuntimeRegistry : IDisposable
    {
        private const int MutexTimeoutMs = 5000;
        private const uint MutexAllAccess = 0x001F0001;
        private const uint WaitObject0 = 0x00000000;
        private const uint WaitAbandoned = 0x00000080;
        private const uint WaitTimeout = 0x00000102;
        private const uint TokenQuery = 0x0008;
        private const int TokenUser = 1;
        private const int ErrorInsufficientBuffer = 122;
        private readonly UnityCliRuntimeIdentity identity;
        private readonly string sessionId;
        private readonly string listenerId;
        private readonly int preferredPort;
        private readonly int actualPort;
        private readonly string buildStamp;
        private readonly string buildId;
        private readonly string executablePath;
        private readonly DateTime startedAt;
        private long publicationSequence;
        private bool disposed;

        internal UnityCliRuntimeRegistry(UnityCliRuntimeIdentity identity, string sessionId, string listenerId, int preferredPort, int actualPort, string buildStamp, string buildId)
        {
            this.identity = identity;
            this.sessionId = sessionId;
            this.listenerId = listenerId;
            this.preferredPort = preferredPort;
            this.actualPort = actualPort;
            this.buildStamp = buildStamp;
            this.buildId = buildId;
            this.startedAt = DateTime.UtcNow;
            this.executablePath = SafeExecutablePath();
            if (string.IsNullOrWhiteSpace(this.executablePath)) throw new InvalidOperationException("Runtime Player executable path is unavailable.");
        }

        /// <summary>发布低频ready snapshot；实时main-thread pulse只保留在内存health中。</summary>
        internal void PublishReady()
        {
            ThrowIfDisposed();
            WithRegistryMutex(delegate
            {
                var record = ReadRecord();
                var sessions = ReadSessions(record);
                var previous = sessions.FirstOrDefault(IsCurrent);
                var nextSequence = Interlocked.Increment(ref publicationSequence);
                if (IsStalePublication(previous, nextSequence)) return;
                sessions = sessions.Where(IsRetainedForeignClaim).ToList();
                sessions.Add(CurrentClaim(nextSequence));
                WriteRecord(ReadLong(record, "revision") + 1L, sessions);
            });
        }

        /// <summary>Player退出仅移除当前session，项目identity offline record仍保留。</summary>
        internal void RemoveCurrentClaim()
        {
            if (disposed) return;
            try
            {
                WithRegistryMutex(delegate
                {
                    var record = ReadRecord();
                    var sessions = ReadSessions(record).Where(delegate(Dictionary<string, object> claim) { return !IsCurrent(claim) && IsClaimAliveOrUnverifiable(claim); }).ToList();
                    WriteRecord(ReadLong(record, "revision") + 1L, sessions);
                });
            }
            catch (Exception exception) { Debug.LogWarning("UnityCli runtime registry cleanup failed: " + exception.GetType().Name); }
        }

        public void Dispose() { if (disposed) return; RemoveCurrentClaim(); disposed = true; }

        private Dictionary<string, object> CurrentClaim(long sequence)
        {
            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            return new Dictionary<string, object>
            {
                { "target", "runtime" },
                { "sessionId", sessionId },
                { "runtimeSessionId", sessionId },
                { "processId", (long)Process.GetCurrentProcess().Id },
                { "processStartedAt", ProcessStartedAt(Process.GetCurrentProcess()) },
                { "publishedAt", now },
                { "publicationSequence", sequence },
                { "lifecycle", "ready" },
                { "lastUnitySnapshot", new Dictionary<string, object>
                    {
                        { "schemaVersion", 1L }, { "observedAt", now }, { "sourceRuntimeSessionId", sessionId }, { "sourceListenerGeneration", 1L },
                        { "fields", new Dictionary<string, object>
                            {
                                { "developmentBuild", KnownValue(Debug.isDebugBuild) },
                                { "application", KnownValue(new Dictionary<string, object>
                                    {
                                        { "isFocused", Application.isFocused }, { "platform", Application.platform.ToString() },
                                        { "productName", Application.productName }, { "version", Application.version }
                                    })
                                },
                                { "frame", KnownValue(new Dictionary<string, object>
                                    {
                                        { "frameCount", (long)Time.frameCount }, { "targetFrameRate", (long)Application.targetFrameRate }, { "timeScale", (double)Time.timeScale }
                                    })
                                }
                            }
                        }
                    }
                },
                { "listener", new Dictionary<string, object>
                    {
                        { "listenerId", listenerId }, { "generation", 1L }, { "startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture) },
                        { "endpoint", new Dictionary<string, object>
                            {
                                { "host", "127.0.0.1" }, { "preferredPort", (long)preferredPort }, { "actualPort", (long)actualPort }, { "fallback", actualPort != preferredPort },
                                { "url", "http://127.0.0.1:" + actualPort.ToString(CultureInfo.InvariantCulture) }
                            }
                        }
                    }
                },
                { "progress", new List<object>() }, { "blockers", new List<object>() },
                { "unityVersion", Application.unityVersion }, { "buildStamp", buildStamp },
                { "toolCatalogFingerprint", "player-runtime-executor" }, { "executablePath", executablePath }, { "buildId", buildId }
            };
        }

        private Dictionary<string, object> ReadRecord()
        {
            var path = InstancePath();
            if (!File.Exists(path)) return new Dictionary<string, object>();
            var record = UnityCliRuntimeJson.Object(UnityCliRuntimeJson.Parse(File.ReadAllText(path, Encoding.UTF8)), "Instance registry is not a JSON object.");
            var identityRecord = ReadObject(record, "identity");
            if (ReadLong(record, "version") != 3L) return new Dictionary<string, object>();
            if (identityRecord == null
                || !EqualsString(identityRecord, "instanceId", identity.InstanceId)
                || !EqualsString(identityRecord, "canonicalProjectPath", identity.CanonicalProjectPath))
                throw new InvalidDataException("Instance registry identity does not match runtime sidecar.");
            return record;
        }

        private void WriteRecord(long revision, List<Dictionary<string, object>> sessions)
        {
            var content = new Dictionary<string, object>
            {
                { "version", 3L }, { "revision", revision }, { "canonicalizationVersion", 1L },
                { "identity", new Dictionary<string, object>
                    {
                        { "instanceId", identity.InstanceId }, { "canonicalProjectPath", identity.CanonicalProjectPath }, { "displayProjectPath", identity.DisplayProjectPath }
                    }
                },
                { "sessions", sessions.Cast<object>().ToList() }, { "publishedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) }
            };
            WriteAtomically(InstancePath(), UnityCliRuntimeJson.Serialize(content) + "\n");
        }

        private static List<Dictionary<string, object>> ReadSessions(Dictionary<string, object> record)
        {
            object raw;
            var list = record != null && record.TryGetValue("sessions", out raw) ? raw as List<object> : null;
            return (list ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
        }

        private bool IsCurrent(Dictionary<string, object> claim)
        {
            return claim != null && string.Equals(ReadString(claim, "sessionId"), sessionId, StringComparison.Ordinal);
        }

        private bool IsRetainedForeignClaim(Dictionary<string, object> claim) { return !IsCurrent(claim) && IsClaimAliveOrUnverifiable(claim); }

        /// <summary>同runtime session仅接受更大的publication sequence，防止延迟writer覆盖新snapshot。</summary>
        private static bool IsStalePublication(Dictionary<string, object> previous, long sequence)
        {
            if (previous == null) return false;
            var listener = ReadObject(previous, "listener");
            var generation = ReadLong(listener, "generation");
            return generation > 1L || (generation == 1L && ReadLong(previous, "publicationSequence") >= sequence);
        }

        private static bool IsClaimAliveOrUnverifiable(Dictionary<string, object> claim)
        {
            if (claim == null) return false;
            var processId = (int)ReadLong(claim, "processId");
            var expectedStart = ReadString(claim, "processStartedAt");
            if (processId <= 0 || string.IsNullOrEmpty(expectedStart)) return true;
            try { using (var process = Process.GetProcessById(processId)) return string.Equals(ProcessStartedAt(process), expectedStart, StringComparison.Ordinal); }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch { return true; }
        }

        private static string DataDirectory()
        {
            var configured = Environment.GetEnvironmentVariable("UNITYCLI_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) throw new InvalidOperationException("LOCALAPPDATA is required for UnityCLI runtime registry state.");
            return Path.Combine(local, "UnityCli");
        }
        private string InstancePath() { return Path.Combine(DataDirectory(), "instances", identity.InstanceId + ".json"); }

        private static void WriteAtomically(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) { writer.Write(content); writer.Flush(); stream.Flush(true); }
                for (var attempt = 0; ; attempt++)
                {
                    try { if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); break; }
                    catch (IOException) { if (attempt >= 4) throw; Thread.Sleep(10 * (attempt + 1)); }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private void WithRegistryMutex(Action action)
        {
            var sid = CurrentUserSidString();
            IntPtr descriptor = IntPtr.Zero; uint size;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;" + sid + ")", 1, out descriptor, out size)) throw new InvalidOperationException("Could not build current-user registry mutex DACL.");
            IntPtr handle = IntPtr.Zero; var acquired = false;
            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), SecurityDescriptor = descriptor, InheritHandle = false };
                handle = CreateMutexExW(ref attributes, "Local\\UnityCli.Registry." + identity.InstanceId, 0, MutexAllAccess);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1)) throw new InvalidOperationException("Could not create current-user registry mutex.");
                var wait = WaitForSingleObject(handle, MutexTimeoutMs);
                if (wait == WaitTimeout) throw new TimeoutException("Timed out waiting for current-user registry mutex.");
                if (wait != WaitObject0 && wait != WaitAbandoned) throw new InvalidOperationException("Could not acquire current-user registry mutex.");
                acquired = true; action();
            }
            finally
            {
                if (acquired) ReleaseMutex(handle);
                if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
                if (descriptor != IntPtr.Zero) LocalFree(descriptor);
            }
        }

        private static string CurrentUserSidString()
        {
            IntPtr token = IntPtr.Zero, buffer = IntPtr.Zero, text = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) throw new InvalidOperationException("Could not open process token.");
                uint required; GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out required);
                if (required == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) throw new InvalidOperationException("Could not read token user size.");
                buffer = Marshal.AllocHGlobal((int)required);
                if (!GetTokenInformation(token, TokenUser, buffer, required, out required)) throw new InvalidOperationException("Could not read token user.");
                if (!ConvertSidToStringSidW(Marshal.ReadIntPtr(buffer), out text)) throw new InvalidOperationException("Could not format token user.");
                return Marshal.PtrToStringUni(text);
            }
            finally { if (text != IntPtr.Zero) LocalFree(text); if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); if (token != IntPtr.Zero) CloseHandle(token); }
        }

        private static Dictionary<string, object> KnownValue(object value) { return new Dictionary<string, object> { { "known", true }, { "value", value } }; }
        private static Dictionary<string, object> ReadObject(Dictionary<string, object> value, string key) { object result; return value != null && value.TryGetValue(key, out result) ? result as Dictionary<string, object> : null; }
        private static string ReadString(Dictionary<string, object> value, string key) { object result; return value != null && value.TryGetValue(key, out result) ? result as string : null; }
        private static long ReadLong(Dictionary<string, object> value, string key) { object result; return value != null && value.TryGetValue(key, out result) && result != null ? Convert.ToInt64(result, CultureInfo.InvariantCulture) : 0L; }
        private static bool EqualsString(Dictionary<string, object> value, string key, string expected) { return string.Equals(ReadString(value, key), expected, StringComparison.Ordinal); }
        private static string ProcessStartedAt(Process process) { return process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture); }
        private static string SafeExecutablePath() { try { return Process.GetCurrentProcess().MainModule.FileName; } catch { return string.Empty; } }
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException("UnityCliRuntimeRegistry"); }

        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr SecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
        [DllImport("kernel32.dll", EntryPoint = "GetCurrentProcess")] private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr handle, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(IntPtr token, int type, IntPtr buffer, uint length, out uint returned);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string value, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateMutexExW(ref SecurityAttributes attributes, string name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ReleaseMutex(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr value);
    }

    internal sealed class UnityCliRuntimeIdentity
    {
        internal string InstanceId;
        internal string CanonicalProjectPath;
        internal string DisplayProjectPath;
    }
}
