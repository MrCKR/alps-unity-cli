#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static readonly object CompletionProbeLock = new object();
        private static readonly Dictionary<string, string> CompletionProbeResourceOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        private static string CompletionProbeSessionOwner;

        private sealed class CompletionProbePrepared
        {
            public RegisteredContext Context { get; set; }
            public object Input { get; set; }
            public UnityCliProofResourceClaim Resource { get; set; }
            public string SessionId { get; set; }
            public long ListenerGeneration { get; set; }
            public string BeforeHash { get; set; }
            public Dictionary<string, CompletionProbePointerValue> BeforeValues { get; set; }
        }

        private sealed class CompletionProbePointerValue
        {
            public bool Exists { get; set; }
            public object Value { get; set; }
        }

        /// <summary>
        /// 在同一主线程调用内执行 pre-read、唯一 effect barrier 与 fresh post-read。
        /// Context、输入 schema、断言、资源 identity、session identity 与互斥 lease 都必须在 effect 前成立。
        /// </summary>
        public static UnityCliCompletionProbeReadback ExecuteCompletionProbe(
            UnityCliProjectToolContext ctx,
            UnityCliCompletionProbe probe,
            Action effect)
        {
            if (ctx == null) throw new ArgumentNullException("ctx");
            if (effect == null) throw new ArgumentNullException("effect");
            var prepared = PrepareCompletionProbe(ctx, probe);
            AcquireCompletionProbeLease(ctx.RequestId, prepared.Resource.ResourceKey);
            try
            {
                var beforeSnapshot = ReadCompletionProbeSnapshot(ctx, prepared.Context, prepared.Input, "before");
                prepared.BeforeHash = CanonicalCompletionProbeHash(beforeSnapshot);
                prepared.BeforeValues = ResolveCompletionProbePointers(beforeSnapshot, probe.Assertions);
                ValidateCompletionProbeBefore(probe.Assertions, prepared.BeforeValues);

                ctx.CommitEffect();
                try
                {
                    effect();
                }
                catch (Exception exception)
                {
                    throw CompletionProbeOutcomeUnknown("effect", exception);
                }

                EnsureCompletionProbeSessionContinuity(prepared, "post-effect");
                UnityCliProofResourceClaim afterResource;
                try
                {
                    afterResource = ResolveCompletionProbeResource(prepared.Context, prepared.Input);
                }
                catch (Exception exception)
                {
                    throw CompletionProbeOutcomeUnknown("resource-identity", exception);
                }
                if (!SameCompletionProbeResource(prepared.Resource, afterResource))
                    throw CompletionProbeOutcomeUnknown("resource-identity", null);

                object afterSnapshot;
                try
                {
                    afterSnapshot = ReadCompletionProbeSnapshot(ctx, prepared.Context, prepared.Input, "after");
                }
                catch (Exception exception)
                {
                    throw CompletionProbeOutcomeUnknown("after-read", exception);
                }
                EnsureCompletionProbeSessionContinuity(prepared, "after-read");

                Dictionary<string, CompletionProbePointerValue> afterValues;
                try
                {
                    var afterHash = CanonicalCompletionProbeHash(afterSnapshot);
                    if (string.Equals(prepared.BeforeHash, afterHash, StringComparison.Ordinal))
                        throw CompletionProbeOutcomeUnknown("unchanged-canonical-readback", null);
                    afterValues = ResolveCompletionProbePointers(afterSnapshot, probe.Assertions);
                    ValidateCompletionProbeAfter(probe.Assertions, afterValues);
                }
                catch (BridgeCommandException exception)
                {
                    throw CompletionProbeOutcomeUnknown("postcondition", exception);
                }
                catch (Exception exception)
                {
                    throw CompletionProbeOutcomeUnknown("postcondition", exception);
                }

                return new UnityCliCompletionProbeReadback
                {
                    Assertions = probe.Assertions.Select((assertion, index) => new UnityCliCompletionAssertionReadback
                    {
                        Path = assertion.Path,
                        Value = afterValues[index.ToString(CultureInfo.InvariantCulture)].Exists ? afterValues[index.ToString(CultureInfo.InvariantCulture)].Value : null
                    }).ToList()
                };
            }
            finally
            {
                ReleaseCompletionProbeLease(ctx.RequestId, prepared.Resource.ResourceKey);
            }
        }

        /// <summary>解析并校验 probe contract；此方法及其调用链不得产生 Unity mutation。</summary>
        private static CompletionProbePrepared PrepareCompletionProbe(UnityCliProjectToolContext ctx, UnityCliCompletionProbe probe)
        {
            if (probe == null)
                throw new BridgeCommandException("E_COMPLETION_PROBE_REQUIRED", "This Tool requires completionProbe before it can execute.", "Pass a supported read-only Context, input, and before/after assertions.");
            if (probe.Assertions == null || probe.Assertions.Count == 0)
                throw new BridgeCommandException("E_COMPLETION_PROBE_ASSERTION_INVALID", "completionProbe requires at least one assertion.", "Declare one or more JSON Pointer assertions with before and after values.");
            if (probe.Assertions.Count > 32)
                throw new BridgeCommandException("E_COMPLETION_PROBE_ASSERTION_INVALID", "completionProbe accepts at most 32 assertions.", "Reduce the assertion set to the state changed by this request.");
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer || IsPlayModeChanging())
                throw new BridgeCommandException("E_UNITY_BUSY", "Unity Editor is not stable enough to capture completionProbe pre-state.", "Wait for editor.state to report ready, then submit a new request.");

            DiscoverContexts();
            RegisteredContext definition;
            if (!Contexts.TryGetValue(probe.Context ?? string.Empty, out definition) || definition.ResolveProofResource == null)
                throw new BridgeCommandException("E_COMPLETION_PROBE_CONTEXT_UNSUPPORTED", "Context is not an approved completion proof observer: " + (probe.Context ?? "<null>") + ".", "Use a Context listed by the completionProbe context schema enum.");

            object typedInput;
            try
            {
                var inputJson = UnityCliSerializer.Serialize(probe.Input);
                var schemaFailure = ValidateTypedInput(definition.RequestType, inputJson);
                if (schemaFailure != null) throw SchemaValidationException(schemaFailure);
                typedInput = UnityCliSerializer.Deserialize(inputJson, definition.RequestType);
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_COMPLETION_PROBE_INPUT_INVALID", "completionProbe Context input is invalid: " + exception.Message, "Validate the input against context describe output before submitting the Tool.");
            }

            ValidateCompletionProbeAssertionSyntax(probe.Assertions);
            var resource = ResolveCompletionProbeResource(definition, typedInput);
            return new CompletionProbePrepared
            {
                Context = definition,
                Input = typedInput,
                Resource = resource,
                SessionId = EditorSessionId(),
                ListenerGeneration = CurrentListenerGeneration()
            };
        }

        /// <summary>只执行已注册 Context delegate；异步或跨帧 Context 不可用于同调用 proof。</summary>
        private static object ReadCompletionProbeSnapshot(UnityCliProjectToolContext parent, RegisteredContext definition, object input, string stage)
        {
            var readContext = new UnityCliProjectToolContext
            {
                RequestId = parent.RequestId,
                ProjectPath = parent.ProjectPath,
                LogPath = parent.LogPath,
                CancellationToken = parent.CancellationToken,
                Wait = null
            };
            var pending = definition.ReadAsync(input, readContext, parent.CancellationToken);
            if (!pending.IsCompleted)
                throw new BridgeCommandException("E_COMPLETION_PROBE_CONTEXT_ASYNC", "completionProbe Context did not finish synchronously during " + stage + " read.", "Use a synchronous built-in proof observer Context.");
            var result = pending.GetAwaiter().GetResult();
            return UnityCliSerializer.Parse(UnityCliSerializer.Serialize(result));
        }

        private static UnityCliProofResourceClaim ResolveCompletionProbeResource(RegisteredContext definition, object input)
        {
            UnityCliProofResourceClaim claim;
            try { claim = definition.ResolveProofResource(input); }
            catch (BridgeCommandException) { throw; }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_COMPLETION_PROBE_RESOURCE_INVALID", "completionProbe could not resolve a canonical resource: " + exception.Message, "Use an exact Context target that resolves one stable resource.");
            }
            if (claim == null || string.IsNullOrWhiteSpace(claim.ResourceKey) || string.IsNullOrWhiteSpace(claim.Identity))
                throw new BridgeCommandException("E_COMPLETION_PROBE_RESOURCE_INVALID", "completionProbe observer returned an incomplete canonical resource claim.", "Use an exact Context target that resolves one stable resource.");
            return claim;
        }

        private static void AcquireCompletionProbeLease(string requestId, string resourceKey)
        {
            lock (CompletionProbeLock)
            {
                string owner;
                if (!string.IsNullOrEmpty(CompletionProbeSessionOwner) && !string.Equals(CompletionProbeSessionOwner, requestId, StringComparison.Ordinal))
                    throw new BridgeCommandException("E_COMPLETION_PROBE_RESOURCE_BUSY", "Another completionProbe mutation owns the Editor session.", "Wait for the current direct Tool call to finish, then submit a new request.", true, 100);
                if (CompletionProbeResourceOwners.TryGetValue(resourceKey, out owner) && !string.Equals(owner, requestId, StringComparison.Ordinal))
                    throw new BridgeCommandException("E_COMPLETION_PROBE_RESOURCE_BUSY", "The completionProbe resource is owned by another request.", "Wait for the current direct Tool call to finish, then submit a new request.", true, 100);
                CompletionProbeSessionOwner = requestId;
                CompletionProbeResourceOwners[resourceKey] = requestId;
            }
        }

        private static void ReleaseCompletionProbeLease(string requestId, string resourceKey)
        {
            lock (CompletionProbeLock)
            {
                string owner;
                if (CompletionProbeResourceOwners.TryGetValue(resourceKey, out owner) && string.Equals(owner, requestId, StringComparison.Ordinal))
                    CompletionProbeResourceOwners.Remove(resourceKey);
                if (string.Equals(CompletionProbeSessionOwner, requestId, StringComparison.Ordinal)) CompletionProbeSessionOwner = null;
            }
        }

        private static void EnsureCompletionProbeSessionContinuity(CompletionProbePrepared prepared, string stage)
        {
            if (!string.Equals(prepared.SessionId, EditorSessionId(), StringComparison.Ordinal)
                || prepared.ListenerGeneration != CurrentListenerGeneration()
                || EditorApplication.isCompiling || EditorApplication.isUpdating || IsPlayModeChanging())
                throw CompletionProbeOutcomeUnknown(stage + "-session", null);
        }

        private static bool SameCompletionProbeResource(UnityCliProofResourceClaim before, UnityCliProofResourceClaim after)
        {
            return before != null && after != null
                && string.Equals(before.ResourceKey, after.ResourceKey, StringComparison.Ordinal)
                && string.Equals(before.Identity, after.Identity, StringComparison.Ordinal)
                && before.InstanceId == after.InstanceId
                && before.SceneHandle == after.SceneHandle;
        }

        private static void ValidateCompletionProbeAssertionSyntax(IList<UnityCliCompletionAssertion> assertions)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assertion in assertions)
            {
                if (assertion == null || string.IsNullOrEmpty(assertion.Path) || assertion.Path[0] != '/')
                    throw new BridgeCommandException("E_COMPLETION_PROBE_ASSERTION_INVALID", "Each completionProbe assertion path must be a non-empty JSON Pointer.", "Use paths such as /transform/position/x.");
                DecodeCompletionProbePointer(assertion.Path);
                var key = assertion.Path + "\n" + assertion.Operator;
                if (!seen.Add(key))
                    throw new BridgeCommandException("E_COMPLETION_PROBE_ASSERTION_INVALID", "completionProbe contains a duplicate path/operator assertion: " + assertion.Path + ".", "Keep one assertion for each path and operator.");
                if (assertion.Operator == "approx")
                {
                    if (!assertion.Tolerance.HasValue || assertion.Tolerance.Value < 0 || double.IsNaN(assertion.Tolerance.Value) || double.IsInfinity(assertion.Tolerance.Value))
                        throw new BridgeCommandException("E_COMPLETION_PROBE_ASSERTION_INVALID", "approx requires a finite non-negative tolerance.", "Pass tolerance with every approx assertion.");
                    RequireCompletionProbeNumber(assertion.Before, "before");
                    RequireCompletionProbeNumber(assertion.After, "after");
                }
                else if (assertion.Tolerance.HasValue)
                {
                    throw new BridgeCommandException("E_COMPLETION_PROBE_ASSERTION_INVALID", "tolerance is only valid for approx assertions.", "Remove tolerance or use operator approx.");
                }
                if (assertion.Operator == "exists" && (!(assertion.Before is bool) || !(assertion.After is bool)))
                    throw new BridgeCommandException("E_COMPLETION_PROBE_ASSERTION_INVALID", "exists before/after values must be booleans.", "Pass true or false for exists assertions.");
                if (assertion.Operator == "lengthEquals")
                {
                    RequireCompletionProbeLength(assertion.Before, "before");
                    RequireCompletionProbeLength(assertion.After, "after");
                }
            }
        }

        private static Dictionary<string, CompletionProbePointerValue> ResolveCompletionProbePointers(object snapshot, IList<UnityCliCompletionAssertion> assertions)
        {
            return assertions.Select(assertion => ResolveCompletionProbePointer(snapshot, assertion.Path))
                .Select((value, index) => new { Index = index, Value = value })
                .ToDictionary(item => item.Index.ToString(CultureInfo.InvariantCulture), item => item.Value, StringComparer.Ordinal);
        }

        private static void ValidateCompletionProbeBefore(IList<UnityCliCompletionAssertion> assertions, Dictionary<string, CompletionProbePointerValue> values)
        {
            var anyAfterUnsatisfied = false;
            for (var index = 0; index < assertions.Count; index++)
            {
                var assertion = assertions[index];
                var observed = values[index.ToString(CultureInfo.InvariantCulture)];
                if (!EvaluateCompletionProbeAssertion(assertion, assertion.Before, observed))
                    throw new BridgeCommandException("E_COMPLETION_PROBE_PRECONDITION_FAILED", "completionProbe before assertion failed at " + assertion.Path + ".", "Read the Context again and submit assertions that match the current resource state.");
                if (!EvaluateCompletionProbeAssertion(assertion, assertion.After, observed)) anyAfterUnsatisfied = true;
            }
            if (!anyAfterUnsatisfied)
                throw new BridgeCommandException("E_COMPLETION_PROBE_ALREADY_SATISFIED", "All completionProbe after assertions already hold before the effect.", "Choose an operation whose declared post-state differs from the current resource state.");
        }

        private static void ValidateCompletionProbeAfter(IList<UnityCliCompletionAssertion> assertions, Dictionary<string, CompletionProbePointerValue> values)
        {
            for (var index = 0; index < assertions.Count; index++)
            {
                var assertion = assertions[index];
                if (!EvaluateCompletionProbeAssertion(assertion, assertion.After, values[index.ToString(CultureInfo.InvariantCulture)]))
                    throw new InvalidOperationException("completionProbe after assertion failed at " + assertion.Path + ".");
            }
        }

        private static bool EvaluateCompletionProbeAssertion(UnityCliCompletionAssertion assertion, object expected, CompletionProbePointerValue observed)
        {
            switch (assertion.Operator)
            {
                case "exists": return (bool)expected == observed.Exists;
                case "equals": return observed.Exists && CompletionProbeJsonEquals(observed.Value, expected);
                case "notEquals": return observed.Exists && !CompletionProbeJsonEquals(observed.Value, expected);
                case "approx": return observed.Exists && Math.Abs(CompletionProbeNumber(observed.Value) - CompletionProbeNumber(expected)) <= assertion.Tolerance.Value;
                case "lengthEquals": return observed.Exists && CompletionProbeLength(observed.Value) == CompletionProbeLength(expected);
                case "contains": return observed.Exists && CompletionProbeContains(observed.Value, expected);
                default: throw new BridgeCommandException("E_COMPLETION_PROBE_ASSERTION_INVALID", "Unsupported completionProbe operator: " + assertion.Operator + ".", "Use an operator listed by the completionProbe schema enum.");
            }
        }

        private static CompletionProbePointerValue ResolveCompletionProbePointer(object root, string pointer)
        {
            var current = root;
            foreach (var segment in DecodeCompletionProbePointer(pointer))
            {
                var dictionary = current as IDictionary<string, object>;
                if (dictionary != null)
                {
                    if (!dictionary.TryGetValue(segment, out current)) return new CompletionProbePointerValue();
                    continue;
                }
                var list = current as IList;
                int index;
                if (list == null || !int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index) || index < 0 || index >= list.Count)
                    return new CompletionProbePointerValue();
                current = list[index];
            }
            return new CompletionProbePointerValue { Exists = true, Value = current };
        }

        private static string[] DecodeCompletionProbePointer(string pointer)
        {
            var parts = pointer.Substring(1).Split('/');
            for (var index = 0; index < parts.Length; index++)
            {
                var part = parts[index];
                for (var cursor = 0; cursor < part.Length; cursor++)
                {
                    if (part[cursor] == '~' && (cursor + 1 >= part.Length || (part[cursor + 1] != '0' && part[cursor + 1] != '1')))
                        throw new BridgeCommandException("E_COMPLETION_PROBE_ASSERTION_INVALID", "completionProbe contains an invalid JSON Pointer escape.", "Escape ~ as ~0 and / as ~1.");
                }
                parts[index] = part.Replace("~1", "/").Replace("~0", "~");
            }
            return parts;
        }

        private static bool CompletionProbeJsonEquals(object left, object right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (IsCompletionProbeNumber(left) && IsCompletionProbeNumber(right)) return CompletionProbeNumber(left) == CompletionProbeNumber(right);
            var leftDictionary = left as IDictionary<string, object>;
            var rightDictionary = right as IDictionary<string, object>;
            if (leftDictionary != null || rightDictionary != null)
            {
                if (leftDictionary == null || rightDictionary == null || leftDictionary.Count != rightDictionary.Count) return false;
                return leftDictionary.All(pair => rightDictionary.ContainsKey(pair.Key) && CompletionProbeJsonEquals(pair.Value, rightDictionary[pair.Key]));
            }
            var leftList = left as IList;
            var rightList = right as IList;
            if (leftList != null || rightList != null)
            {
                if (leftList == null || rightList == null || leftList.Count != rightList.Count) return false;
                for (var index = 0; index < leftList.Count; index++) if (!CompletionProbeJsonEquals(leftList[index], rightList[index])) return false;
                return true;
            }
            return left.GetType() == right.GetType() && left.Equals(right);
        }

        private static bool CompletionProbeContains(object actual, object expected)
        {
            var text = actual as string;
            var expectedText = expected as string;
            if (text != null && expectedText != null) return text.IndexOf(expectedText, StringComparison.Ordinal) >= 0;
            var list = actual as IList;
            if (list != null) return list.Cast<object>().Any(item => CompletionProbeJsonEquals(item, expected));
            var dictionary = actual as IDictionary<string, object>;
            return dictionary != null && expectedText != null && dictionary.ContainsKey(expectedText);
        }

        private static bool IsCompletionProbeNumber(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint
                || value is long || value is ulong || value is float || value is double || value is decimal;
        }

        private static void RequireCompletionProbeNumber(object value, string field)
        {
            if (!IsCompletionProbeNumber(value))
                throw new BridgeCommandException("E_COMPLETION_PROBE_ASSERTION_INVALID", "approx " + field + " must be a JSON number.", "Pass numeric before/after values without string coercion.");
            CompletionProbeNumber(value);
        }

        private static double CompletionProbeNumber(object value)
        {
            if (!IsCompletionProbeNumber(value)) throw new InvalidOperationException("Observed value is not numeric.");
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidOperationException("Observed number is not finite.");
            return number;
        }

        private static void RequireCompletionProbeLength(object value, string field)
        {
            if (!IsCompletionProbeNumber(value) || CompletionProbeNumber(value) < 0 || Math.Floor(CompletionProbeNumber(value)) != CompletionProbeNumber(value))
                throw new BridgeCommandException("E_COMPLETION_PROBE_ASSERTION_INVALID", "lengthEquals " + field + " must be a non-negative integer.", "Pass an integer length as before/after.");
        }

        private static int CompletionProbeLength(object value)
        {
            if (IsCompletionProbeNumber(value)) return checked((int)CompletionProbeNumber(value));
            var text = value as string;
            if (text != null) return text.Length;
            var list = value as IList;
            if (list != null) return list.Count;
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null) return dictionary.Count;
            throw new InvalidOperationException("Observed value has no length.");
        }

        private static string CanonicalCompletionProbeHash(object value)
        {
            var canonical = CanonicalCompletionProbeValue(value);
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes(UnityCliSerializer.Serialize(canonical));
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static object CanonicalCompletionProbeValue(object value)
        {
            var dictionary = value as IDictionary<string, object>;
            if (dictionary != null)
            {
                var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal);
                foreach (var pair in dictionary) sorted[pair.Key] = CanonicalCompletionProbeValue(pair.Value);
                return sorted;
            }
            var list = value as IList;
            if (list != null) return list.Cast<object>().Select(CanonicalCompletionProbeValue).ToList();
            return value;
        }

        private static BridgeCommandException CompletionProbeOutcomeUnknown(string stage, Exception cause)
        {
            return new BridgeCommandException(
                "E_EXECUTION_TIMEOUT",
                "The completionProbe result cannot be confirmed. Check the affected business state.",
                new Dictionary<string, object>
                {
                    { "stage", stage },
                    { "cause", cause == null ? null : cause.Message }
                },
                new[] { "Check the declared Context and inspect the target resource before submitting another request." });
        }
    }
}
#endif
