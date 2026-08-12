#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor.PackageManager;

namespace UnityCli
{
    /// <summary>官方 Pipeline attribute Tool 的运行时绑定；不进入公开 descriptor。</summary>
    internal sealed class OfficialAttributeToolBinding
    {
        public MethodInfo Method { get; set; }
        public bool MainThreadRequired { get; set; }
        public bool RuntimeOnly { get; set; }
        public Type DeclaredResultType { get; set; }
        public bool NormalizeResultWithNewtonsoft { get; set; }
        public List<OfficialAttributeParameterBinding> Parameters { get; set; }
    }

    /// <summary>一个 attributed method 参数的公开名称、必填性和默认值绑定。</summary>
    internal sealed class OfficialAttributeParameterBinding
    {
        public ParameterInfo Parameter { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool Required { get; set; }
        public bool HasDefaultValue { get; set; }
        public object DefaultValue { get; set; }
    }

    public static partial class UnityCliBridge
    {
        private const string OfficialCliCommandAttribute = "Unity.Pipeline.Commands.CliCommandAttribute";
        private const string OfficialCliArgAttribute = "Unity.Pipeline.Commands.CliArgAttribute";
        private const string OfficialStructuredInput = "Unity.Pipeline.Commands.IStructuredCommandInput";
        private const string SuppressedOfficialAttributePackage = "com.unity.pipeline";
        private static readonly Dictionary<Assembly, bool> SuppressedOfficialAttributeAssemblies = new Dictionary<Assembly, bool>();
        private static readonly HashSet<Assembly> LoggedSuppressedOfficialAttributeAssemblies = new HashSet<Assembly>();
        // 超时后的 observer 只消费后台 fault，不参与业务取消、执行或完成判定。
        private static readonly CancellationToken FaultObservationToken = new CancellationToken();
        private const string NewtonsoftJsonIgnoreAttribute = "Newtonsoft.Json.JsonIgnoreAttribute";
        private const string NewtonsoftJsonPropertyAttribute = "Newtonsoft.Json.JsonPropertyAttribute";

        /// <summary>
        /// 扫描一个类型声明的官方 attributed static method。所有候选都先写入 pending registry；
        /// 任一签名、schema、ownership 或名称冲突都会阻止本轮目录发布。
        /// </summary>
        private static void TryRegisterOfficialAttributeTools(Assembly assembly, Type type)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => FindAttribute(method, OfficialCliCommandAttribute) != null)
                .OrderBy(method => method.MetadataToken)
                .ToArray();
            if (methods.Length == 0) return;
            if (IsSuppressedOfficialAttributeProvider(assembly))
            {
                if (LoggedSuppressedOfficialAttributeAssemblies.Add(assembly))
                    WriteLog("official_attribute_provider_suppressed package=" + SuppressedOfficialAttributePackage + " assembly=" + assembly.GetName().Name);
                return;
            }

            foreach (var method in methods)
            {
                var definition = CreateOfficialAttributeTool(assembly, type, method);
                RegisterProjectTool(definition);
                WriteLog("custom_tool_registered name=" + definition.Name + " source=official-attribute type=" + type.FullName + " method=" + method.Name);
            }
        }

        /// <summary>
        /// 只按method声明assembly的exact Package ID抑制Pipeline自身命令。
        /// attribute定义assembly、命令名与namespace都不能作为owner，避免误伤项目和其他Package。
        /// </summary>
        private static bool IsSuppressedOfficialAttributeProvider(Assembly assembly)
        {
            bool suppressed;
            if (SuppressedOfficialAttributeAssemblies.TryGetValue(assembly, out suppressed)) return suppressed;
            PackageInfo package;
            try
            {
                package = PackageInfo.FindForAssembly(assembly);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("Official attribute provider package ownership could not be resolved for " + assembly.FullName + ".", exception);
            }
            suppressed = package != null && string.Equals(package.name, SuppressedOfficialAttributePackage, StringComparison.Ordinal);
            SuppressedOfficialAttributeAssemblies.Add(assembly, suppressed);
            return suppressed;
        }

