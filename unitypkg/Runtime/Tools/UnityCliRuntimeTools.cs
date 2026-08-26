using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>批准的 development Player Runtime Tool 执行器；所有入口只由主线程 dispatcher 调用。</summary>
    internal static class UnityCliRuntimeTools
    {
        private const int MaxLogEntries = 1000;
        private const int MaxConsoleLimit = 200;
        private const int MaxMessageLength = 4096;
        private const int MaxStackTraceLength = 8192;
        private const int MaxUiDocuments = 32;
        private const int MaxUiElementsScanned = 4096;
        private const int MaxCaptureDimension = 4096;
        private const int MaxCapturePixels = 16 * 1024 * 1024;
        private const int MaxPngBytes = 32 * 1024 * 1024;
        private static readonly object LogSync = new object();
        private static readonly List<RuntimeLogEntry> LogEntries = new List<RuntimeLogEntry>();
        private static string runtimeSessionId;
        private static string buildId;
        private static string instanceId;
        private static long logSequence;
        private static int currentFrameCount;
        private static bool initialized;

        /// <summary>绑定一个Player session并注册有界log观察器。</summary>
        internal static void Initialize(string session, string build, string instance)
        {
            if (initialized) Shutdown();
            runtimeSessionId = session;
            buildId = build;
            instanceId = instance;
            lock (LogSync) { LogEntries.Clear(); logSequence = 0L; }
            currentFrameCount = Time.frameCount;
            Application.logMessageReceivedThreaded += OnLogMessage;
            initialized = true;
        }

        /// <summary>解除session观察器并清空所有进程内bounded state。</summary>
        internal static void Shutdown()
        {
            if (initialized) Application.logMessageReceivedThreaded -= OnLogMessage;
            initialized = false;
            lock (LogSync) { LogEntries.Clear(); logSequence = 0L; }
        }

        internal static void TickFrame(int frameCount) { currentFrameCount = frameCount; }

        /// <summary>按唯一 canonical name 分派；未批准名称绝不进入反射或任意代码路径。</summary>
        internal static object Execute(string name, Dictionary<string, object> input, bool dryRun)
        {
            if (!initialized) throw Error("E_RUNTIME_NOT_READY", "Runtime Tool executor is not initialized.");
            if (input == null) throw Error("E_SCHEMA_INVALID", "Runtime Tool input must be an object.");
            switch (name)
            {
                case "runtime.state": return RuntimeState(input);
                case "runtime.console": return RuntimeConsole(input);
                case "runtime.log": return RuntimeLog(input, dryRun);
                case "runtime.target-framerate.set": return SetTargetFramerate(input, dryRun);
                case "runtime.time-scale.set": return SetTimeScale(input, dryRun);
                case "runtime.input.key": return SimulateKey(input, dryRun);
                case "runtime.input.pointer": return SimulatePointer(input, dryRun);
                case "runtime.ui.capture-element": return CaptureElement(input, dryRun);
                default: throw Error("E_TOOL_NOT_FOUND", "Runtime Tool is not approved: " + name + ".");
            }
        }

        /// <summary>读取当前Player主线程可证明的bounded状态快照。</summary>
        private static object RuntimeState(Dictionary<string, object> input)
        {
            RequireOnly(input);
            var scene = SceneManager.GetActiveScene();
            return new Dictionary<string, object>
            {
                { "runtimeSessionId", runtimeSessionId }, { "buildId", buildId }, { "observedAt", UtcNow() },
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "productName", Bounded(Application.productName, 256) }, { "applicationVersion", Bounded(Application.version, 128) },
                { "scene", new Dictionary<string, object> { { "name", Bounded(scene.name, 256) }, { "buildIndex", (long)scene.buildIndex }, { "isLoaded", scene.isLoaded } } },
                { "frame", new Dictionary<string, object> { { "frameCount", (long)Time.frameCount }, { "realtimeSinceStartup", Finite(Time.realtimeSinceStartup) }, { "targetFrameRate", (long)Application.targetFrameRate } } },
                { "time", new Dictionary<string, object> { { "timeScale", Finite(Time.timeScale) }, { "time", Finite(Time.time) }, { "unscaledTime", Finite(Time.unscaledTime) } } },
                { "screen", new Dictionary<string, object> { { "width", (long)Screen.width }, { "height", (long)Screen.height }, { "dpi", Finite(Screen.dpi) }, { "fullScreen", Screen.fullScreen }, { "focused", Application.isFocused } } },
                { "memory", new Dictionary<string, object>
                    {
                        { "totalAllocatedBytes", Profiler.GetTotalAllocatedMemoryLong() },
                        { "totalReservedBytes", Profiler.GetTotalReservedMemoryLong() },
                        { "totalUnusedReservedBytes", Profiler.GetTotalUnusedReservedMemoryLong() }
                    }
                }
            };
        }

        /// <summary>按cursor、level和limit读取当前session的有界console窗口。</summary>
        private static object RuntimeConsole(Dictionary<string, object> input)
        {
            RequireOnly(input, "since", "limit", "level", "includeStackTrace");
            var since = OptionalInteger(input, "since", -1L);
            var limit = (int)OptionalInteger(input, "limit", 50L);
            var level = OptionalString(input, "level", "log");
            var includeStackTrace = OptionalBool(input, "includeStackTrace", false);
            if (since < -1L) throw Error("E_SCHEMA_INVALID", "runtime.console since must be -1 or a non-negative cursor.");
            if (limit < 1 || limit > MaxConsoleLimit) throw Error("E_SCHEMA_INVALID", "runtime.console limit must be between 1 and 200.");
            var minimum = Severity(level);
            List<RuntimeLogEntry> selected;
            long cursor;
            long droppedBefore;
            lock (LogSync)
            {
                cursor = logSequence;
                droppedBefore = LogEntries.Count == 0 ? cursor + 1L : LogEntries[0].Sequence;
                selected = LogEntries.Where(entry => entry.Sequence > since && entry.Severity >= minimum).ToList();
                if (selected.Count > limit) selected = selected.Skip(selected.Count - limit).ToList();
            }
            return new Dictionary<string, object>
            {
                { "runtimeSessionId", runtimeSessionId }, { "cursor", cursor }, { "droppedBeforeSequence", droppedBefore },
                { "count", (long)selected.Count }, { "entries", selected.Select(entry => LogEntryData(entry, includeStackTrace)).Cast<object>().ToList() }
            };
        }

        /// <summary>写入一条bounded日志，并通过同一session callback证明sequence。</summary>
        private static object RuntimeLog(Dictionary<string, object> input, bool dryRun)
        {
            RequireOnly(input, "message", "level");
            var message = RequiredString(input, "message", MaxMessageLength);
            if (string.IsNullOrWhiteSpace(message)) throw Error("E_SCHEMA_INVALID", "runtime.log message must not be empty.");
            var level = OptionalString(input, "level", "info");
            if (level != "info" && level != "warning" && level != "error") throw Error("E_SCHEMA_INVALID", "runtime.log level must be info, warning, or error.");
            long? sequence = null;
            if (!dryRun)
            {
                var before = CurrentSequence();
                if (level == "warning") Debug.LogWarning(message);
                else if (level == "error") Debug.LogError(message);
                else Debug.Log(message);
                sequence = FindObservedSequence(before, message, level == "info" ? "log" : level);
                if (!sequence.HasValue) throw Error("E_RUNTIME_LOG_UNPROVEN", "The Player log callback did not observe the emitted message.");
            }
            return new Dictionary<string, object>
            {
                { "runtimeSessionId", runtimeSessionId }, { "dryRun", dryRun }, { "logged", !dryRun },
                { "level", level }, { "message", message }, { "sequence", sequence.HasValue ? (object)sequence.Value : null },
                { "observedAt", UtcNow() }, { "plannedEffects", new List<object> { new Dictionary<string, object> { { "type", "runtime-log" }, { "level", level } } } }
            };
        }

        /// <summary>设置当前session帧率目标，并从Application执行fresh readback。</summary>
        private static object SetTargetFramerate(Dictionary<string, object> input, bool dryRun)
        {
            RequireOnly(input, "frameRate");
            var target = RequiredInteger(input, "frameRate");
            if (target < -1L || target > 1000L) throw Error("E_SCHEMA_INVALID", "runtime.target-framerate.set frameRate must be between -1 and 1000.");
            var before = Application.targetFrameRate;
            if (!dryRun && before != (int)target) Application.targetFrameRate = (int)target;
            var after = Application.targetFrameRate;
            if (!dryRun && after != (int)target) throw Error("E_RUNTIME_TARGET_FRAMERATE_UNPROVEN", "Application.targetFrameRate did not match the requested value after assignment.");
            return new Dictionary<string, object>
            {
                { "runtimeSessionId", runtimeSessionId }, { "dryRun", dryRun }, { "changed", before != (int)target },
                { "before", (long)before }, { "after", (long)after },
                { "plannedEffects", new List<object> { new Dictionary<string, object> { { "type", "runtime-target-framerate-set" }, { "frameRate", target } } } }
            };
        }

        /// <summary>设置当前session time scale，并从Time执行fresh readback。</summary>
        private static object SetTimeScale(Dictionary<string, object> input, bool dryRun)
        {
            RequireOnly(input, "scale");
            var target = RequiredFiniteNumber(input, "scale");
            if (target < 0d || target > 1000d) throw Error("E_SCHEMA_INVALID", "runtime.time-scale.set scale must be between 0 and 1000.");
            var expected = (float)target;
            var before = Time.timeScale;
            if (!dryRun && before != expected) Time.timeScale = expected;
            var after = Time.timeScale;
            if (!dryRun && after != expected) throw Error("E_RUNTIME_TIME_SCALE_UNPROVEN", "Time.timeScale did not match the requested value after assignment.");
            return new Dictionary<string, object>
            {
                { "runtimeSessionId", runtimeSessionId }, { "dryRun", dryRun }, { "changed", before != expected },
                { "before", Finite(before) }, { "after", Finite(after) },
                { "plannedEffects", new List<object> { new Dictionary<string, object> { { "type", "runtime-time-scale-set" }, { "scale", (double)expected } } } }
            };
        }

        /// <summary>向current keyboard注入一个批准的key action并返回flush后的状态序列。</summary>
        private static object SimulateKey(Dictionary<string, object> input, bool dryRun)
        {
            RequireOnly(input, "key", "event");
            var key = RequiredString(input, "key", 128);
            var eventName = OptionalString(input, "event", "press");
            if (eventName != "down" && eventName != "up" && eventName != "press") throw Error("E_SCHEMA_INVALID", "runtime.input.key event must be down, up, or press.");
            var facade = InputSystemFacade.Create();
            var control = facade.ResolveKey(key);
            var canonicalKey = facade.CanonicalKey(control);
            var path = facade.ControlPath(control);
            // actual调用先消费已排队的native状态，再冻结before并注入排在其后的full StateEvent。
            if (!dryRun) facade.Update();
            var before = facade.IsPressed(control);
            var observed = new List<object>();
            if (!dryRun)
            {
                if (eventName == "down" || eventName == "press") { facade.QueueButton(control, true); facade.Update(); observed.Add(facade.IsPressed(control)); }
                if (eventName == "up" || eventName == "press") { facade.QueueButton(control, false); facade.Update(); observed.Add(facade.IsPressed(control)); }
            }
            var after = facade.IsPressed(control);
            var expected = eventName == "down" ? true : eventName == "up" || eventName == "press" ? false : before;
            var phasesProven = eventName == "down"
                ? observed.Count == 1 && observed[0] is bool && (bool)observed[0]
                : eventName == "up"
                    ? observed.Count == 1 && observed[0] is bool && !(bool)observed[0]
                    : observed.Count == 2 && observed[0] is bool && (bool)observed[0] && observed[1] is bool && !(bool)observed[1];
            if (!dryRun && (after != expected || !phasesProven)) throw Error("E_RUNTIME_INPUT_UNPROVEN", "Keyboard pressed-state phases did not match the requested event after InputSystem.Update.");
            return new Dictionary<string, object>
            {
                { "runtimeSessionId", runtimeSessionId }, { "dryRun", dryRun }, { "simulated", !dryRun },
                { "key", canonicalKey }, { "event", eventName }, { "controlPath", path },
                { "beforePressed", before }, { "afterPressed", after }, { "observedPressedStates", observed },
                { "plannedEffects", new List<object> { new Dictionary<string, object> { { "type", "runtime-input-key" }, { "key", canonicalKey }, { "event", eventName } } } }
            };
        }

        /// <summary>向current mouse注入一个屏幕内pointer action并返回position/button readback。</summary>
        private static object SimulatePointer(Dictionary<string, object> input, bool dryRun)
        {
            RequireOnly(input, "x", "y", "event", "button");
            var x = RequiredFiniteNumber(input, "x");
            var y = RequiredFiniteNumber(input, "y");
            if (x < 0d || y < 0d || x >= Screen.width || y >= Screen.height) throw Error("E_SCHEMA_INVALID", "runtime.input.pointer coordinates must be inside the current Player screen.");
            var eventName = OptionalString(input, "event", "click");
            var button = OptionalString(input, "button", "left");
            if (eventName != "move" && eventName != "down" && eventName != "up" && eventName != "click") throw Error("E_SCHEMA_INVALID", "runtime.input.pointer event must be move, down, up, or click.");
            if (button != "left" && button != "right" && button != "middle") throw Error("E_SCHEMA_INVALID", "runtime.input.pointer button must be left, right, or middle.");
            var facade = InputSystemFacade.Create();
            var pointer = facade.ResolvePointer(button);
            // pointer与keyboard共享相同的native-event flush边界，避免读到将被同次Update覆盖的前态。
            if (!dryRun) facade.Update();
            var beforePosition = facade.ReadPosition(pointer.PositionControl);
            var beforePressed = facade.IsPressed(pointer.ButtonControl);
            var observed = new List<object>();
            var requestedPosition = new Vector2((float)x, (float)y);
            if (!dryRun)
            {
                facade.QueuePointer(pointer.PositionControl, eventName == "move" ? null : pointer.ButtonControl, requestedPosition, eventName == "down" || eventName == "click");
                facade.Update();
                if (eventName != "move") observed.Add(facade.IsPressed(pointer.ButtonControl));
                if (eventName == "click") { facade.QueueButton(pointer.ButtonControl, false); facade.Update(); observed.Add(facade.IsPressed(pointer.ButtonControl)); }
            }
            var afterPosition = facade.ReadPosition(pointer.PositionControl);
            var afterPressed = facade.IsPressed(pointer.ButtonControl);
            var expectedPressed = eventName == "down" ? true : eventName == "up" || eventName == "click" ? false : beforePressed;
            var phasesProven = eventName == "move"
                ? observed.Count == 0
                : eventName == "down"
                    ? observed.Count == 1 && observed[0] is bool && (bool)observed[0]
                    : eventName == "up"
                        ? observed.Count == 1 && observed[0] is bool && !(bool)observed[0]
                        : observed.Count == 2 && observed[0] is bool && (bool)observed[0] && observed[1] is bool && !(bool)observed[1];
            if (!dryRun && (afterPosition != requestedPosition || afterPressed != expectedPressed || !phasesProven)) throw Error("E_RUNTIME_INPUT_UNPROVEN", "Pointer position or pressed-state phases did not match the requested event after InputSystem.Update.");
            return new Dictionary<string, object>
            {
                { "runtimeSessionId", runtimeSessionId }, { "dryRun", dryRun }, { "simulated", !dryRun },
                { "event", eventName }, { "button", button }, { "controlPath", facade.ControlPath(pointer.ButtonControl) },
                { "beforePosition", PointData(beforePosition) }, { "afterPosition", PointData(afterPosition) },
                { "beforePressed", beforePressed }, { "afterPressed", afterPressed }, { "observedPressedStates", observed },
                { "plannedEffects", new List<object> { new Dictionary<string, object> { { "type", "runtime-input-pointer" }, { "x", x }, { "y", y }, { "event", eventName }, { "button", button } } } }
            };
        }

        /// <summary>解析一个bounded UI Toolkit selector并写入固定runtime artifact。</summary>
        private static object CaptureElement(Dictionary<string, object> input, bool dryRun)
        {
            RequireOnly(input, "selector", "panel");
            var selector = RequiredString(input, "selector", 256);
            if (string.IsNullOrWhiteSpace(selector)) throw Error("E_SCHEMA_INVALID", "runtime.ui.capture-element selector must not be empty.");
            var panelName = OptionalString(input, "panel", string.Empty);
#if !UNITY_6000_7_OR_NEWER
            throw Error("E_RUNTIME_ELEMENT_CAPTURE_UNAVAILABLE", "runtime.ui.capture-element requires Unity 6000.7 or newer.");
#else
            var panel = ResolvePanel(panelName);
            var element = ResolveElement(panel.Root, selector);
            var width = CheckedDimension(element.resolvedStyle.width, "width");
            var height = CheckedDimension(element.resolvedStyle.height, "height");
            if ((long)width * height > MaxCapturePixels) throw Error("E_RUNTIME_ELEMENT_CAPTURE_BOUNDS", "The selected runtime element exceeds the 16 megapixel capture limit.");
            var result = new Dictionary<string, object>
            {
                { "runtimeSessionId", runtimeSessionId }, { "dryRun", dryRun }, { "captured", !dryRun },
                { "panel", panel.Name }, { "selector", selector },
                { "element", new Dictionary<string, object> { { "name", element.name ?? string.Empty }, { "type", element.GetType().FullName }, { "width", (long)width }, { "height", (long)height } } },
                { "plannedEffects", new List<object> { new Dictionary<string, object> { { "type", "runtime-element-capture" }, { "artifactName", "runtime-element.png" } } } }
            };
            if (dryRun) return result;
            var artifact = WriteCaptureArtifact(element);
            result["artifact"] = artifact;
            return result;
#endif
        }

