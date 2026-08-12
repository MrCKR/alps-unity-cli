#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>选定Editor process的native main-window冻结、focus effect和foreground proof owner。</summary>
    internal static class EditorWindowFocusToolSupport
    {
        internal sealed class Plan
        {
            internal IntPtr Window;
            internal int ProcessId;
            internal string Title;
            internal bool WasMinimized;
            internal object HostView;
            internal System.Reflection.MethodInfo HostFocus;
        }

        internal static Plan Prepare()
        {
#if !UNITY_EDITOR_WIN
            throw new UnityCliBridge.BridgeCommandException("E_EDITOR_FOCUS_UNAVAILABLE", "editor.focus is available only on Windows.", "Run this Tool from a Windows Unity Editor instance.");
#else
            var processId = Process.GetCurrentProcess().Id;
            var windows = TopLevelWindows(processId);
            if (windows.Count == 0)
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_FOCUS_WINDOW_NOT_FOUND", "No visible top-level window belongs to the selected Unity Editor process.", "Open the selected Editor main window and retry.");
            windows.Sort((left, right) => Score(right).CompareTo(Score(left)));
            var target = windows[0];
            object host;
            System.Reflection.MethodInfo focus;
            ResolveHostView(out host, out focus);
            return new Plan { Window = target.Handle, ProcessId = processId, Title = target.Title, WasMinimized = IsIconic(target.Handle), HostView = host, HostFocus = focus };
#endif
        }

        /// <summary>调用方必须先跨barrier；所有effect严格作用于冻结的current-process HWND。</summary>
        internal static void Apply(Plan plan)
        {
#if !UNITY_EDITOR_WIN
            throw new UnityCliBridge.BridgeCommandException("E_EDITOR_FOCUS_UNAVAILABLE", "editor.focus is available only on Windows.");
#else
            if (plan == null || plan.Window == IntPtr.Zero || !WindowBelongsToProcess(plan.Window, plan.ProcessId))
                throw new UnityCliBridge.BridgeCommandException("E_EDITOR_FOCUS_TARGET_CHANGED", "The frozen Unity Editor window no longer belongs to the selected process.", "Inspect editor.state and retry with the current instance.");
            var foreground = GetForegroundWindow();
            uint foregroundProcess;
            var foregroundThread = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, out foregroundProcess);
            var currentThread = GetCurrentThreadId();
            var attached = foregroundThread != 0 && foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
            try
            {
                if (plan.WasMinimized) ShowWindowAsync(plan.Window, 9);
                try { plan.HostFocus?.Invoke(plan.HostView, null); } catch { }
                BringWindowToTop(plan.Window);
                SetForegroundWindow(plan.Window);
                SetFocus(plan.Window);
            }
            finally
            {
                if (attached) AttachThreadInput(currentThread, foregroundThread, false);
            }
#endif
        }

        internal static bool IsForeground(Plan plan)
        {
#if UNITY_EDITOR_WIN
            return plan != null && plan.Window != IntPtr.Zero && GetForegroundWindow() == plan.Window && WindowBelongsToProcess(plan.Window, plan.ProcessId);
#else
            return false;
#endif
        }

        internal static EditorFocusWindowState Snapshot(Plan plan)
        {
            return new EditorFocusWindowState
            {
                ProcessId = plan == null ? 0 : plan.ProcessId,
                WindowHandle = plan == null ? null : "0x" + plan.Window.ToInt64().ToString("x"),
                Title = plan == null ? null : plan.Title,
                Foreground = IsForeground(plan),
#if UNITY_EDITOR_WIN
                Minimized = plan != null && plan.Window != IntPtr.Zero && IsIconic(plan.Window)
#else
                Minimized = false
#endif
            };
        }

#if UNITY_EDITOR_WIN
        private sealed class WindowInfo { internal IntPtr Handle; internal string Title; }
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);

        private static List<WindowInfo> TopLevelWindows(int processId)
        {
            var result = new List<WindowInfo>();
            EnumWindows((window, _) =>
            {
                uint ownerProcess;
                GetWindowThreadProcessId(window, out ownerProcess);
                if (ownerProcess != processId || !IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero) return true;
                var length = Math.Min(4096, Math.Max(0, GetWindowTextLength(window)));
                var title = new StringBuilder(length + 1);
                GetWindowText(window, title, title.Capacity);
                result.Add(new WindowInfo { Handle = window, Title = title.ToString() });
                return true;
            }, IntPtr.Zero);
            return result;
        }

        private static int Score(WindowInfo value)
        {
            if (value == null) return int.MinValue;
            var score = string.IsNullOrEmpty(value.Title) ? 0 : 10;
            if (!string.IsNullOrEmpty(value.Title) && value.Title.IndexOf("Unity", StringComparison.OrdinalIgnoreCase) >= 0) score += 100;
            if (!IsIconic(value.Handle)) score += 5;
            return score;
        }

        private static bool WindowBelongsToProcess(IntPtr window, int processId)
        {
            uint actual;
            GetWindowThreadProcessId(window, out actual);
            return actual == processId && IsWindowVisible(window);
        }

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool value);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
#endif

        /// <summary>HostView只作为激活辅助；terminal仍以Win32 exact HWND为准。</summary>
        private static void ResolveHostView(out object host, out System.Reflection.MethodInfo focus)
        {
            host = null;
            focus = null;
            try
            {
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                var assembly = typeof(EditorWindow).Assembly;
                var containerType = assembly.GetType("UnityEditor.ContainerWindow");
                var hostType = assembly.GetType("UnityEditor.HostView");
                var guiType = assembly.GetType("UnityEditor.GUIView");
                var isMain = containerType?.GetMethod("IsMainWindow", flags);
                focus = guiType?.GetMethod("Focus", flags, null, Type.EmptyTypes, null);
                if (containerType == null || hostType == null || isMain == null || focus == null) return;
                object main = null;
                foreach (var window in Resources.FindObjectsOfTypeAll(containerType))
                    if ((bool)isMain.Invoke(window, null)) { main = window; break; }
                var root = containerType.GetProperty("rootView", flags)?.GetValue(main, null) ?? containerType.GetField("m_RootView", flags)?.GetValue(main);
                if (root == null) return;
                if (hostType.IsInstanceOfType(root)) { host = root; return; }
                var children = root.GetType().GetProperty("allChildren", flags)?.GetValue(root, null) as Array;
                if (children == null) return;
                foreach (var child in children)
                    if (child != null && hostType.IsInstanceOfType(child) && child.GetType().Name == "DockArea") { host = child; return; }
                foreach (var child in children)
                    if (child != null && hostType.IsInstanceOfType(child)) { host = child; return; }
            }
            catch
            {
                host = null;
                focus = null;
            }
        }
    }

    public sealed class EditorFocusWindowState
    {
        public long ProcessId { get; set; }
        public string WindowHandle { get; set; }
        public string Title { get; set; }
        public bool Foreground { get; set; }
        public bool Minimized { get; set; }
    }
}
#endif