        /// <summary>从 CustomAttributeData 构造保守 descriptor、严格输入 schema 与方法返回完成契约。</summary>
        private static RegisteredTool CreateOfficialAttributeTool(Assembly assembly, Type implementationType, MethodInfo method)
        {
            if (!method.IsStatic) throw new InvalidOperationException("Official CLI command method must be static: " + MethodIdentity(method) + ".");
            if (method.IsGenericMethodDefinition || method.ContainsGenericParameters)
                throw new InvalidOperationException("Official CLI command method cannot be generic: " + MethodIdentity(method) + ".");
            if (method.ReturnType.IsByRef || method.ReturnType.IsPointer)
                throw new InvalidOperationException("Official CLI command return type is unsupported: " + MethodIdentity(method) + ".");
            if (method.ReturnType == typeof(void) && method.IsDefined(typeof(AsyncStateMachineAttribute), false))
                throw new InvalidOperationException("Official CLI command cannot use async void: " + MethodIdentity(method) + ".");

            var command = FindAttribute(method, OfficialCliCommandAttribute);
            var name = ConstructorString(command, 0, "name", method);
            var description = ConstructorString(command, 1, "description", method);
            if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, name.Trim(), StringComparison.Ordinal) || name.Any(char.IsWhiteSpace))
                throw new InvalidOperationException("Official CLI command name must be one non-empty token without whitespace: " + MethodIdentity(method) + ".");
            var toolName = OfficialAttributeToolPrefix + name;
            if (string.IsNullOrWhiteSpace(description))
                throw new InvalidOperationException("Official CLI command description is required: " + MethodIdentity(method) + ".");

            var mainThreadRequired = NamedBoolean(command, "MainThreadRequired", true);
            var runtimeOnly = NamedBoolean(command, "RuntimeOnly", false);
            var parameters = method.GetParameters().Select(BuildOfficialParameter).ToList();
            var duplicateParameter = parameters.GroupBy(parameter => parameter.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicateParameter != null)
                throw new InvalidOperationException("Official CLI command contains duplicate projected parameter name " + duplicateParameter.Key + ": " + MethodIdentity(method) + ".");

            var inputSchema = BuildOfficialInputSchema(name, description, parameters);
            var output = BuildOfficialOutputContract(method.ReturnType);
            var exampleInput = parameters.ToDictionary(parameter => parameter.Name, OfficialExampleValue, StringComparer.Ordinal);
            var playModeSupport = runtimeOnly ? "runtime-only" : "unknown";
            var binding = new OfficialAttributeToolBinding
            {
                Method = method,
                MainThreadRequired = mainThreadRequired,
                RuntimeOnly = runtimeOnly,
                DeclaredResultType = output.DeclaredType,
                NormalizeResultWithNewtonsoft = output.NormalizeWithNewtonsoft,
                Parameters = parameters
            };
            var ownership = registryOwnership.Resolve(implementationType);
            if (!string.Equals(ownership, "project", StringComparison.Ordinal))
                throw new InvalidOperationException("Official attribute Tool ownership must resolve to project or third-party code: " + MethodIdentity(method) + ".");

            var completion = new UnityCliToolCompletionContract(
                toolName,
                "project",
                "direct-terminal",
                "external",
                "attributed-method-return-v1",
                "official-attributed-method-return",
                "outcome-unknown-after-dispatch",
                "E_EXECUTION_TIMEOUT",
                "method-may-still-be-running",
                "official-method-return-v1",
                "required",
                true);

            return new RegisteredTool
            {
                Name = toolName,
                Summary = OfficialSummary(description),
                Description = description + " Imported from Unity.Pipeline.Commands.CliCommandAttribute. Risk, PlayMode, dry-run and completion semantics use the conservative official-attribute contract. Schema defaults and examples describe valid default behavior, not caller intent; use caller-requested business values on the first execution.",
                Group = OfficialAttributeToolGroup,
                Source = "project",
                Ownership = "project",
                InputSchema = UnityCliSerializer.Serialize(inputSchema),
                OutputSchema = output.Schema,
                DestructiveHint = true,
                SideEffects = new[] { "arbitrary-method-invocation" },
                SupportsDryRun = false,
                Permissions = new[] { "execute-user-code" },
                RequiresPackages = new string[0],
                Examples = new[]
                {
                    new ToolExample
                    {
                        Description = "Valid invocation using schema defaults or generated placeholders. Use these values only for default behavior; replace them with caller-requested business values before execution.",
                        Input = exampleInput
                    }
                },
                Target = "editor",
                AvailabilityScope = runtimeOnly ? "playModeSession" : "editorSession",
                PlayModeSupport = playModeSupport,
                ContractProfile = "official-attribute",
                DefaultTimeoutMs = 30000,
                MaxTimeoutMs = 600000,
                RequiredInput = parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name).ToArray(),
                RegisteredBy = "official-pipeline-attribute",
                DescriptorSource = OfficialCliCommandAttribute,
                Assembly = assembly.GetName().Name,
                Method = MethodIdentity(method),
                RequestType = typeof(Dictionary<string, object>),
                ResultType = output.ValidationType,
                CompletionContract = completion,
                OfficialAttributeBinding = binding,
                InvokeAsync = (request, context, cancellationToken) => InvokeOfficialAttributeTool(binding, request, context, cancellationToken)
            };
        }

        /// <summary>解析参数 attribute，严格验证签名和默认值可转换性。</summary>
        private static OfficialAttributeParameterBinding BuildOfficialParameter(ParameterInfo parameter)
        {
            var parameterType = parameter.ParameterType;
            if (parameter.IsOut || parameterType.IsByRef || parameterType.IsPointer || parameter.GetRequiredCustomModifiers().Length > 0)
                throw new InvalidOperationException("Official CLI command parameter type is unsupported: " + parameter.Member.DeclaringType.FullName + "." + parameter.Member.Name + "(" + parameter.Name + ").");

            var attribute = FindAttribute(parameter, OfficialCliArgAttribute);
            var name = attribute == null ? parameter.Name : ConstructorString(attribute, 0, "name", parameter.Member as MethodInfo);
            var description = attribute == null ? "Parameter: " + parameter.Name : ConstructorString(attribute, 1, "description", parameter.Member as MethodInfo);
            if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, name.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("Official CLI argument name must be non-empty without surrounding whitespace: " + parameter.Name + ".");

            var required = attribute == null ? !parameter.HasDefaultValue : NamedBoolean(attribute, "Required", false);
            object defaultValue = null;
            var hasDefault = false;
            if (parameter.HasDefaultValue && parameter.DefaultValue != DBNull.Value && parameter.DefaultValue != Missing.Value)
            {
                hasDefault = true;
                defaultValue = parameter.DefaultValue;
            }
            else if (attribute != null && TryNamedValue(attribute, "DefaultValue", out defaultValue))
            {
                hasDefault = true;
            }
            if (hasDefault) defaultValue = ConvertOfficialArgument(defaultValue, parameterType);

            // 构造 schema 会递归验证 structured member 名称、环和可转换类型。
            BuildOfficialTypeSchema(parameterType, new HashSet<Type>());
            return new OfficialAttributeParameterBinding
            {
                Parameter = parameter,
                Name = name,
                Description = description,
                Required = required,
                HasDefaultValue = hasDefault,
                DefaultValue = defaultValue
            };
        }

        /// <summary>执行 attributed method；effect barrier 位于首次 method dispatch 之前。</summary>
        private static async ValueTask<object> InvokeOfficialAttributeTool(OfficialAttributeToolBinding binding, object request, UnityCliProjectToolContext context, CancellationToken cancellationToken)
        {
            var input = request as IDictionary<string, object>;
            if (input == null) throw new FormatException("Official attribute Tool input must be a JSON object.");
            var arguments = new object[binding.Parameters.Count];
            for (var index = 0; index < binding.Parameters.Count; index++)
            {
                var parameter = binding.Parameters[index];
                try
                {
                    object value;
                    if (input.TryGetValue(parameter.Name, out value)) arguments[index] = ConvertOfficialArgument(value, parameter.Parameter.ParameterType);
                    else if (parameter.HasDefaultValue) arguments[index] = parameter.DefaultValue;
                    else arguments[index] = DefaultValue(parameter.Parameter.ParameterType);
                }
                catch (BridgeCommandException)
                {
                    throw;
                }
                catch
                {
                    throw new BridgeCommandException(
                        "E_SCHEMA_INVALID",
                        "Attributed command input could not be converted at $." + parameter.Name + ".",
                        new Dictionary<string, object>
                        {
                            { "path", "$." + parameter.Name },
                            { "expected", OfficialTypeName(parameter.Parameter.ParameterType) },
                            { "actual", "conversion failed" }
                        },
                        new[] { "Run tools describe again and provide a value accepted by the attributed parameter schema." });
                }
            }

            context.CommitEffect();
            object rawResult;
            if (binding.MainThreadRequired)
            {
                rawResult = binding.Method.Invoke(null, arguments);
            }
            else
            {
                rawResult = await Task.Run(() => binding.Method.Invoke(null, arguments), cancellationToken).ConfigureAwait(false);
            }

            if (rawResult is Task task)
            {
                await AwaitOfficialTask(task, cancellationToken).ConfigureAwait(false);
                if (binding.DeclaredResultType == typeof(void))
                {
                    rawResult = null;
                }
                else
                {
                    var resultProperty = task.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
                    rawResult = resultProperty == null ? null : resultProperty.GetValue(task, null);
                }
            }
            return NormalizeOfficialResult(rawResult, binding.DeclaredResultType, binding.NormalizeResultWithNewtonsoft);
        }

        /// <summary>timeout 后 attributed Task 可能继续运行；只停止等待并观察其后续 fault，绝不宣称取消成功。</summary>
        private static async Task AwaitOfficialTask(Task task, CancellationToken cancellationToken)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }
            var cancellation = Task.Delay(Timeout.Infinite, cancellationToken);
            if (await Task.WhenAny(task, cancellation).ConfigureAwait(false) != task)
            {
                _ = task.ContinueWith(completed => { var ignored = completed.Exception; }, FaultObservationToken, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw new OperationCanceledException(cancellationToken);
            }
            await task.ConfigureAwait(false);
        }

        /// <summary>官方 descriptor 输入 schema 是严格 object；未知字段与缺失 required 在 dispatch 前失败。</summary>
        private static Dictionary<string, object> BuildOfficialInputSchema(string name, string description, List<OfficialAttributeParameterBinding> parameters)
        {
            var properties = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var parameter in parameters)
            {
                var valueSchema = BuildOfficialTypeSchema(parameter.Parameter.ParameterType, new HashSet<Type>());
                var acceptsNull = !parameter.Required && OfficialTypeAcceptsNull(parameter.Parameter.ParameterType)
                    && !valueSchema.ContainsKey("x-unityCliAnyJson");
                var schema = acceptsNull
                    ? new Dictionary<string, object> { { "oneOf", new object[] { valueSchema, TypeSchema("null") } } }
                    : valueSchema;
                schema["description"] = parameter.Description;
                if (parameter.HasDefaultValue && !(parameter.Required && OfficialRequiredValueMissing(parameter.DefaultValue)))
                    schema["default"] = NormalizeOfficialResult(parameter.DefaultValue, parameter.Parameter.ParameterType);
                properties.Add(parameter.Name, schema);
            }
            return new Dictionary<string, object>
            {
                { "type", "object" },
                { "title", name },
                { "description", description },
                { "properties", properties },
                { "required", parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name).ToArray() },
                { "additionalProperties", false }
            };
        }

        /// <summary>递归派生官方 scalar/enum/collection/structured schema；未知类型显式降级为任意 JSON。</summary>
        private static Dictionary<string, object> BuildOfficialTypeSchema(Type rawType, HashSet<Type> visited)
        {
            var type = Nullable.GetUnderlyingType(rawType) ?? rawType;
            if (type == typeof(object)) return AnyJsonSchema();
            if (type == typeof(string)) return TypeSchema("string");
            if (type == typeof(char)) return new Dictionary<string, object> { { "type", "string" }, { "minLength", 1 }, { "maxLength", 1 } };
            if (type == typeof(bool)) return TypeSchema("boolean");
            if (UnityCliSchemaDeriver.IsInteger(type)) return TypeSchema("integer");
            if (UnityCliSchemaDeriver.IsNumber(type)) return TypeSchema("number");
            if (type.IsEnum) return new Dictionary<string, object> { { "type", "string" }, { "enum", Enum.GetNames(type) } };
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return new Dictionary<string, object> { { "type", "string" }, { "format", "date-time" } };
            if (type == typeof(Guid)) return new Dictionary<string, object> { { "type", "string" }, { "format", "uuid" } };

            Type dictionaryValueType;
            if (TryOfficialDictionaryValueType(type, out dictionaryValueType))
                return new Dictionary<string, object> { { "type", "object" }, { "additionalProperties", BuildOfficialTypeSchema(dictionaryValueType, visited) } };

            Type elementType;
            if (TryOfficialElementType(type, out elementType))
                return new Dictionary<string, object> { { "type", "array" }, { "items", BuildOfficialTypeSchema(elementType, visited) } };

            if (IsOfficialStructuredInput(type))
            {
                if (type.IsAbstract || type.IsInterface) throw new InvalidOperationException("Official structured input must be a concrete type: " + type.FullName + ".");
                if (!visited.Add(type)) throw new InvalidOperationException("Official structured input cannot contain a recursive type cycle: " + type.FullName + ".");
                try
                {
                    var members = OfficialStructuredMembers(type);
                    var duplicate = members.GroupBy(member => member.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
                    if (duplicate != null) throw new InvalidOperationException("Official structured input contains duplicate projected member name " + duplicate.Key + ": " + type.FullName + ".");
                    var properties = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (var member in members)
                    {
                        var schema = BuildOfficialTypeSchema(member.Type, visited);
                        if (!string.IsNullOrWhiteSpace(member.Description)) schema["description"] = member.Description;
                        properties.Add(member.Name, schema);
                    }
                    return new Dictionary<string, object>
                    {
                        { "type", "object" },
                        { "properties", properties },
                        { "required", members.Where(member => member.Required).Select(member => member.Name).ToArray() },
                        { "additionalProperties", false }
                    };
                }
                finally
                {
                    visited.Remove(type);
                }
            }

            return AnyJsonSchema();
        }

        /// <summary>输出类型可由原生 deriver证明时保留 concrete schema，否则显式任意 JSON。</summary>
        private static OfficialOutputContract BuildOfficialOutputContract(Type returnType)
        {
            Type declaredType;
            if (returnType == typeof(void) || returnType == typeof(Task))
                declaredType = typeof(void);
            else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
                declaredType = returnType.GetGenericArguments()[0];
            else if (typeof(Task).IsAssignableFrom(returnType) || IsAwaitableType(returnType))
                throw new InvalidOperationException("Official CLI command asynchronous return type must be Task or Task<T>: " + returnType.FullName + ".");
            else
                declaredType = returnType;

            if (declaredType == typeof(void))
                return new OfficialOutputContract { DeclaredType = declaredType, ValidationType = typeof(void), Schema = "{\"type\":\"null\"}", NormalizeWithNewtonsoft = false };
            if (declaredType == typeof(DateTime) || declaredType == typeof(DateTimeOffset) || declaredType == typeof(Guid) || declaredType == typeof(object))
                return new OfficialOutputContract { DeclaredType = declaredType, ValidationType = typeof(object), Schema = UnityCliSerializer.Serialize(AnyJsonSchema()), NormalizeWithNewtonsoft = true };
            try
            {
                return new OfficialOutputContract
                {
                    DeclaredType = declaredType,
                    ValidationType = declaredType,
                    Schema = UnityCliSchemaDeriver.DeriveSchema(declaredType),
                    NormalizeWithNewtonsoft = false
                };
            }
            catch
            {
                return new OfficialOutputContract { DeclaredType = declaredType, ValidationType = typeof(object), Schema = UnityCliSerializer.Serialize(AnyJsonSchema()), NormalizeWithNewtonsoft = true };
            }
        }

        /// <summary>把公开 JSON value 严格转换为 attributed method 参数；失败不会回退默认值。</summary>
        private static object ConvertOfficialArgument(object value, Type rawType)
        {
            if (value == null)
            {
                if (Nullable.GetUnderlyingType(rawType) != null || !rawType.IsValueType) return null;
                throw new FormatException("JSON null is not valid for " + rawType.FullName + ".");
            }
            var type = Nullable.GetUnderlyingType(rawType) ?? rawType;
            if (type == typeof(object)) return value;
            if (type.IsEnum && !(value is string))
            {
                try { return Enum.ToObject(type, value); }
                catch { throw new FormatException("Default enum value is invalid for " + type.FullName + "."); }
            }
            if (type == typeof(DateTime))
            {
                DateTime parsed;
                if (value is string && DateTime.TryParse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed)) return parsed;
                throw new FormatException("Expected an ISO-8601 date-time string.");
            }
            if (type == typeof(DateTimeOffset))
            {
                DateTimeOffset parsed;
                if (value is string && DateTimeOffset.TryParse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsed)) return parsed;
                throw new FormatException("Expected an ISO-8601 date-time string.");
            }
            if (type == typeof(Guid))
            {
                Guid parsed;
                if (value is string && Guid.TryParse((string)value, out parsed)) return parsed;
                throw new FormatException("Expected a UUID string.");
            }
            if (IsNewtonsoftJsonTokenType(type)) return ConvertNewtonsoftJsonValue(value, type);
            var normalized = NormalizeOfficialInput(value, type);
            return UnityCliSerializer.Deserialize(UnityCliSerializer.Serialize(normalized), rawType);
        }

        /// <summary>将 structured input 的 CliArg/JsonProperty 名映射回 CLR member 名后交给统一 serializer。</summary>
        private static object NormalizeOfficialInput(object value, Type rawType)
        {
            var type = Nullable.GetUnderlyingType(rawType) ?? rawType;
            if (value == null) return null;
            Type dictionaryValueType;
            if (TryOfficialDictionaryValueType(type, out dictionaryValueType))
            {
                var dictionary = value as IDictionary<string, object>;
                if (dictionary == null) return value;
                return dictionary.ToDictionary(pair => pair.Key, pair => NormalizeOfficialInput(pair.Value, dictionaryValueType), StringComparer.Ordinal);
            }
            Type elementType;
            if (TryOfficialElementType(type, out elementType))
            {
                var list = value as IList;
                if (list == null) return value;
                return list.Cast<object>().Select(item => NormalizeOfficialInput(item, elementType)).ToList();
            }
            if (!IsOfficialStructuredInput(type)) return value;
            var source = value as IDictionary<string, object>;
            if (source == null) return value;
            var normalized = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var member in OfficialStructuredMembers(type))
            {
                object child;
                if (source.TryGetValue(member.Name, out child)) normalized[UnityCliSchemaDeriver.ToCamelCase(member.Member.Name)] = NormalizeOfficialInput(child, member.Type);
            }
            return normalized;
        }

        /// <summary>把official method结果递归归一为统一serializer可验证的普通JSON value tree。</summary>
        private static object NormalizeOfficialResult(object value, Type declaredType)
        {
            return NormalizeOfficialResult(value, declaredType, false);
        }

        private static object NormalizeOfficialResult(object value, Type declaredType, bool preferNewtonsoft)
        {
            if (value == null) return null;
            var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            var valueType = value.GetType();
            if (valueType == typeof(DateTime)) return ((DateTime)value).ToString("o", CultureInfo.InvariantCulture);
            if (valueType == typeof(DateTimeOffset)) return ((DateTimeOffset)value).ToString("o", CultureInfo.InvariantCulture);
            if (valueType == typeof(Guid)) return ((Guid)value).ToString("D");
            if (valueType.IsEnum) return value.ToString();
            if (IsNewtonsoftJsonTokenType(valueType)) return NormalizeNewtonsoftJsonValue(value, valueType);
            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (DictionaryEntry item in dictionary)
                {
                    var childType = item.Value == null ? typeof(object) : item.Value.GetType();
                    result[Convert.ToString(item.Key, CultureInfo.InvariantCulture)] = NormalizeOfficialResult(item.Value, childType, preferNewtonsoft);
                }
                return result;
            }
            if (!(value is string) && value is IEnumerable sequence)
            {
                var result = new List<object>();
                foreach (var item in sequence) result.Add(NormalizeOfficialResult(item, item == null ? typeof(object) : item.GetType(), preferNewtonsoft));
                return result;
            }
            if (type.IsPrimitive || value is string || value is decimal) return value;
            object normalized;
            return preferNewtonsoft && TryNormalizeWithNewtonsoftJson(value, out normalized) ? normalized : value;
        }

        /// <summary>执行前按同一参数 binding 验证 required、unknown field 与严格 CLR 转换。</summary>
        private static SchemaValidationFailure ValidateOfficialAttributeInput(OfficialAttributeToolBinding binding, string inputJson)
        {
            object value;
            try { value = UnityCliSerializer.Parse(inputJson); }
            catch (Exception exception) when (exception is FormatException || exception is OverflowException)
            {
                return Failure("$", "valid JSON object", "malformed JSON", exception.Message);
            }
            var input = value as IDictionary<string, object>;
            if (input == null) return Failure("$", "object", ActualType(value));
            var known = binding.Parameters.ToDictionary(parameter => parameter.Name, parameter => parameter, StringComparer.Ordinal);
            foreach (var key in input.Keys)
                if (!known.ContainsKey(key)) return Failure("$." + key, "known property", "unknown property");
            foreach (var parameter in binding.Parameters)
            {
                object child;
                if (!input.TryGetValue(parameter.Name, out child))
                {
                    if (parameter.Required) return Failure("$." + parameter.Name, "required property", "missing");
                    continue;
                }
                if (parameter.Required && (child == null || child is string && string.IsNullOrEmpty((string)child)))
                    return Failure("$." + parameter.Name, "non-empty required value", child == null ? "null" : "empty string");
                var failure = ValidateOfficialAttributeValue(child, parameter.Parameter.ParameterType, "$." + parameter.Name);
                if (failure != null) return failure;
            }
            return null;
        }

        /// <summary>递归验证官方 structured input；unknown、required 与 CLR 类型必须在 effect barrier 前失败。</summary>
        private static SchemaValidationFailure ValidateOfficialAttributeValue(object value, Type rawType, string path)
        {
            var nullableType = Nullable.GetUnderlyingType(rawType);
            if (value == null)
            {
                return nullableType != null || !rawType.IsValueType
                    ? null
                    : Failure(path, OfficialTypeName(rawType), "null");
            }
            var type = nullableType ?? rawType;
            if (type == typeof(object)) return null;
            if (type == typeof(string)) return value is string ? null : Failure(path, "string", ActualType(value));
            if (type == typeof(char))
            {
                var character = value as string;
                return character != null && character.Length == 1
                    ? null
                    : Failure(path, "single-character string", ActualType(value));
            }
            if (type == typeof(bool)) return value is bool ? null : Failure(path, "boolean", ActualType(value));
            if (UnityCliSchemaDeriver.IsInteger(type)) return ValidateInteger(value, type, path);
            if (UnityCliSchemaDeriver.IsNumber(type)) return ValidateNumber(value, type, path);
            if (type.IsEnum)
            {
                var enumName = value as string;
                return enumName != null && Enum.GetNames(type).Contains(enumName, StringComparer.Ordinal)
                    ? null
                    : Failure(path, "enum(" + string.Join("|", Enum.GetNames(type)) + ")", ActualType(value));
            }
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(Guid))
            {
                try { ConvertOfficialArgument(value, type); return null; }
                catch (Exception exception) when (exception is FormatException || exception is OverflowException || exception is InvalidCastException)
                {
                    return Failure(path, OfficialTypeName(type), ActualType(value));
                }
            }

            Type dictionaryValueType;
            if (TryOfficialDictionaryValueType(type, out dictionaryValueType))
            {
                var dictionary = value as IDictionary<string, object>;
                if (dictionary == null) return Failure(path, "object", ActualType(value));
                foreach (var pair in dictionary)
                {
                    var valueFailure = ValidateOfficialAttributeValue(pair.Value, dictionaryValueType, path + "." + pair.Key);
                    if (valueFailure != null) return valueFailure;
                }
                return null;
            }

            Type elementType;
            if (TryOfficialElementType(type, out elementType))
            {
                var items = value as IList;
                if (items == null) return Failure(path, "array", ActualType(value));
                for (var index = 0; index < items.Count; index++)
                {
                    var itemFailure = ValidateOfficialAttributeValue(items[index], elementType, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                    if (itemFailure != null) return itemFailure;
                }
                return null;
            }

            if (!IsOfficialStructuredInput(type)) return null;
            var input = value as IDictionary<string, object>;
            if (input == null) return Failure(path, "object", ActualType(value));
            var members = OfficialStructuredMembers(type);
            var knownMembers = members.ToDictionary(member => member.Name, member => member, StringComparer.Ordinal);
            foreach (var key in input.Keys)
                if (!knownMembers.ContainsKey(key)) return Failure(path + "." + key, "known property", "unknown property");
            foreach (var member in members)
            {
                object child;
                if (!input.TryGetValue(member.Name, out child))
                {
                    if (member.Required) return Failure(path + "." + member.Name, "required property", "missing");
                    continue;
                }
                if (member.Required && (child == null || child is string && string.IsNullOrEmpty((string)child)))
                    return Failure(path + "." + member.Name, "non-empty required value", child == null ? "null" : "empty string");
                var childFailure = ValidateOfficialAttributeValue(child, member.Type, path + "." + member.Name);
                if (childFailure != null) return childFailure;
            }
            return null;
        }

        private sealed class OfficialStructuredMember
        {
            public MemberInfo Member;
            public string Name;
            public string Description;
            public Type Type;
            public bool Required;
        }

        private sealed class OfficialOutputContract
        {
            public Type DeclaredType;
            public Type ValidationType;
            public string Schema;
            public bool NormalizeWithNewtonsoft;
        }

        /// <summary>枚举 structured DTO 的 public writable member，并读取 CliArg/Json.NET name。</summary>
        private static List<OfficialStructuredMember> OfficialStructuredMembers(Type type)
        {
            var members = new List<OfficialStructuredMember>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(field => field.MetadataToken))
            {
                if (field.IsInitOnly || field.IsLiteral || FindAttribute(field, NewtonsoftJsonIgnoreAttribute) != null) continue;
                members.Add(BuildOfficialStructuredMember(field, field.FieldType));
            }
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(property => property.MetadataToken))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0 || FindAttribute(property, NewtonsoftJsonIgnoreAttribute) != null) continue;
                members.Add(BuildOfficialStructuredMember(property, property.PropertyType));
            }
            return members;
        }

        private static OfficialStructuredMember BuildOfficialStructuredMember(MemberInfo member, Type memberType)
        {
            var cliArg = FindAttribute(member, OfficialCliArgAttribute);
            var jsonProperty = FindAttribute(member, NewtonsoftJsonPropertyAttribute);
            var name = cliArg != null
                ? ConstructorString(cliArg, 0, "name", null)
                : jsonProperty != null && jsonProperty.ConstructorArguments.Count > 0 && jsonProperty.ConstructorArguments[0].Value is string
                    ? (string)jsonProperty.ConstructorArguments[0].Value
                    : member.Name;
            if (string.IsNullOrWhiteSpace(name) || !string.Equals(name, name.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("Official structured member name must be non-empty without surrounding whitespace: " + member.DeclaringType.FullName + "." + member.Name + ".");
            return new OfficialStructuredMember
            {
                Member = member,
                Name = name,
                Description = cliArg == null ? null : ConstructorString(cliArg, 1, "description", null),
                Type = memberType,
                Required = cliArg != null && NamedBoolean(cliArg, "Required", false)
            };
        }

        private static bool IsOfficialStructuredInput(Type type)
        {
            return string.Equals(type.FullName, OfficialStructuredInput, StringComparison.Ordinal)
                || type.GetInterfaces().Any(candidate => string.Equals(candidate.FullName, OfficialStructuredInput, StringComparison.Ordinal));
        }

        private static bool IsAwaitableType(Type type)
        {
            return type != null && type.GetMethod("GetAwaiter", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) != null;
        }

        /// <summary>通过已加载Json.NET程序集转换JObject/JToken，core保持零编译依赖。</summary>
        private static object ConvertNewtonsoftJsonValue(object value, Type type)
        {
            var jsonConvert = FindNewtonsoftJsonConvert(type);
            var deserialize = jsonConvert.GetMethod("DeserializeObject", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Type) }, null);
            if (deserialize == null) throw new FormatException("Json.NET DeserializeObject(string, Type) is unavailable.");
            var converted = deserialize.Invoke(null, new object[] { UnityCliSerializer.Serialize(value), type });
            if (converted == null || !type.IsInstanceOfType(converted)) throw new FormatException("Json.NET value could not be converted to " + type.FullName + ".");
            return converted;
        }

        /// <summary>把JToken结果归一为统一serializer可验证的普通JSON value tree。</summary>
        private static object NormalizeNewtonsoftJsonValue(object value, Type type)
        {
            var jsonConvert = FindNewtonsoftJsonConvert(type);
            var serialize = jsonConvert.GetMethod("SerializeObject", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(object) }, null);
            if (serialize == null) throw new FormatException("Json.NET SerializeObject(object) is unavailable.");
            var json = serialize.Invoke(null, new[] { value }) as string;
            if (string.IsNullOrEmpty(json)) throw new FormatException("Json.NET value could not be serialized.");
            return UnityCliSerializer.Parse(json);
        }

        /// <summary>任意official DTO优先借已加载Json.NET归一，失败保持原始值交给统一serializer决定。</summary>
        private static bool TryNormalizeWithNewtonsoftJson(object value, out object normalized)
        {
            normalized = null;
            Type jsonConvert;
            try { jsonConvert = FindNewtonsoftJsonConvert(value.GetType()); }
            catch { return false; }
            var serialize = jsonConvert.GetMethod("SerializeObject", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(object) }, null);
            if (serialize == null) return false;
            try
            {
                var json = serialize.Invoke(null, new[] { value }) as string;
                if (string.IsNullOrEmpty(json)) return false;
                normalized = UnityCliSerializer.Parse(json);
                return true;
            }
            catch { return false; }
        }

        private static Type FindNewtonsoftJsonConvert(Type preferredType)
        {
            var direct = preferredType.Assembly.GetType("Newtonsoft.Json.JsonConvert", false);
            if (direct != null) return direct;
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("Newtonsoft.Json.JsonConvert", false))
                .FirstOrDefault(candidate => candidate != null);
            if (loaded == null) throw new FormatException("Json.NET JsonConvert is unavailable.");
            return loaded;
        }

        private static bool IsNewtonsoftJsonTokenType(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
                if (string.Equals(current.FullName, "Newtonsoft.Json.Linq.JToken", StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>只识别能由Dictionary&lt;string,T&gt;构造的真实dictionary；JObject等动态JSON类型走AnyJSON fallback。</summary>
        private static bool TryOfficialDictionaryValueType(Type type, out Type valueType)
        {
            valueType = null;
            try { return UnityCliSchemaDeriver.TryDictionaryValueType(type, out valueType); }
            catch (InvalidOperationException) { return false; }
        }

        private static bool TryOfficialElementType(Type type, out Type elementType)
        {
            elementType = null;
            if (type == typeof(string)) return false;
            if (type.IsArray)
            {
                elementType = type.GetElementType();
                return elementType != null;
            }
            if (!type.IsGenericType) return false;
            var definition = type.GetGenericTypeDefinition();
            if (definition != typeof(List<>) && definition != typeof(IList<>) && definition != typeof(IEnumerable<>)
                && definition != typeof(ICollection<>) && definition != typeof(IReadOnlyList<>) && definition != typeof(IReadOnlyCollection<>)) return false;
            elementType = type.GetGenericArguments()[0];
            return true;
        }

        private static object OfficialExampleValue(OfficialAttributeParameterBinding parameter)
        {
            if (parameter.HasDefaultValue && !(parameter.Required && OfficialRequiredValueMissing(parameter.DefaultValue)))
                return NormalizeOfficialResult(parameter.DefaultValue, parameter.Parameter.ParameterType);
            return ExampleValue(parameter.Parameter.ParameterType, new HashSet<Type>());
        }

        /// <summary>optional reference/nullable参数可显式接收null；required binding仍拒绝null与空字符串。</summary>
        private static bool OfficialTypeAcceptsNull(Type type)
        {
            return Nullable.GetUnderlyingType(type) != null || !type.IsValueType;
        }

        private static bool OfficialRequiredValueMissing(object value)
        {
            return value == null || value is string && string.IsNullOrEmpty((string)value);
        }

        private static object ExampleValue(Type rawType, HashSet<Type> visited)
        {
            var type = Nullable.GetUnderlyingType(rawType) ?? rawType;
            if (type == typeof(object)) return new Dictionary<string, object>();
            if (type == typeof(string)) return "value";
            if (type == typeof(char)) return "x";
            if (type == typeof(bool)) return false;
            if (UnityCliSchemaDeriver.IsInteger(type)) return 0;
            if (UnityCliSchemaDeriver.IsNumber(type)) return 0d;
            if (type.IsEnum) return Enum.GetNames(type).FirstOrDefault() ?? string.Empty;
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "1970-01-01T00:00:00.0000000Z";
            if (type == typeof(Guid)) return Guid.Empty.ToString("D");
            Type dictionaryValueType;
            if (TryOfficialDictionaryValueType(type, out dictionaryValueType)) return new Dictionary<string, object>();
            Type elementType;
            if (TryOfficialElementType(type, out elementType)) return new List<object>();
            if (IsOfficialStructuredInput(type))
            {
                if (!visited.Add(type)) return new Dictionary<string, object>();
                try
                {
                    return OfficialStructuredMembers(type).ToDictionary(member => member.Name, member => ExampleValue(member.Type, visited), StringComparer.Ordinal);
                }
                finally { visited.Remove(type); }
            }
            return new Dictionary<string, object>();
        }

        private static object DefaultValue(Type type)
        {
            return type.IsValueType && Nullable.GetUnderlyingType(type) == null ? Activator.CreateInstance(type) : null;
        }

        private static string OfficialTypeName(Type type)
        {
            return type == null ? "unknown" : (type.FullName ?? type.Name);
        }

        private static Dictionary<string, object> TypeSchema(string type)
        {
            return new Dictionary<string, object> { { "type", type } };
        }

        private static Dictionary<string, object> AnyJsonSchema()
        {
            return new Dictionary<string, object> { { "x-unityCliAnyJson", true } };
        }

        private static string OfficialSummary(string description)
        {
            var normalized = description.Trim();
            var newline = normalized.IndexOfAny(new[] { '\r', '\n' });
            return newline < 0 ? normalized : normalized.Substring(0, newline).Trim();
        }

        private static CustomAttributeData FindAttribute(MemberInfo member, string fullName)
        {
            return CustomAttributeData.GetCustomAttributes(member).SingleOrDefault(attribute => string.Equals(attribute.AttributeType.FullName, fullName, StringComparison.Ordinal));
        }

        private static CustomAttributeData FindAttribute(ParameterInfo parameter, string fullName)
        {
            return CustomAttributeData.GetCustomAttributes(parameter).SingleOrDefault(attribute => string.Equals(attribute.AttributeType.FullName, fullName, StringComparison.Ordinal));
        }

        private static string ConstructorString(CustomAttributeData attribute, int index, string field, MethodInfo method)
        {
            if (attribute == null || attribute.ConstructorArguments.Count <= index || !(attribute.ConstructorArguments[index].Value is string))
                throw new InvalidOperationException("Official attribute " + field + " is malformed" + (method == null ? "." : ": " + MethodIdentity(method) + "."));
            return (string)attribute.ConstructorArguments[index].Value;
        }

        private static bool NamedBoolean(CustomAttributeData attribute, string name, bool fallback)
        {
            object value;
            return TryNamedValue(attribute, name, out value) && value is bool ? (bool)value : fallback;
        }

        private static bool TryNamedValue(CustomAttributeData attribute, string name, out object value)
        {
            foreach (var argument in attribute.NamedArguments)
            {
                if (!string.Equals(argument.MemberName, name, StringComparison.Ordinal)) continue;
                value = argument.TypedValue.Value;
                return true;
            }
            value = null;
            return false;
        }

        /// <summary>官方任意用户代码的异常文本不得进入公开 timeout details。</summary>
        private static string ProjectToolFailureReason(RegisteredTool definition, string internalReason)
        {
            return definition != null && definition.OfficialAttributeBinding != null
                ? "The attributed method did not return a trustworthy terminal result after dispatch."
                : internalReason;
        }

        private static string MethodIdentity(MethodInfo method)
        {
            return (method.DeclaringType == null ? "unknown" : method.DeclaringType.FullName) + "." + method.Name;
        }
    }
}
#endif