#if UNITY_6000_7_OR_NEWER
        private sealed class RuntimePanel { internal string Name; internal VisualElement Root; }

        private static RuntimePanel ResolvePanel(string requested)
        {
            var documents = UnityEngine.Object.FindObjectsOfType<UIDocument>(true).Take(MaxUiDocuments + 1).ToArray();
            if (documents.Length > MaxUiDocuments) throw Error("E_RUNTIME_PANEL_BOUNDS", "More than 32 runtime UIDocuments are active.");
            var panels = documents.Where(document => document != null && document.rootVisualElement != null && document.rootVisualElement.panel != null)
                .Select(document => new RuntimePanel { Name = document.panelSettings != null && !string.IsNullOrEmpty(document.panelSettings.name) ? document.panelSettings.name : document.gameObject.name, Root = document.rootVisualElement }).ToList();
            if (!string.IsNullOrEmpty(requested)) panels = panels.Where(panel => string.Equals(panel.Name, requested, StringComparison.Ordinal)).ToList();
            if (panels.Count == 0) throw Error("E_RUNTIME_PANEL_NOT_FOUND", "No attached runtime UI Toolkit panel matches the request.");
            if (panels.Count > 1) throw Error("E_RUNTIME_PANEL_AMBIGUOUS", "The runtime panel request matches more than one UIDocument.");
            return panels[0];
        }

        private static VisualElement ResolveElement(VisualElement root, string selector)
        {
            var matches = new List<VisualElement>();
            var stack = new Stack<VisualElement>();
            stack.Push(root);
            var scanned = 0;
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                scanned++;
                if (scanned > MaxUiElementsScanned) throw Error("E_RUNTIME_ELEMENT_SELECTOR_BOUNDS", "Runtime UI selector scan exceeds 4096 elements.");
                if (MatchesSelector(current, selector)) { matches.Add(current); if (matches.Count > 1) break; }
                for (var index = current.hierarchy.childCount - 1; index >= 0; index--) stack.Push(current.hierarchy[index]);
            }
            if (matches.Count == 0) throw Error("E_RUNTIME_ELEMENT_NOT_FOUND", "Runtime UI element selector did not match an attached element.");
            if (matches.Count > 1) throw Error("E_RUNTIME_ELEMENT_AMBIGUOUS", "Runtime UI element selector matched more than one element.");
            return matches[0];
        }

        private static bool MatchesSelector(VisualElement element, string selector)
        {
            if (selector[0] == '#') return selector.Length > 1 && string.Equals(element.name, selector.Substring(1), StringComparison.Ordinal);
            if (selector[0] == '.') return selector.Length > 1 && element.ClassListContains(selector.Substring(1));
            return string.Equals(element.GetType().Name, selector, StringComparison.Ordinal) || string.Equals(element.GetType().FullName, selector, StringComparison.Ordinal);
        }

        private static Dictionary<string, object> WriteCaptureArtifact(VisualElement element)
        {
            RenderTexture texture = null;
            Texture2D readable = null;
            var previous = RenderTexture.active;
            byte[] png;
            try
            {
                texture = element.CaptureToRenderTexture();
                if (texture == null) throw Error("E_RUNTIME_ELEMENT_CAPTURE_FAILED", "Unity did not return a capture texture.");
                RenderTexture.active = texture;
                readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                readable.Apply(false, false);
                png = readable.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                if (readable != null) UnityEngine.Object.Destroy(readable);
                if (texture != null) UnityEngine.Object.Destroy(texture);
            }
            if (png == null || png.Length == 0 || png.Length > MaxPngBytes) throw Error("E_RUNTIME_ELEMENT_CAPTURE_BOUNDS", "Runtime element PNG is empty or exceeds 32 MiB.");
            var directory = RuntimeArtifactDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "runtime-element.png");
            var temporary = path + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, png);
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            var bytes = File.ReadAllBytes(path);
            return new Dictionary<string, object> { { "path", path }, { "size", (long)bytes.Length }, { "sha256", Sha256(bytes) }, { "mimeType", "image/png" } };
        }
