#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
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
        private const string ProjectToolFingerprintDefaultMode = "block";
        private static readonly Dictionary<string, UnityCliProjectToolDefinition> ProjectTools = new Dictionary<string, UnityCliProjectToolDefinition>(StringComparer.Ordinal);

        public static void RegisterProjectTool(UnityCliProjectToolDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.Name) || string.IsNullOrEmpty(definition.Summary) || definition.Execute == null)
            {
                throw new ArgumentException("Custom tool registration requires name, summary, and Execute delegate.");
            }
            if (definition.ArtifactKinds == null)
            {
                definition.ArtifactKinds = new[] { "json", "log", "file", "image" };
            }
            if (definition.RequiredInput == null)
            {
                definition.RequiredInput = new string[0];
            }
            definition.Group = string.IsNullOrEmpty(definition.Group) ? ProjectToolGroupFromName(definition.Name) : definition.Group;
            definition.Version = string.IsNullOrEmpty(definition.Version) ? "1.0.0" : definition.Version;
            definition.InputSchema = string.IsNullOrEmpty(definition.InputSchema) ? "{}" : definition.InputSchema;
            definition.OutputSchema = string.IsNullOrEmpty(definition.OutputSchema) ? "{}" : definition.OutputSchema;
            definition.CapabilityType = string.IsNullOrEmpty(definition.CapabilityType) ? "project-write" : definition.CapabilityType;
            definition.Target = string.IsNullOrEmpty(definition.Target) ? "editor" : definition.Target;
            definition.AvailabilityScope = string.IsNullOrEmpty(definition.AvailabilityScope) ? (definition.Target == "runtime" ? "playModeSession" : "editorSession") : definition.AvailabilityScope;
            definition.PlayModeSupport = NormalizeProjectToolPlayModeSupport(definition.PlayModeSupport, definition.Target);
            definition.DefaultTimeoutMs = definition.DefaultTimeoutMs <= 0 ? 30000 : definition.DefaultTimeoutMs;
            definition.MaxTimeoutMs = definition.MaxTimeoutMs <= 0 ? 600000 : definition.MaxTimeoutMs;
            definition.RegisteredBy = string.IsNullOrEmpty(definition.RegisteredBy) ? "unity-editor-package" : definition.RegisteredBy;
            definition.DescriptorSource = string.IsNullOrEmpty(definition.DescriptorSource) ? "runtime-registration" : definition.DescriptorSource;
            RefreshProjectToolFingerprint(definition, null);
            ProjectTools[definition.Name] = definition;
        }

        private static void DiscoverProjectTools()
        {
            if (projectToolsDiscovered)
            {
                return;
            }
            projectToolsDiscovered = true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null) continue;

                    // 新形态:扫描实现 IUnityCliTool<,> 泛型接口的类(Stage 2+,废除 attribute 后的唯一发现路径)
                    // IUnityCliDeferredTool<,> 是 IUnityCliTool<,> 的子接口,实现它的工具(含 runtime.wait)
                    // 由 TryRegisterInterfaceProjectTool 统一发现,不再需要特殊直接注册。
                    TryRegisterInterfaceProjectTool(assembly, type);
                }
            }
        }

        // ── 新形态:IUnityCliTool<TReq,TRes> 泛型接口实现类发现与注册 ──
        // 设计依据:.plan/自定义工具定义规范.md(废除 attribute,实现接口即声明)
        private static void TryRegisterInterfaceProjectTool(Assembly assembly, Type type)
        {
            if (type == null || type.IsAbstract || type.IsInterface) return;

            // 查找实现的 IUnityCliTool<TReq,TRes> 接口
            Type toolInterface = null;
            Type reqType = null;
            Type resType = null;
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IUnityCliTool<,>))
                {
                    toolInterface = iface;
                    var args = iface.GetGenericArguments();
                    reqType = args[0];
                    resType = args[1];
                    break;
                }
            }
            if (toolInterface == null) return;

            // deferred 检测:若同时实现 IUnityCliDeferredTool<,>,缓存反射元数据供 Execute 委托使用。
            // 不实现则 tryDeferMethod 为 null,Execute 委托走原同步路径(零影响)。
            Type deferredInterface = null;
            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IUnityCliDeferredTool<,>))
                {
                    deferredInterface = iface;
                    break;
                }
            }
            MethodInfo tryDeferMethod = null;
            FieldInfo fiIsComplete = null, fiOnComplete = null, fiTimeoutMs = null;
            FieldInfo fiTimeoutErrorCode = null, fiTimeoutErrorMessage = null, fiTimeoutNextAction = null;
            if (deferredInterface != null)
            {
                tryDeferMethod = deferredInterface.GetMethod("TryDefer");
                // DeferredCompletion<TRes> 的字段经构造泛型类型预缓存,避免每帧反射。
                var completionType = typeof(DeferredCompletion<>).MakeGenericType(resType);
                fiIsComplete = completionType.GetField("IsComplete");
                fiOnComplete = completionType.GetField("OnComplete");
                fiTimeoutMs = completionType.GetField("TimeoutMs");
                fiTimeoutErrorCode = completionType.GetField("TimeoutErrorCode");
                fiTimeoutErrorMessage = completionType.GetField("TimeoutErrorMessage");
                fiTimeoutNextAction = completionType.GetField("TimeoutNextAction");
            }

            // 实例化工具类(要求无参构造)
            object instance;
            try
            {
                instance = Activator.CreateInstance(type);
            }
            catch (Exception e)
            {
                WriteLog("custom_tool_registration_skipped type=" + type.FullName + " reason=instantiation_failed err=" + e.GetType().Name);
                return;
            }

            // 调 Describe() 取 desc 人写字段
            ToolDescriptor descriptor = null;
            try
            {
                var describeMethod = type.GetMethod("Describe", Type.EmptyTypes);
                if (describeMethod == null)
                {
                    WriteLog("custom_tool_registration_skipped type=" + type.FullName + " reason=missing_describe");
                    return;
                }
                descriptor = describeMethod.Invoke(instance, null) as ToolDescriptor;
            }
            catch (Exception e)
            {
                WriteLog("custom_tool_registration_skipped type=" + type.FullName + " reason=describe_failed err=" + e.GetType().Name);
                return;
            }
            if (descriptor == null || string.IsNullOrEmpty(descriptor.Name))
            {
                WriteLog("custom_tool_registration_skipped type=" + type.FullName + " reason=descriptor_incomplete");
                return;
            }

            // 从 TReq/TRes 反射派生 schema(框架派生,Describe 无法染指)
            var inputSchema = UnityCliSchemaDeriver.DeriveSchema(reqType);
            var outputSchema = UnityCliSchemaDeriver.DeriveSchema(resType);
            var inferredSource = InferProjectToolSource(assembly, type, descriptor);

            // 包装 Execute 为旧 UnityCliProjectToolDefinition.Execute 委托(string in/out)
            // 输入 JSON → 反序列化为 TReq → 调 Execute(TReq):TRes → 序列化 TRes 为 JSON
            var executeMethod = toolInterface.GetMethod("Execute");
            var definition = new UnityCliProjectToolDefinition
            {
                Name = descriptor.Name,
                Summary = descriptor.Summary,
                Description = descriptor.Description,
                Group = string.IsNullOrEmpty(descriptor.Group) ? "custom" : descriptor.Group,
                Source = inferredSource,
                Version = string.IsNullOrEmpty(descriptor.Version) ? "1.0.0" : descriptor.Version,
                InputSchema = string.IsNullOrEmpty(inputSchema) ? "{}" : inputSchema,
                OutputSchema = string.IsNullOrEmpty(outputSchema) ? "{}" : outputSchema,
                CapabilityType = string.IsNullOrEmpty(descriptor.CapabilityType) ? "project-write" : descriptor.CapabilityType,
                DestructiveHint = descriptor.DestructiveHint,
                Target = string.IsNullOrEmpty(descriptor.Target) ? "editor" : descriptor.Target,
                PlayModeSupport = string.IsNullOrEmpty(descriptor.PlayModeSupport) ? "editor-only" : descriptor.PlayModeSupport,
                DefaultTimeoutMs = descriptor.DefaultTimeoutMs <= 0 ? 30000 : descriptor.DefaultTimeoutMs,
                MaxTimeoutMs = descriptor.MaxTimeoutMs <= 0 ? 600000 : descriptor.MaxTimeoutMs,
                SupportsJob = descriptor.SupportsJob,
                ArtifactKinds = descriptor.ArtifactKinds ?? new[] { "json", "log", "file", "image" },
                RequiredInput = new string[0],
                RegisteredBy = "unity-editor-package",
                DescriptorSource = "IUnityCliTool",
                Assembly = assembly.GetName().Name,
                Method = type.FullName + ".Execute",
                Fingerprint = descriptor.Fingerprint,
                PinnedFingerprint = descriptor.Fingerprint,
                ExpectedFingerprint = descriptor.Fingerprint,
                Execute = delegate(UnityCliProjectToolContext context)
                {
                    // 输入:JSON → TReq(反序列化,废除 StringArg 正则刮)。用非泛型重载(运行时 Type)。
                    var req = UnityCliSerializer.Deserialize(context.InputJson, reqType);
                    // deferred 路径:若实现 IUnityCliDeferredTool,反射调 TryDefer(out completion)。
                    // 返回 true 则校验 completion 并启动 StartDeferredWait(返回 marker);false 走同步 Execute。
                    if (tryDeferMethod != null)
                    {
                        // out 参数反射:Invoke 传 object[]{req, ctx, null},Invoke 后 args[2] 为 completion。
                        var deferArgs = new object[] { req, context, null };
                        var deferred = (bool)tryDeferMethod.Invoke(instance, deferArgs);
                        if (deferred)
                        {
                            return StartDeferredFromCompletion(
                                deferArgs[2], currentWorkItem,
                                fiIsComplete, fiOnComplete, fiTimeoutMs,
                                fiTimeoutErrorCode, fiTimeoutErrorMessage, fiTimeoutNextAction);
                        }
                    }
                    // 同步路径:调 handler(传 ctx)
                    var result = executeMethod.Invoke(instance, new object[] { req, context });
                    // 输出:TRes → JSON(反射序列化)
                    return result == null ? "null" : UnityCliSerializer.Serialize(result);
                }
            };
            RegisterProjectTool(definition);
            WriteLog("custom_tool_registered name=" + descriptor.Name + " source=IUnityCliTool type=" + type.FullName
                + (deferredInterface != null ? " deferred=true" : ""));
        }

        /// <summary>
        /// 用预缓存 FieldInfo 读取 DeferredCompletion&lt;TRes&gt; 字段并启动 StartDeferredWait。
        /// completion 为 DeferredCompletion&lt;TRes&gt;(TRes 运行时已知);IsComplete/OnComplete 非空校验失败抛 E_SCHEMA_INVALID。
        /// onSuccess 序列化 OnComplete 返回的 TRes 为 SuccessJson envelope(与旧 deferred profiler 响应形态一致)。
        /// currentWorkItem 由 ProcessQueuedWork 在 Execute 调用链内设置,此处直接取用。
        /// </summary>
        private static string StartDeferredFromCompletion(
            object completion,
            BridgeWorkItem item,
            FieldInfo isCompleteField,
            FieldInfo onCompleteField,
            FieldInfo timeoutMsField,
            FieldInfo timeoutErrorCodeField,
            FieldInfo timeoutErrorMessageField,
            FieldInfo timeoutNextActionField)
        {
            if (completion == null)
            {
                throw new BridgeCommandException("E_SCHEMA_INVALID",
                    "Deferred tool TryDefer returned true but completion is null.",
                    "Fix the tool implementation: populate the out completion parameter.");
            }
            var isComplete = (Func<bool>)isCompleteField.GetValue(completion);
            var onComplete = (Delegate)onCompleteField.GetValue(completion);
            if (isComplete == null || onComplete == null)
            {
                throw new BridgeCommandException("E_SCHEMA_INVALID",
                    "Deferred tool completion requires non-null IsComplete and OnComplete.",
                    "Fix the tool implementation: set IsComplete and OnComplete on the DeferredCompletion.");
            }
            var timeoutMs = (int)timeoutMsField.GetValue(completion);
            var timeoutErrorCode = (string)timeoutErrorCodeField.GetValue(completion);
            var timeoutErrorMessage = (string)timeoutErrorMessageField.GetValue(completion);
            var timeoutNextAction = (string)timeoutNextActionField.GetValue(completion);
            return StartDeferredWait(
                item,
                isComplete,
                timeoutMs,
                () =>
                {
                    // DynamicInvoke 会把 OnComplete 抛出的 BridgeCommandException 包装成 TargetInvocationException,
                    // 这里解包以让 StartDeferredWait 的 catch(BridgeCommandException) 捕获原始结构化错误码。
                    try
                    {
                        var res = onComplete.DynamicInvoke();
                        return res == null ? SuccessJson("null") : SuccessJson(UnityCliSerializer.Serialize(res));
                    }
                    catch (TargetInvocationException tie)
                    {
                        throw tie.InnerException ?? tie;
                    }
                },
                timeoutErrorCode,
                timeoutErrorMessage,
                timeoutNextAction);
        }

        private static string ProjectToolGroupFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "custom";
            var dot = name.IndexOf('.');
            return dot > 0 ? name.Substring(0, dot) : "custom";
        }

        private static string ProjectToolList(BridgeRequest request)
        {
            DiscoverProjectTools();
            var tools = new List<string>();
            foreach (var pair in ProjectTools)
            {
                tools.Add(ProjectToolDefinitionJson(pair.Value));
            }
            tools.Sort(StringComparer.Ordinal);
            var data = new UnityCliJsonWriter().BeginObject()
                .PropArray("tools", arr => { foreach (var tool in tools) { arr.RawValue(tool); } })
                .Prop("logPath", LogPath())
                .EndObject()
                .ToString();
            return SuccessJson(data);
        }

        /// <summary>
        /// 生成面向 discovery Context 的项目 Tool 摘要，避免暴露 handler、fingerprint 等内部实现字段。
        /// </summary>
        private static string ProjectToolPublicDiscoveryJson()
        {
            DiscoverProjectTools();
            var tools = new List<string>();
            foreach (var pair in ProjectTools)
            {
                tools.Add(ProjectToolPublicDefinitionJson(pair.Value));
            }
            tools.Sort(StringComparer.Ordinal);
            var writer = new UnityCliJsonWriter().BeginArray();
            foreach (var tool in tools)
            {
                writer.RawValue(tool);
            }
            return writer.EndArray().ToString();
        }

        private sealed class ProjectToolRunArgs
        {
            public string Name { get; set; }
            public string FingerprintMode { get; set; }
            public int? TimeoutMs { get; set; }
            public string ArtifactDir { get; set; }
            public string ProjectPath { get; set; }
            public ProjectToolExecutionArgs Execution { get; set; }
        }

        private sealed class ProjectToolExecutionArgs
        {
            public bool? PreferJob { get; set; }
        }

        private static string ProjectToolRun(BridgeRequest request)
        {
            DiscoverProjectTools();
            var args = request.DeserializeArgs<ProjectToolRunArgs>();
            var name = args.Name;
            if (string.IsNullOrEmpty(name) || !ProjectTools.ContainsKey(name))
            {
                return ErrorJson("E_TOOL_NOT_FOUND", "Tool not found: " + name, "Run unity_cli tools list --include-custom to inspect registered tools.");
            }

            var definition = ProjectTools[name];

            if (EditorApplication.isPlaying && string.Equals(definition.PlayModeSupport, "editor-only", StringComparison.OrdinalIgnoreCase))
            {
                return ErrorJsonWithDetails("E_TOOL_PLAYMODE_UNAVAILABLE",
                    "Tool " + name + " is editor-only and cannot run while Unity is in Play Mode.",
                    new UnityCliJsonWriter().BeginObject()
                        .Prop("toolName", name)
                        .Prop("playModeSupport", definition.PlayModeSupport)
                        .Prop("isPlaying", true)
                        .EndObject().ToString(),
                    "Exit Play Mode before running this tool, or use a runtime tool with PlayModeSupport=runtime-only.");
            }

            // If the tool has Target=runtime, reject from project-tool.run and suggest runtime-tool.run
            if (definition.Target == "runtime")
            {
                return ErrorJsonWithDetails("E_RUNTIME_TOOL_TARGET_MISMATCH",
                    "Tool " + name + " has Target=runtime and must be executed via the runtime path (runtime-tool.run).",
                    new UnityCliJsonWriter().BeginObject()
                        .Prop("toolName", name)
                        .Prop("toolTarget", definition.Target)
                        .EndObject().ToString(),
                    "Use runtime-tool.run <name> or tools run <name> with a runtime target to execute this tool in Play Mode.");
            }
            var fingerprintCheck = RefreshProjectToolFingerprint(definition, args.FingerprintMode);
            if (fingerprintCheck.Blocked)
            {
                WriteLog("custom_tool_fingerprint_blocked requestId=" + request.RequestId + " name=" + name + " expected=" + fingerprintCheck.Expected + " actual=" + fingerprintCheck.Actual);
                return ErrorJsonWithDetails("E_TOOL_FINGERPRINT_MISMATCH", fingerprintCheck.Message, ProjectToolFingerprintDetailsJson(definition), "Review the registered custom tool descriptor/handler code, then update the expected fingerprint or rerun with --fingerprint-mode warn if policy allows.");
            }
            if (fingerprintCheck.Warn)
            {
                WriteLog("custom_tool_fingerprint_warn requestId=" + request.RequestId + " name=" + name + " expected=" + fingerprintCheck.Expected + " actual=" + fingerprintCheck.Actual);
            }

            var timeoutMs = args.TimeoutMs ?? definition.DefaultTimeoutMs;
            if (timeoutMs <= 0 || timeoutMs > definition.MaxTimeoutMs)
            {
                return ErrorJson("E_SCHEMA_INVALID", "timeoutMs exceeds the registered tool limit.", "Use a timeout between 1 and " + definition.MaxTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms.");
            }

            if (args.Execution != null && (args.Execution.PreferJob ?? false))
            {
                return ErrorJson("E_JOB_UNSUPPORTED", "--job / Local Agent project-tool execution is not implemented by the bundled Live Bridge yet.", "Run without --job for synchronous Live Bridge execution, or wait for Local Agent job support before relying on asynchronous project-tool semantics.");
            }

            var inputJson = request.RawJsonArg("input") ?? "{}";
            var schemaError = ValidateProjectToolInput(definition, inputJson);
            if (!string.IsNullOrEmpty(schemaError))
            {
                return ErrorJson("E_TOOL_SCHEMA_INVALID", schemaError, "Run unity_cli tools list --include-custom to inspect the tool inputSchema.");
            }

            var startedAt = DateTime.UtcNow;
            var artifactDir = ResolveArtifactDir(args.ArtifactDir, request.RequestId);
            try
            {
                Directory.CreateDirectory(artifactDir);
            }
            catch (Exception exception)
            {
                return ErrorJson("E_ARTIFACT_WRITE_FAILED", "Failed to create artifact directory: " + exception.Message, "Pass --artifact-dir to a writable directory.");
            }

            string resultJson;
            try
            {
                WriteLog("custom_tool_run requestId=" + request.RequestId + " name=" + name + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                resultJson = definition.Execute(new UnityCliProjectToolContext
                {
                    RequestId = request.RequestId,
                    ProjectPath = args.ProjectPath ?? ProjectRoot(),
                    InputJson = inputJson,
                    ArtifactDir = artifactDir,
                    LogPath = LogPath()
                });
            }
            catch (BridgeCommandException bce)
            {
                // handler 主动抛出的结构化错误：直接转成错误 envelope，
                // 避免被外层 SuccessJson 误包成 ok:true（旧行为会吞掉错误）。
                WriteLog("custom_tool_error requestId=" + request.RequestId + " name=" + name + " error=" + bce.Code);
                return BridgeExceptionErrorJson(bce);
            }
            catch (TargetInvocationException exception)
            {
                // 反射调用路径：内层可能是结构化 BridgeCommandException，需正确解包。
                var inner = exception.InnerException ?? exception;
                var innerBce = inner as BridgeCommandException;
                if (innerBce != null)
                {
                    WriteLog("custom_tool_error requestId=" + request.RequestId + " name=" + name + " error=" + innerBce.Code);
                    return BridgeExceptionErrorJson(innerBce);
                }
                return ErrorJson("E_TOOL_EXECUTION_FAILED", inner.GetType().Name + ": " + inner.Message, "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }
            catch (Exception exception)
            {
                return ErrorJson("E_TOOL_EXECUTION_FAILED", exception.GetType().Name + ": " + exception.Message, "Check Unity Console and UnityCliBridge.log with the returned requestId.");
            }

            // deferred: Execute 委托已启动 StartDeferredWait 并返回 marker。直接透传给 ProcessQueuedWork
            // (IsDeferred 已标记,不 Completed.Set;真实响应由 deferred 回调异步填入)。跳过 manifest/envelope 包装。
            if (resultJson == DeferredResponseMarker)
            {
                return resultJson;
            }

            var endedAt = DateTime.UtcNow;
            var durationMs = (long)(endedAt - startedAt).TotalMilliseconds;
            var manifest = ProjectToolManifestJson(request.RequestId, definition, artifactDir, startedAt, endedAt);
            try
            {
                File.WriteAllText(Path.Combine(artifactDir, "manifest.json"), manifest);
            }
            catch (Exception exception)
            {
                return ErrorJson("E_ARTIFACT_WRITE_FAILED", "Failed to write artifact manifest: " + exception.Message, "Pass --artifact-dir to a writable directory.");
            }
            if (durationMs > timeoutMs)
            {
                WriteLog("custom_tool_timeout requestId=" + request.RequestId + " name=" + name + " durationMs=" + durationMs.ToString(CultureInfo.InvariantCulture) + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                return ErrorJson("E_EXECUTION_TIMEOUT", "Tool exceeded timeoutMs after completion: " + durationMs.ToString(CultureInfo.InvariantCulture) + "ms > " + timeoutMs.ToString(CultureInfo.InvariantCulture) + "ms.", "Inspect bridge logs and artifact manifest with the returned requestId before retrying.");
            }

            var data = new UnityCliJsonWriter().BeginObject()
                .Prop("command", "project-tool.run")
                .PropObject("tool", tool =>
                {
                    tool.Prop("name", definition.Name)
                        .Prop("version", definition.Version);
                })
                .PropObject("execution", execution =>
                {
                    execution.Prop("channel", "live")
                        .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture))
                        .Prop("durationMs", durationMs)
                        .Prop("exitState", "completed");
                })
                .PropRaw("result", resultJson)
                .PropArray("artifacts", artifacts =>
                {
                    artifacts.BeginObject()
                        .Prop("id", "manifest")
                        .Prop("kind", "json")
                        .Prop("path", Path.Combine(artifactDir, "manifest.json"))
                        .Prop("description", "project-tool artifact manifest")
                        .EndObject();
                })
                .PropRaw("audit", ProjectToolAuditJson(request, definition, artifactDir, inputJson, timeoutMs, startedAt, endedAt, "completed"))
                .PropObject("logs", logs => logs.Prop("bridge", LogPath()))
                .EndObject()
                .ToString();
            return SuccessJson(data);
        }

        private static string ProjectToolDefinitionJson(UnityCliProjectToolDefinition definition)
        {
            var runtimeTarget = string.Equals(definition.Target, "runtime", StringComparison.OrdinalIgnoreCase);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", definition.Name)
                .Prop("summary", definition.Summary)
                .Prop("description", definition.Description)
                .Prop("group", definition.Group)
                .Prop("version", definition.Version)
                .PropRaw("inputSchema", definition.InputSchema)
                .PropRaw("outputSchema", definition.OutputSchema)
                .Prop("capabilityType", definition.CapabilityType)
                .Prop("destructiveHint", definition.DestructiveHint)
                .Prop("target", definition.Target)
                .Prop("availabilityScope", definition.AvailabilityScope)
                .Prop("playModeSupport", definition.PlayModeSupport)
                .Prop("defaultTimeoutMs", definition.DefaultTimeoutMs)
                .Prop("maxTimeoutMs", definition.MaxTimeoutMs)
                .Prop("supportsJob", definition.SupportsJob)
                .PropRaw("artifactKinds", StringArrayJson(definition.ArtifactKinds))
                .Prop("handler", runtimeTarget ? "bridge-runtime" : "bridge")
                .Prop("supportStatus", runtimeTarget ? "live-bridge-runtime" : "live-bridge")
                .Prop("cliCommand", "tools run " + definition.Name)
                .Prop("docsTopic", runtimeTarget ? "runtime-tool" : "project-tool")
                .Prop("descriptorSource", definition.DescriptorSource)
                .Prop("registeredBy", definition.RegisteredBy)
                .Prop("assembly", definition.Assembly)
                .Prop("method", definition.Method)
                .PropRaw("fingerprint", ProjectToolFingerprintJson(definition))
                .EndObject()
                .ToString();
        }

        /// <summary>
        /// 生成 AI-facing 的项目 Tool discovery 字段，隐藏 Bridge 内部路由、源码位置和指纹细节。
        /// </summary>
        private static string ProjectToolPublicDefinitionJson(UnityCliProjectToolDefinition definition)
        {
            var runtimeTarget = string.Equals(definition.Target, "runtime", StringComparison.OrdinalIgnoreCase);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", definition.Name)
                .Prop("summary", definition.Summary)
                .Prop("description", definition.Description)
                .Prop("group", definition.Group)
                .Prop("source", string.IsNullOrEmpty(definition.Source) ? "project" : definition.Source)
                .Prop("destructiveHint", definition.DestructiveHint)
                .Prop("target", definition.Target)
                .Prop("availabilityScope", definition.AvailabilityScope)
                .Prop("playModeSupport", definition.PlayModeSupport)
                .Prop("supportStatus", runtimeTarget ? "live-bridge-runtime" : "live-bridge")
                .Prop("cliCommand", "tools run " + definition.Name)
                .PropRaw("inputSchema", definition.InputSchema)
                .PropRaw("outputSchema", definition.OutputSchema)
                .EndObject()
                .ToString();
        }

        private static string InferProjectToolSource(Assembly assembly, Type type, ToolDescriptor descriptor)
        {
            var group = string.IsNullOrEmpty(descriptor.Group) ? ProjectToolGroupFromName(descriptor.Name) : descriptor.Group;
            if (group == "probuilder" || group == "cinemachine" || group == "vfx" || group == "urp" || group == "hdrp")
            {
                return "extension";
            }
            if (assembly == typeof(UnityCliBridge).Assembly)
            {
                return "built-in";
            }
            return "project";
        }

        private static string ProjectToolManifestJson(string requestId, UnityCliProjectToolDefinition definition, string artifactDir, DateTime startedAt, DateTime endedAt)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("requestId", requestId)
                .Prop("command", "project-tool.run")
                .PropObject("tool", tool =>
                {
                    tool.Prop("name", definition.Name)
                        .Prop("version", definition.Version);
                })
                .PropArray("artifact", artifact =>
                {
                    artifact.BeginObject()
                        .Prop("id", "manifest")
                        .Prop("kind", "json")
                        .Prop("relativePath", "manifest.json")
                        .Prop("producer", "unity-editor-package")
                        .EndObject();
                })
                .Prop("createdAt", endedAt.ToString("O", CultureInfo.InvariantCulture))
                .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                .Prop("root", artifactDir)
                .EndObject()
                .ToString();
        }

        private static string ProjectToolAuditJson(BridgeRequest request, UnityCliProjectToolDefinition definition, string artifactDir, string inputJson, int timeoutMs, DateTime startedAt, DateTime endedAt, string exitState)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("requestId", request.RequestId)
                .Prop("command", "project-tool.run")
                .Prop("projectPath", request.StringArg("projectPath") ?? ProjectRoot())
                .Prop("instanceId", request.StringArg("instanceId"))
                .Prop("unityVersion", Application.unityVersion)
                .Prop("bridgeVersion", "com.unity_cli")
                .Prop("startedAt", startedAt.ToString("O", CultureInfo.InvariantCulture))
                .Prop("endedAt", endedAt.ToString("O", CultureInfo.InvariantCulture))
                .Prop("durationMs", (long)(endedAt - startedAt).TotalMilliseconds)
                .PropObject("initiator", initiator =>
                {
                    initiator.Prop("cliUser", "local")
                        .Prop("cwd", Directory.GetCurrentDirectory());
                })
                .PropObject("input", input =>
                {
                    input.Prop("sourceKind", "json")
                        .Prop("sizeBytes", Encoding.UTF8.GetByteCount(inputJson ?? string.Empty));
                })
                .PropObject("execution", execution =>
                {
                    execution.Prop("channel", "live")
                        .Prop("timeoutMs", timeoutMs)
                        .Prop("exitState", exitState);
                })
                .PropObject("effects", effects =>
                {
                    effects.PropRaw("fileWrites", "[]")
                        .Prop("assetRefresh", false)
                        .Prop("domainReload", false)
                        .Prop("playModeChanged", false)
                        .Prop("packagesChanged", false);
                })
                .PropArray("artifacts", artifacts =>
                {
                    artifacts.BeginObject()
                        .Prop("kind", "json")
                        .Prop("path", Path.Combine(artifactDir, "manifest.json"))
                        .EndObject();
                })
                .PropObject("tool", tool =>
                {
                    tool.Prop("name", definition.Name)
                        .Prop("capabilityType", definition.CapabilityType)
                        .PropRaw("fingerprint", ProjectToolFingerprintJson(definition));
                })
                .EndObject()
                .ToString();
        }

        private sealed class ProjectToolFingerprintCheck
        {
            public bool Blocked;
            public bool Warn;
            public string Expected;
            public string Actual;
            public string Message;
        }

        private static ProjectToolFingerprintCheck RefreshProjectToolFingerprint(UnityCliProjectToolDefinition definition, string requestedMode)
        {
            var descriptor = ProjectToolDescriptorFingerprintMaterial(definition);
            definition.DescriptorSha256 = Sha256Hex(descriptor);
            definition.HandlerSha256 = Sha256Hex(ProjectToolHandlerFingerprintMaterial(definition));
            definition.Fingerprint = Sha256Hex("unity-cli-project-tool-fingerprint-v1\nhandler=" + definition.HandlerSha256 + "\ndescriptor=" + definition.DescriptorSha256);

            // When the attribute has an explicit Fingerprint (pinned by the developer),
            // enforce strict matching. When it is not pinned (null/empty), auto-accept
            // the current computed fingerprint as the new baseline — this allows
            // built-in sample tools to evolve without manual fingerprint updates.
            var isPinned = !string.IsNullOrEmpty(definition.PinnedFingerprint);
            if (isPinned)
            {
                definition.ExpectedFingerprint = definition.PinnedFingerprint;
            }
            else
            {
                // Not pinned: always accept current fingerprint as expected.
                // Update the persisted baseline so mismatch is detectable if
                // the user later pins the fingerprint.
                definition.ExpectedFingerprint = definition.Fingerprint;
                SaveProjectToolExpectedFingerprint(definition.Name, definition.Fingerprint);
            }

            definition.FingerprintMode = NormalizeProjectToolFingerprintMode(requestedMode);
            var matches = string.Equals(definition.ExpectedFingerprint, definition.Fingerprint, StringComparison.OrdinalIgnoreCase);
            definition.FingerprintStatus = matches ? "match" : (definition.FingerprintMode == "warn" ? "warn" : "blocked");
            definition.FingerprintMessage = matches
                ? "Custom tool fingerprint matches handler code + descriptor."
                : "Custom tool fingerprint mismatch for " + definition.Name + ": expected " + definition.ExpectedFingerprint + " but computed " + definition.Fingerprint + ".";
            return new ProjectToolFingerprintCheck
            {
                Blocked = !matches && definition.FingerprintMode == "block",
                Warn = !matches && definition.FingerprintMode == "warn",
                Expected = definition.ExpectedFingerprint,
                Actual = definition.Fingerprint,
                Message = definition.FingerprintMessage
            };
        }


        private static string LoadProjectToolExpectedFingerprint(string name)
        {
            try
            {
                var path = ProjectToolExpectedFingerprintPath(name);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch (Exception exception)
            {
                WriteLog("custom_tool_fingerprint_baseline_read_failed name=" + name + " error=" + exception.GetType().Name + ": " + exception.Message);
                return null;
            }
        }

        private static void SaveProjectToolExpectedFingerprint(string name, string fingerprint)
        {
            try
            {
                var path = ProjectToolExpectedFingerprintPath(name);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, fingerprint ?? string.Empty);
            }
            catch (Exception exception)
            {
                WriteLog("custom_tool_fingerprint_baseline_write_failed name=" + name + " error=" + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static string ProjectToolExpectedFingerprintPath(string name)
        {
            return Path.Combine(ProjectRoot(), "Library", "UnityCliProjectToolFingerprints", SafeProjectToolFingerprintFileName(name) + ".sha256");
        }

        private static string SafeProjectToolFingerprintFileName(string name)
        {
            return Regex.Replace(name ?? "project-tool", "[^A-Za-z0-9_.-]", "_");
        }

        private static string NormalizeProjectToolFingerprintMode(string requestedMode)
        {
            if (string.Equals(requestedMode, "warn", StringComparison.OrdinalIgnoreCase)) return "warn";
            if (string.Equals(requestedMode, "block", StringComparison.OrdinalIgnoreCase)) return "block";
            var environment = Environment.GetEnvironmentVariable("UNITY_CLI_PROJECT_TOOL_FINGERPRINT_MODE");
            if (string.Equals(environment, "warn", StringComparison.OrdinalIgnoreCase)) return "warn";
            if (string.Equals(environment, "block", StringComparison.OrdinalIgnoreCase)) return "block";
            return ProjectToolFingerprintDefaultMode;
        }

        private static string ProjectToolDescriptorFingerprintMaterial(UnityCliProjectToolDefinition definition)
        {
            return "name=" + (definition.Name ?? string.Empty)
                + "\nsummary=" + (definition.Summary ?? string.Empty)
                + "\ndescription=" + (definition.Description ?? string.Empty)
                + "\ngroup=" + (definition.Group ?? string.Empty)
                + "\nversion=" + (definition.Version ?? string.Empty)
                + "\ninputSchema=" + NormalizeFingerprintJson(definition.InputSchema)
                + "\noutputSchema=" + NormalizeFingerprintJson(definition.OutputSchema)
                + "\ncapabilityType=" + (definition.CapabilityType ?? string.Empty)
                + "\ndestructiveHint=" + Bool(definition.DestructiveHint)
                + "\nplayModeSupport=" + (definition.PlayModeSupport ?? string.Empty)
                + "\ndefaultTimeoutMs=" + definition.DefaultTimeoutMs.ToString(CultureInfo.InvariantCulture)
                + "\nmaxTimeoutMs=" + definition.MaxTimeoutMs.ToString(CultureInfo.InvariantCulture)
                + "\nsupportsJob=" + Bool(definition.SupportsJob)
                + "\nartifactKinds=" + string.Join(",", (definition.ArtifactKinds ?? new string[0]).OrderBy(value => value, StringComparer.Ordinal).ToArray())
                + "\nrequiredInput=" + string.Join(",", (definition.RequiredInput ?? new string[0]).OrderBy(value => value, StringComparer.Ordinal).ToArray())
                + "\ndescriptorSource=" + (definition.DescriptorSource ?? string.Empty);
        }

        private static string NormalizeProjectToolPlayModeSupport(string value, string target)
        {
            if (string.Equals(value, "supported", StringComparison.OrdinalIgnoreCase)) return "supported";
            if (string.Equals(value, "runtime-only", StringComparison.OrdinalIgnoreCase)) return "runtime-only";
            if (string.Equals(value, "session-affecting", StringComparison.OrdinalIgnoreCase)) return "session-affecting";
            if (string.Equals(value, "editor-only", StringComparison.OrdinalIgnoreCase)) return "editor-only";
            if (string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase)) return "unknown";
            return string.Equals(target, "runtime", StringComparison.OrdinalIgnoreCase) ? "runtime-only" : "editor-only";
        }

        private static string ProjectToolHandlerFingerprintMaterial(UnityCliProjectToolDefinition definition)
        {
            return "assembly=" + (definition.Assembly ?? string.Empty)
                + "\nmethod=" + (definition.Method ?? string.Empty)
                + "\nhandler=" + ProjectToolHandlerSource(definition);
        }

        private static string ProjectToolHandlerSource(UnityCliProjectToolDefinition definition)
        {
            try
            {
                var typeName = definition.Method;
                if (string.IsNullOrEmpty(typeName)) return string.Empty;
                var dot = typeName.LastIndexOf('.');
                if (dot <= 0) return string.Empty;
                typeName = typeName.Substring(0, dot);
                var fileName = typeName.Substring(typeName.LastIndexOf('.') + 1) + ".cs";
                foreach (var guid in AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(fileName) + " t:MonoScript"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                    var text = File.ReadAllText(path);
                    if (text.IndexOf(definition.Method.Substring(dot + 1), StringComparison.Ordinal) >= 0)
                    {
                        return NormalizeFingerprintText(text);
                    }
                }
            }
            catch (Exception exception)
            {
                WriteLog("custom_tool_fingerprint_source_failed name=" + definition.Name + " error=" + exception.GetType().Name + ": " + exception.Message);
            }
            return string.Empty;
        }

        private static string NormalizeFingerprintText(string value)
        {
            return (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private static string NormalizeFingerprintJson(string value)
        {
            return Regex.Replace((value ?? string.Empty).Trim(), "\\s+", " ");
        }

        private static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                {
                    builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                }
                return builder.ToString();
            }
        }

        private static string ProjectToolFingerprintJson(UnityCliProjectToolDefinition definition)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("algorithm", "sha256-handler-code-plus-descriptor-v1")
                .Prop("value", definition.Fingerprint)
                .Prop("expected", definition.ExpectedFingerprint)
                .Prop("status", definition.FingerprintStatus)
                .Prop("mode", definition.FingerprintMode)
                .Prop("message", definition.FingerprintMessage)
                .Prop("descriptorSha256", definition.DescriptorSha256)
                .Prop("handlerSha256", definition.HandlerSha256)
                .EndObject()
                .ToString();
        }


        private static string ProjectToolFingerprintSummaryJson()
        {
            var items = new List<string>();
            var blocked = 0;
            var warn = 0;
            foreach (var pair in ProjectTools)
            {
                var definition = pair.Value;
                RefreshProjectToolFingerprint(definition, null);
                if (definition.FingerprintStatus == "blocked") blocked++;
                if (definition.FingerprintStatus == "warn") warn++;
                items.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("name", definition.Name)
                    .PropRaw("fingerprint", ProjectToolFingerprintJson(definition))
                    .EndObject()
                    .ToString());
            }
            items.Sort(StringComparer.Ordinal);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("algorithm", "sha256-handler-code-plus-descriptor-v1")
                .Prop("mode", NormalizeProjectToolFingerprintMode(null))
                .Prop("toolCount", ProjectTools.Count)
                .Prop("blockedCount", blocked)
                .Prop("warnCount", warn)
                .PropArray("tools", arr => { foreach (var item in items) { arr.RawValue(item); } })
                .EndObject()
                .ToString();
        }

        private static string ProjectToolFingerprintDetailsJson(UnityCliProjectToolDefinition definition)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("tool", definition.Name)
                .PropRaw("fingerprint", ProjectToolFingerprintJson(definition))
                .EndObject()
                .ToString();
        }
    }
}
#endif
