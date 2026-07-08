#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// diagnose.preflight — 聚合 Unity Editor 健康诊断。
        /// 迁移自旧 RegisterCommand(DiagnoseCommands.DiagnosePreflightJson)为 IUnityCliTool 自包含。
        ///
        /// 作为 UnityCliBridge 的嵌套类实现:diagnose 聚合多个 Bridge 内部 private helper
        /// (EditorStateJson/CapabilitiesJson/DiscoverProjectTools/DiscoverProjectContexts/
        /// ProjectToolFingerprintSummaryJson/ProjectContextFingerprintSummaryJson/port/LogPath/
        /// ProjectTools/ProjectContexts/ProjectRoot/FindType)。嵌套类可直接访问外层 private 成员,
        /// 无需改动任何 helper 的可见性。
        ///
        /// 这些 helper 返回 raw JSON 字符串,而 IUnityCliTool 的 Result 是强类型 DTO(由 UnityCliSerializer
        /// 反射序列化,string 字段会被转义而非当嵌套对象)。因此用私有 ParseJsonToTree 把 JSON 字符串
        /// 解析成 object 树(Dictionary/List/标量),放入 object 类型字段——UnityCliSerializer 会把
        /// Dictionary 序列化为嵌套对象、List 序列化为数组,从而保留原有输出结构。
        /// </summary>
        public sealed class DiagnosePreflightTool : IUnityCliTool<DiagnosePreflightTool.Request, DiagnosePreflightTool.Result>
        {
            public sealed class Request
            {
                public string Command { get; set; }
            }

            public sealed class Result
            {
                public string Command { get; set; }
                public string TargetCommand { get; set; }
                public ProjectInfo Project { get; set; }
                // Editor/Capabilities/Fingerprint 为 raw JSON 解析出的 object 树(运行时为 Dictionary),序列化为嵌套对象
                public object Editor { get; set; }
                public BridgeInfo Bridge { get; set; }
                public CustomFingerprintInfo CustomTools { get; set; }
                public CustomFingerprintInfo CustomContexts { get; set; }
                public List<object> Checks { get; set; }
                public string LogPath { get; set; }
            }

            public sealed class ProjectInfo
            {
                public string Path { get; set; }
                public string UnityVersion { get; set; }
                public string ActiveScene { get; set; }
            }

            public sealed class BridgeInfo
            {
                public long Port { get; set; }
                public string LogPath { get; set; }
                public object Capabilities { get; set; }
            }

            public sealed class CustomFingerprintInfo
            {
                public object Fingerprint { get; set; }
            }

            public ToolDescriptor Describe() => new()
            {
                Name = "diagnose.preflight",
                Summary = "Run a live Unity Editor health check for project path, editor busy state, bridge capabilities, custom tool/context fingerprints, and recoverable checks.",
                Description = "Run a live Unity Editor health check for project path, editor busy state, bridge capabilities, custom tool/context fingerprints, and recoverable checks. Use this before live bridge work when Unity may be compiling, importing, or using stale custom tool/context metadata.",
                Group = "diagnose", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported",
                DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
                Examples = new[]
                {
                    new ToolExample { Description = "Run live preflight", Input = new Request { } },
                    new ToolExample { Description = "Preflight before component work", Input = new Request { Command = "component.add" } }
                }
            };

            public Result Execute(Request request, UnityCliProjectToolContext ctx)
            {
                // 触发项目自定义工具/上下文发现,确保 fingerprint 摘要反映最新注册状态
                DiscoverProjectTools();
                DiscoverProjectContexts();

                var scene = SceneManager.GetActiveScene();
                var checks = new List<object>
                {
                    MakeCheck("unityNotBusy", !(EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer)),
                    MakeCheck("sceneLoaded", scene.isLoaded),
                    MakeCheck("projectToolFingerprint", !ProjectTools.Values.Any(definition => definition.FingerprintStatus == "blocked")),
                    MakeCheck("projectContextFingerprint", !ProjectContexts.Values.Any(definition => definition.FingerprintStatus == "blocked")),
                    ParseJsonToTree(CompileStatusJson())
                };

                return new Result
                {
                    Command = "diagnose.preflight",
                    TargetCommand = request.Command,
                    Project = new ProjectInfo
                    {
                        Path = ProjectRoot(),
                        UnityVersion = Application.unityVersion,
                        ActiveScene = scene.path
                    },
                    Editor = ParseJsonToTree(EditorStateJson()),
                    Bridge = new BridgeInfo
                    {
                        Port = (long)port,
                        LogPath = LogPath(),
                        Capabilities = ParseJsonToTree(CapabilitiesJson())
                    },
                    CustomTools = new CustomFingerprintInfo { Fingerprint = ParseJsonToTree(ProjectToolFingerprintSummaryJson()) },
                    CustomContexts = new CustomFingerprintInfo { Fingerprint = ParseJsonToTree(ProjectContextFingerprintSummaryJson()) },
                    Checks = checks,
                    LogPath = LogPath()
                };
            }

            // 构造 {name, ok} 检查项(运行时 Dictionary,UnityCliSerializer 序列化为对象)
            private static Dictionary<string, object> MakeCheck(string name, bool ok)
            {
                return new Dictionary<string, object> { { "name", name }, { "ok", ok } };
            }

            // 编译状态检查:isCompiling + 近期编译错误(通过反射 LogEntries)。原样搬迁自 DiagnoseCommands。
            // 扩展编译失败会让 Unity 持续无法完成编译、Bridge 报 E_UNITY_COMPILING,这里把状态和错误明示出来,
            // 让 doctor 能定位是"扩展编译失败"而非"bridge 不可达"。
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
                                // 仅关注编译错误(CS####)。mode 位掩码中 bit1 为 error
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

            // ── 轻量 JSON → object 树解析器 ──
            // 把 Bridge 内部 helper 返回的 raw JSON 字符串解析成 object 树,供 UnityCliSerializer
            // 按对象/数组序列化嵌入 Result。返回 string/long/double/bool/Dictionary<string,object>/List<object>/null。
            private static object ParseJsonToTree(string json)
            {
                if (string.IsNullOrEmpty(json)) return null;
                var index = 0;
                return ParseValue(json, ref index);
            }

            private static object ParseValue(string json, ref int index)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length) return null;
                var ch = json[index];
                if (ch == '"') return ParseString(json, ref index);
                if (ch == '{') return ParseObject(json, ref index);
                if (ch == '[') return ParseArray(json, ref index);
                if (ch == 't' || ch == 'f') return ParseBool(json, ref index);
                if (ch == 'n') { index += 4; return null; }
                if (ch == '-' || char.IsDigit(ch)) return ParseNumber(json, ref index);
                return null;
            }

            private static string ParseString(string json, ref int index)
            {
                index++; // 跳过开头 "
                var chars = new List<char>();
                while (index < json.Length)
                {
                    var ch = json[index++];
                    if (ch == '"') return new string(chars.ToArray());
                    if (ch == '\\' && index < json.Length)
                    {
                        var esc = json[index++];
                        switch (esc)
                        {
                            case '"': chars.Add('"'); break;
                            case '\\': chars.Add('\\'); break;
                            case '/': chars.Add('/'); break;
                            case 'b': chars.Add('\b'); break;
                            case 'f': chars.Add('\f'); break;
                            case 'n': chars.Add('\n'); break;
                            case 'r': chars.Add('\r'); break;
                            case 't': chars.Add('\t'); break;
                            case 'u':
                                if (index + 4 <= json.Length)
                                {
                                    chars.Add((char)int.Parse(json.Substring(index, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
                                    index += 4;
                                }
                                break;
                            default: chars.Add(esc); break;
                        }
                    }
                    else
                    {
                        chars.Add(ch);
                    }
                }
                return new string(chars.ToArray());
            }

            private static object ParseNumber(string json, ref int index)
            {
                var start = index;
                if (json[index] == '-') index++;
                while (index < json.Length && char.IsDigit(json[index])) index++;
                var hasFraction = false;
                if (index < json.Length && json[index] == '.')
                {
                    hasFraction = true;
                    index++;
                    while (index < json.Length && char.IsDigit(json[index])) index++;
                }
                if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
                {
                    hasFraction = true;
                    index++;
                    if (index < json.Length && (json[index] == '+' || json[index] == '-')) index++;
                    while (index < json.Length && char.IsDigit(json[index])) index++;
                }
                var text = json.Substring(start, index - start);
                // 整数用 long(保留整数输出),含小数/指数用 double
                return hasFraction
                    ? (object)double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)
                    : (object)long.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            }

            private static bool ParseBool(string json, ref int index)
            {
                if (json[index] == 't') { index += 4; return true; }
                index += 5; return false;
            }

            private static Dictionary<string, object> ParseObject(string json, ref int index)
            {
                index++; // 跳过 {
                var obj = new Dictionary<string, object>();
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == '}') { index++; return obj; }
                while (index < json.Length)
                {
                    SkipWhitespace(json, ref index);
                    var key = ParseString(json, ref index); // key
                    SkipWhitespace(json, ref index);
                    if (index >= json.Length || json[index] != ':') return obj;
                    index++;
                    var value = ParseValue(json, ref index);
                    // 保留 value(含 null):UnityCliSerializer 的 IDictionary 分支会跳过 null value,
                    // 与原 Prop("name", null) 写出 null 略有差异,但对诊断输出无影响
                    obj[key] = value;
                    SkipWhitespace(json, ref index);
                    if (index < json.Length && json[index] == ',') { index++; continue; }
                    if (index < json.Length && json[index] == '}') { index++; return obj; }
                    return obj;
                }
                return obj;
            }

            private static List<object> ParseArray(string json, ref int index)
            {
                index++; // 跳过 [
                var arr = new List<object>();
                SkipWhitespace(json, ref index);
                if (index < json.Length && json[index] == ']') { index++; return arr; }
                while (index < json.Length)
                {
                    var value = ParseValue(json, ref index);
                    arr.Add(value); // 保留 null 元素:UnityCliSerializer 会输出为 null
                    SkipWhitespace(json, ref index);
                    if (index < json.Length && json[index] == ',') { index++; continue; }
                    if (index < json.Length && json[index] == ']') { index++; return arr; }
                    return arr;
                }
                return arr;
            }

            private static void SkipWhitespace(string json, ref int index)
            {
                while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
            }
        }
    }
}
#endif
