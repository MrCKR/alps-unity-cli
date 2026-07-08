#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterDiagnoseCommands(BridgeCommandRegistry registry)
        {
            // diagnose.preflight 已迁移为 IUnityCliTool(unitypkg/Editor/Tools/diagnose/preflight.cs),不再用 RegisterCommand。
            // 旧 DiagnosePreflightJson/CompileStatusJson/DiagnoseCheckJson 保留为 dead code,留给后续清理。
        }

        private static string DiagnosePreflightJson(BridgeRequest request)
        {
            DiscoverProjectTools();
            DiscoverProjectContexts();
            return new UnityCliJsonWriter().BeginObject()
                .Prop("command", "diagnose.preflight")
                .Prop("targetCommand", request.StringArg("command"))
                .PropObject("project", w =>
                {
                    w.Prop("path", ProjectRoot())
                        .Prop("unityVersion", Application.unityVersion)
                        .Prop("activeScene", SceneManager.GetActiveScene().path);
                })
                .PropRaw("editor", EditorStateJson())
                .PropObject("bridge", w =>
                {
                    w.Prop("port", (long)port)
                        .Prop("logPath", LogPath())
                        .PropRaw("capabilities", CapabilitiesJson());
                })
                .PropObject("customTools", w =>
                {
                    w.Prop("fingerprint", ProjectToolFingerprintSummaryJson());
                })
                .PropObject("customContexts", w =>
                {
                    w.PropRaw("fingerprint", ProjectContextFingerprintSummaryJson());
                })
                .PropArray("checks", arr =>
                {
                    arr.RawValue(DiagnoseCheckJson("unityNotBusy", !(EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)));
                    arr.RawValue(DiagnoseCheckJson("sceneLoaded", SceneManager.GetActiveScene().isLoaded));
                    arr.RawValue(DiagnoseCheckJson("projectToolFingerprint", !ProjectTools.Values.Any(definition => definition.FingerprintStatus == "blocked")));
                    arr.RawValue(DiagnoseCheckJson("projectContextFingerprint", !ProjectContexts.Values.Any(definition => definition.FingerprintStatus == "blocked")));
                    arr.RawValue(CompileStatusJson());
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString();
        }

        // 编译状态检查：isCompiling + 近期编译错误（通过反射 LogEntries）。
        // 扩展编译失败会让 Unity 持续无法完成编译、Bridge 报 E_UNITY_COMPILING，这里把状态和错误明示出来，
        // 让 doctor 能定位是“扩展编译失败”而非“bridge 不可达”。
        private static string CompileStatusJson()
        {
            var compiling = EditorApplication.isCompiling;
            var hasCompileErrors = false;
            var errors = new List<string>();
            try
            {
                var logEntriesType = FindType("UnityEditor.LogEntries") ?? FindType("UnityEditorInternal.LogEntries");
                if (logEntriesType != null)
                {
                    var start = logEntriesType.GetMethod("GetEntryCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    var getEntry = logEntriesType.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    var getLines = logEntriesType.GetMethod("GetLinesAndModeInternal", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (start != null)
                    {
                        var total = (int)start.Invoke(null, Type.EmptyTypes);
                        var inspected = 0;
                        for (var i = total - 1; i >= 0 && inspected < 60; i--, inspected++)
                        {
                            string line = null;
                            int mode = 0;
                            try
                            {
                                if (getEntry != null)
                                {
                                    var args = new object[4] { i, null, null, null };
                                    getEntry.Invoke(null, args);
                                    line = args[1] as string;
                                    mode = args[2] as int? ?? 0;
                                }
                            }
                            catch { }
                            if (line == null && getLines != null)
                            {
                                try
                                {
                                    var args = new object[] { i, null };
                                    getLines.Invoke(null, args);
                                    line = args[1] as string;
                                }
                                catch { }
                            }
                            if (string.IsNullOrEmpty(line)) continue;
                            // 仅关注编译错误（CS####）。mode 位掩码中 bit1 为 error。
                            var isCompileError = line.IndexOf("CS", StringComparison.OrdinalIgnoreCase) >= 0 && line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (isCompileError || ((mode & 1) != 0 && line.IndexOf("Compile", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                hasCompileErrors = true;
                                errors.Add(line);
                                if (errors.Count >= 20) break;
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                errors.Insert(0, "Failed to read compile errors: " + exception.GetType().Name);
            }
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("name", "compileStatus");
            w.Prop("ok", !compiling && !hasCompileErrors);
            w.Prop("compiling", compiling);
            w.Prop("hasCompileErrors", hasCompileErrors);
            w.PropArray("compileErrors", arr => { foreach (var e in errors) arr.Value(e); });
            w.PropArray("nextActions", arr =>
            {
                if (hasCompileErrors) arr.Value("Review the listed compile errors (often from a broken extension under Packages/com.unity_cli/Extensions). Fix or remove the offending extension, then let Unity recompile.");
                if (compiling) arr.Value("Wait for Unity to finish compiling, then re-run doctor.");
                if (hasCompileErrors && File.Exists(Path.Combine(ProjectRoot(), "Packages", "com.unity_cli", "Extensions", "extension.json")))
                {
                    arr.Value("If an extension no longer compiles, remove it via unity_cli extensions remove <id> --project <path>.");
                }
            });
            return w.EndObject().ToString();
        }


        private static string DiagnoseCheckJson(string name, bool ok)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", name)
                .Prop("ok", ok)
                .EndObject().ToString();
        }
    }
}
#endif