#endif

        private static void OnLogMessage(string message, string stackTrace, LogType type)
        {
            var severity = type == LogType.Error || type == LogType.Exception || type == LogType.Assert ? 2 : type == LogType.Warning ? 1 : 0;
            var level = severity == 2 ? "error" : severity == 1 ? "warning" : "log";
            lock (LogSync)
            {
                logSequence++;
                LogEntries.Add(new RuntimeLogEntry
                {
                    Sequence = logSequence, ObservedAt = UtcNow(), FrameCount = currentFrameCount, Severity = severity,
                    Level = level, Message = Bounded(message, MaxMessageLength), StackTrace = Bounded(stackTrace, MaxStackTraceLength)
                });
                if (LogEntries.Count > MaxLogEntries) LogEntries.RemoveRange(0, LogEntries.Count - MaxLogEntries);
            }
        }

        private static Dictionary<string, object> LogEntryData(RuntimeLogEntry entry, bool includeStackTrace)
        {
            return new Dictionary<string, object>
            {
                { "sequence", entry.Sequence }, { "observedAt", entry.ObservedAt }, { "frameCount", (long)entry.FrameCount },
                { "level", entry.Level }, { "message", entry.Message }, { "stackTrace", includeStackTrace ? entry.StackTrace : string.Empty }
            };
        }

        private static long CurrentSequence() { lock (LogSync) return logSequence; }

        private static long? FindObservedSequence(long after, string message, string level)
        {
            lock (LogSync)
            {
                var entry = LogEntries.LastOrDefault(candidate => candidate.Sequence > after && candidate.Level == level && candidate.Message == message);
                return entry == null ? (long?)null : entry.Sequence;
            }
        }

        private static int Severity(string level)
        {
            if (level == "log") return 0;
            if (level == "warning") return 1;
            if (level == "error") return 2;
            throw Error("E_SCHEMA_INVALID", "runtime.console level must be log, warning, or error.");
        }

        private static void RequireOnly(Dictionary<string, object> input, params string[] names)
        {
            var allowed = new HashSet<string>(names, StringComparer.Ordinal);
            foreach (var key in input.Keys) if (!allowed.Contains(key)) throw Error("E_SCHEMA_INVALID", "Runtime Tool input contains an unknown field: " + key + ".");
        }

        private static string RequiredString(Dictionary<string, object> input, string key, int maximum)
        {
            object value;
            var text = input.TryGetValue(key, out value) ? value as string : null;
            if (text == null || text.Length > maximum) throw Error("E_SCHEMA_INVALID", key + " must be a string no longer than " + maximum.ToString(CultureInfo.InvariantCulture) + " characters.");
            return text;
        }

        private static string OptionalString(Dictionary<string, object> input, string key, string fallback)
        {
            object value;
            if (!input.TryGetValue(key, out value)) return fallback;
            var text = value as string;
            if (text == null) throw Error("E_SCHEMA_INVALID", key + " must be a string.");
            return text;
        }

        private static bool OptionalBool(Dictionary<string, object> input, string key, bool fallback)
        {
            object value;
            if (!input.TryGetValue(key, out value)) return fallback;
            if (value is bool) return (bool)value;
            throw Error("E_SCHEMA_INVALID", key + " must be a boolean.");
        }

        private static long RequiredInteger(Dictionary<string, object> input, string key)
        {
            if (!input.ContainsKey(key)) throw Error("E_SCHEMA_INVALID", key + " is required.");
            return OptionalInteger(input, key, 0L);
        }

        private static double RequiredFiniteNumber(Dictionary<string, object> input, string key)
        {
            object value;
            if (!input.TryGetValue(key, out value)) throw Error("E_SCHEMA_INVALID", key + " is required.");
            double number;
            if (value is long) number = (long)value;
            else if (value is double) number = (double)value;
            else throw Error("E_SCHEMA_INVALID", key + " must be a number.");
            if (double.IsNaN(number) || double.IsInfinity(number)) throw Error("E_SCHEMA_INVALID", key + " must be finite.");
            return number;
        }

        private static long OptionalInteger(Dictionary<string, object> input, string key, long fallback)
        {
            object value;
            if (!input.TryGetValue(key, out value)) return fallback;
            if (value is long) return (long)value;
            if (value is double)
            {
                var number = (double)value;
                if (!double.IsNaN(number) && !double.IsInfinity(number) && Math.Floor(number) == number && number >= long.MinValue && number <= long.MaxValue) return (long)number;
            }
            throw Error("E_SCHEMA_INVALID", key + " must be an integer.");
        }

        private static Dictionary<string, object> PointData(Vector2 value) { return new Dictionary<string, object> { { "x", Finite(value.x) }, { "y", Finite(value.y) } }; }

        private static int CheckedDimension(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw Error("E_RUNTIME_ELEMENT_CAPTURE_BOUNDS", "Runtime element " + name + " is not finite.");
            var rounded = Mathf.CeilToInt(value);
            if (rounded < 1 || rounded > MaxCaptureDimension) throw Error("E_RUNTIME_ELEMENT_CAPTURE_BOUNDS", "Runtime element " + name + " must be between 1 and 4096 pixels.");
            return rounded;
        }

        private static double Finite(float value) { return float.IsNaN(value) || float.IsInfinity(value) ? 0d : value; }
        private static string UtcNow() { return DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture); }
        private static string Bounded(string value, int maximum) { value = value ?? string.Empty; return value.Length <= maximum ? value : value.Substring(0, maximum); }
        private static string RuntimeArtifactDirectory()
        {
            var configured = Environment.GetEnvironmentVariable("UNITYCLI_DATA_DIR");
            var data = !string.IsNullOrWhiteSpace(configured) ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UnityCli");
            return Path.Combine(data, ".temp", "artifacts", "runtime-" + instanceId.Substring("unity-".Length));
        }
        private static string Sha256(byte[] bytes) { using (var sha = SHA256.Create()) { var hash = sha.ComputeHash(bytes); var result = new StringBuilder(hash.Length * 2); foreach (var item in hash) result.Append(item.ToString("x2")); return result.ToString(); } }
        private static UnityCliRuntimeToolException Error(string code, string message) { return new UnityCliRuntimeToolException(code, message); }

        /// <summary>固定绑定 Input System 已知 API；用户输入只选择 enum/control，不参与类型或方法解析。</summary>
        private sealed class InputSystemFacade
        {
            private readonly Type keyType;
            private readonly Type inputEventPtrType;
            private readonly object tempAllocator;
            private readonly object keyboard;
            private readonly object mouse;
            private readonly MethodInfo stateEventFromMethod;
            private readonly MethodInfo writeValueMethod;
            private readonly MethodInfo queueEventMethod;
            private readonly MethodInfo updateMethod;
            private readonly PropertyInfo currentTimeProperty;
            private readonly PropertyInfo eventTimeProperty;
            private readonly Dictionary<object, string> canonicalKeys = new Dictionary<object, string>();

            private InputSystemFacade(Type keyType, Type inputEventPtrType, object tempAllocator, object keyboard, object mouse, MethodInfo stateEventFromMethod, MethodInfo writeValueMethod, MethodInfo queueEventMethod, MethodInfo updateMethod, PropertyInfo currentTimeProperty, PropertyInfo eventTimeProperty)
            {
                this.keyType = keyType;
                this.inputEventPtrType = inputEventPtrType;
                this.tempAllocator = tempAllocator;
                this.keyboard = keyboard;
                this.mouse = mouse;
                this.stateEventFromMethod = stateEventFromMethod;
                this.writeValueMethod = writeValueMethod;
                this.queueEventMethod = queueEventMethod;
                this.updateMethod = updateMethod;
                this.currentTimeProperty = currentTimeProperty;
                this.eventTimeProperty = eventTimeProperty;
            }

            internal static InputSystemFacade Create()
            {
                var inputSystemType = Type.GetType("UnityEngine.InputSystem.InputSystem, Unity.InputSystem", false);
                var keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem", false);
                var mouseType = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem", false);
                var keyType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem", false);
                var stateEventType = Type.GetType("UnityEngine.InputSystem.LowLevel.StateEvent, Unity.InputSystem", false);
                var inputEventPtrType = Type.GetType("UnityEngine.InputSystem.LowLevel.InputEventPtr, Unity.InputSystem", false);
                var inputStateType = Type.GetType("UnityEngine.InputSystem.LowLevel.InputState, Unity.InputSystem", false);
                var inputControlExtensionsType = Type.GetType("UnityEngine.InputSystem.InputControlExtensions, Unity.InputSystem", false);
                if (inputSystemType == null || keyboardType == null || mouseType == null || keyType == null || stateEventType == null || inputEventPtrType == null || inputStateType == null || inputControlExtensionsType == null) throw Error("E_RUNTIME_INPUT_SYSTEM_UNAVAILABLE", "com.unity.inputsystem is not loaded in this development Player.");
                var from = stateEventType.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(method => method.Name == "From" && method.GetParameters().Length == 3 && method.GetParameters()[1].IsOut && method.GetParameters()[1].ParameterType.GetElementType() == inputEventPtrType);
                var write = inputControlExtensionsType.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(method => method.Name == "WriteValueIntoEvent" && method.IsGenericMethodDefinition && method.GetParameters().Length == 3 && method.GetParameters()[0].ParameterType.FullName == "UnityEngine.InputSystem.InputControl" && method.GetParameters()[2].ParameterType == inputEventPtrType);
                var queue = inputSystemType.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(method => method.Name == "QueueEvent" && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == inputEventPtrType);
                var update = inputSystemType.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(method => method.Name == "Update" && method.GetParameters().Length == 0);
                var currentTime = inputStateType.GetProperty("currentTime", BindingFlags.Public | BindingFlags.Static);
                var eventTime = inputEventPtrType.GetProperty("time", BindingFlags.Public | BindingFlags.Instance);
                var allocatorType = from == null ? null : from.GetParameters()[2].ParameterType;
                if (from == null || write == null || queue == null || update == null || currentTime == null || currentTime.PropertyType != typeof(double)
                    || eventTime == null || eventTime.PropertyType != typeof(double) || !eventTime.CanWrite || allocatorType == null || !allocatorType.IsEnum)
                    throw Error("E_RUNTIME_INPUT_SYSTEM_UNAVAILABLE", "The loaded Input System does not expose the required timestamped state-event API.");
                var keyboard = ReadStaticCurrent(keyboardType);
                var mouse = ReadStaticCurrent(mouseType);
                return new InputSystemFacade(keyType, inputEventPtrType, Enum.Parse(allocatorType, "Temp"), keyboard, mouse, from, write, queue, update, currentTime, eventTime);
            }

            internal object ResolveKey(string requested)
            {
                if (keyboard == null) throw Error("E_RUNTIME_INPUT_DEVICE_UNAVAILABLE", "No current Input System keyboard is present.");
                object key;
                try { key = Enum.Parse(keyType, requested, true); }
                catch { throw Error("E_SCHEMA_INVALID", "Unknown Input System key: " + requested + "."); }
                var canonical = Enum.GetName(keyType, key);
                if (string.IsNullOrEmpty(canonical) || canonical == "None") throw Error("E_SCHEMA_INVALID", "runtime.input.key requires a concrete Input System key.");
                var indexer = keyboard.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(property => property.Name == "Item" && property.GetIndexParameters().Length == 1 && property.GetIndexParameters()[0].ParameterType == keyType);
                var control = indexer == null ? null : indexer.GetValue(keyboard, new[] { key });
                if (control == null) throw Error("E_RUNTIME_INPUT_DEVICE_UNAVAILABLE", "The current keyboard does not expose the requested key control.");
                canonicalKeys[control] = canonical;
                return control;
            }

            internal string CanonicalKey(object control) { string value; return canonicalKeys.TryGetValue(control, out value) ? value : string.Empty; }

            internal ResolvedPointer ResolvePointer(string button)
            {
                if (mouse == null) throw Error("E_RUNTIME_INPUT_DEVICE_UNAVAILABLE", "No current Input System mouse is present.");
                var position = ReadProperty(mouse, "position");
                var buttonControl = ReadProperty(mouse, button + "Button");
                if (position == null || buttonControl == null) throw Error("E_RUNTIME_INPUT_DEVICE_UNAVAILABLE", "The current mouse does not expose the requested controls.");
                return new ResolvedPointer { PositionControl = position, ButtonControl = buttonControl };
            }

            internal bool IsPressed(object control)
            {
                var property = control.GetType().GetProperty("isPressed", BindingFlags.Public | BindingFlags.Instance);
                if (property == null || property.PropertyType != typeof(bool)) throw Error("E_RUNTIME_INPUT_SYSTEM_UNAVAILABLE", "Input System button readback API is unavailable.");
                return (bool)property.GetValue(control, null);
            }

            internal Vector2 ReadPosition(object control)
            {
                var method = control.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(candidate => candidate.Name == "ReadValue" && candidate.GetParameters().Length == 0 && candidate.ReturnType == typeof(Vector2));
                if (method == null) throw Error("E_RUNTIME_INPUT_SYSTEM_UNAVAILABLE", "Input System pointer position readback API is unavailable.");
                return (Vector2)method.Invoke(control, null);
            }

            internal string ControlPath(object control)
            {
                var property = control.GetType().GetProperty("path", BindingFlags.Public | BindingFlags.Instance);
                var path = property == null ? null : Convert.ToString(property.GetValue(control, null), CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(path)) throw Error("E_RUNTIME_INPUT_UNPROVEN", "Input System control path readback is unavailable.");
                return path;
            }

            internal void QueueButton(object control, bool pressed)
            {
                QueueState(control, eventPtr => WriteValue(control, pressed ? (object)1f : 0f, eventPtr));
            }

            internal void QueuePointer(object positionControl, object buttonControl, Vector2 position, bool pressed)
            {
                QueueState(positionControl, eventPtr =>
                {
                    WriteValue(positionControl, position, eventPtr);
                    if (buttonControl != null) WriteValue(buttonControl, pressed ? (object)1f : 0f, eventPtr);
                });
            }

            internal void Update() { Invoke(updateMethod, null, null); }

            private void QueueState(object control, Action<object> write)
            {
                var device = ReadProperty(control, "device");
                if (device == null) throw Error("E_RUNTIME_INPUT_DEVICE_UNAVAILABLE", "The selected Input System control is detached from its device.");
                var arguments = new[] { device, (object)null, tempAllocator };
                var allocation = Invoke(stateEventFromMethod, null, arguments) as IDisposable;
                var eventPtr = arguments[1];
                if (allocation == null || eventPtr == null) throw Error("E_RUNTIME_INPUT_EXECUTION_FAILED", "Input System could not allocate a bounded state event.");
                try
                {
                    // full StateEvent必须使用Input System当前时钟，保证本次注入排在已排队硬件状态之后。
                    var currentTime = Convert.ToDouble(currentTimeProperty.GetValue(null, null), CultureInfo.InvariantCulture);
                    eventTimeProperty.SetValue(eventPtr, currentTime, null);
                    write(eventPtr);
                    Invoke(queueEventMethod, null, new[] { eventPtr });
                }
                finally { allocation.Dispose(); }
            }

            private void WriteValue(object control, object value, object eventPtr)
            {
                MethodInfo method;
                try { method = writeValueMethod.MakeGenericMethod(value.GetType()); }
                catch { throw Error("E_RUNTIME_INPUT_SYSTEM_UNAVAILABLE", "Input System control write API could not bind the requested value."); }
                Invoke(method, null, new[] { control, value, eventPtr });
            }

            private static object ReadStaticCurrent(Type type)
            {
                var property = type.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                return property == null ? null : property.GetValue(null, null);
            }

            private static object ReadProperty(object target, string name)
            {
                var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                return property == null ? null : property.GetValue(target, null);
            }

            private static object Invoke(MethodInfo method, object target, object[] args)
            {
                try { return method.Invoke(target, args); }
                catch (TargetInvocationException) { throw Error("E_RUNTIME_INPUT_EXECUTION_FAILED", "Input System rejected the queued runtime event."); }
                catch (Exception) { throw Error("E_RUNTIME_INPUT_EXECUTION_FAILED", "Input System invocation failed before readback."); }
            }

            internal sealed class ResolvedPointer { internal object PositionControl; internal object ButtonControl; }
        }

        private sealed class RuntimeLogEntry
        {
            internal long Sequence;
            internal string ObservedAt;
            internal int FrameCount;
            internal int Severity;
            internal string Level;
            internal string Message;
            internal string StackTrace;
        }
    }

    internal sealed class UnityCliRuntimeToolException : Exception
    {
        internal string Code { get; private set; }
        internal UnityCliRuntimeToolException(string code, string message) : base(message) { Code = code; }
    }
}
