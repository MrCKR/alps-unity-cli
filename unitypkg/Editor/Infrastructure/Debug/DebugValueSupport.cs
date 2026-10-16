#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>值路径的一个分段：字段名或一维数组下标；解析后同时保留反射身份。</summary>
    internal sealed class DebugValueSegment
    {
        internal string Text;
        internal FieldInfo Field;
        internal int ArrayIndex = -1;
        internal Type ValueType;
        internal bool StaticField;

        internal bool IsArrayElement { get { return ArrayIndex >= 0; } }
    }

    /// <summary>
    /// 一次根加路径解析的完整计划。
    /// 只在主线程构建；构建后不再改变，条件恢复记录直接复用它重新定位目标。
    /// </summary>
    internal sealed class DebugValuePlan
    {
        internal string RootKind;
        internal string RootIdentity;
        internal string RootTypeName;
        internal string RootDeclaringTypeName;
        internal object RootTarget;
        internal FieldInfo RootStaticField;
        internal Type RootValueType;
        internal readonly List<DebugValueSegment> Segments = new List<DebugValueSegment>();
        internal string PathText;
        internal Type LeafType;
        internal string LeafDeclaringTypeName;
        internal bool StructWriteBack;
    }

    /// <summary>
    /// 写计划：解析结果加上 effect 前的全部判定结果。
    /// effect 阶段只消费本对象，不再做任何输入、目标或前置条件判定。
    /// </summary>
    internal sealed class DebugValueWritePlan
    {
        internal DebugValuePlan Plan;
        internal object ConvertedValue;

        /// <summary>effect 前读到的原值；条件恢复记录与结果的 previousValue 都用它，effect 后不重新读取。</summary>
        internal object OriginalValue;

        internal bool GuardEvaluated;
        internal bool GuardSatisfied;
        internal string GuardConflictDetail;

        /// <summary>effect 前解析并预算过的句柄目标；为 null 表示本次不建立句柄。</summary>
        internal object HandleTarget;

        /// <summary>句柄是否使用强引用；只在 HandleTarget 非 null 时有意义。</summary>
        internal bool CreateStrongHandle;
    }

    /// <summary>
    /// debug.value.read 与 debug.value.write 的唯一解析与效果 owner。
    ///
    /// 规则：
    /// - 根只能是精确静态字段、精确 Unity selector 或当前托管对象句柄；路径分段只能是字段名或一维数组下标。
    /// - 纯读取路径不执行属性 getter、不调用任意 ToString、不运行用户序列化器或枚举器，
    ///   也不触发未初始化类型的类型初始化；无法证明安全时以稳定错误拒绝并路由到显式代码执行。
    /// - 写入使用同一解析结果做类型检查的结构化写入；值类型副本链显式写回，绝不静默丢失。
    /// - 本类不注册 update 回调，不做文件 I/O，不改变任何派发状态。
    /// </summary>
    internal static class DebugValueSupport
    {
        private const string PathSeparator = ".";

        /// <summary>
        /// 解析链在根本身处就为 null 时 nullAtPath 使用的稳定标记。
        /// 根不属于 fieldPath，空串表示“链没有在任何分段处断开”，因此根本身的 null 必须有自己的表示，
        /// 否则“叶子值本身就是 null”和“目标已不存在”无法区分，条件恢复就会把失效目标误报成冲突。
        /// 该标记只用于内部判定；公开读取结果按自己的契约报告根本身的值。
        /// </summary>
        internal const string RootNullMarker = "<root>";

        // ------------------------------------------------------------------
        // 读取
        // ------------------------------------------------------------------

        /// <summary>debug.value.read 的完整读取：解析、读取、投影、时钟溯源与可选弱句柄。</summary>
        internal static DebugValueReadResult Read(DebugValueReadRequest request, UnityCliRequestBudget budget)
        {
            if (request == null) throw SchemaInvalid("rootKind", "static-field, selector or handle", "missing");
            var plan = ResolvePlan(request.RootKind, request.TypeName, request.FieldName, SelectorOf(request), request.HandleId, request.FieldPath, budget, out var usedHandle);
            object value;
            string failure;
            string nullAtPath;
            if (!TryReadPlanValue(plan, out value, out failure, out nullAtPath)) throw StaleOrUnresolvable(plan, failure);
            // 根本身就是 null 时 fieldPath 从未被遍历：公开读取契约把这次观测报告为实值 null
            // （nullReason actual-null，且 nullAtPath 为空，因为根本身不属于 fieldPath）。
            // 写入与条件恢复对同一事实取相反解释：链已不可解析，没有可比较或可写的对象。
            var nullAtRoot = string.Equals(nullAtPath, RootNullMarker, StringComparison.Ordinal);
            string projection;
            bool valueTruncated;
            var projected = DebugValueProjection.Project(value, out projection, out valueTruncated);

            DebugLogicalClockView logicalClock = null;
            DebugHandleView createdHandle = null;
            int sessionUsed;
            int globalUsed;
            var sessionId = request.SessionId ?? string.Empty;
            if (!string.IsNullOrEmpty(request.LogicalClockName))
            {
                logicalClock = PublishLogicalClock(request.LogicalClockName, value, plan);
            }
            if (!string.IsNullOrEmpty(request.CreateHandle) && !string.Equals(request.CreateHandle, "none", StringComparison.Ordinal))
            {
                RequireOpenSession(sessionId);
                var handleTarget = RootValue(plan);
                if (handleTarget == null || handleTarget.GetType().IsValueType)
                    throw Refuse(
                        "handle-requires-reference-root",
                        "A handle denotes a current managed object, so the resolved root must be a reference type; this root is null or a value type.",
                        plan,
                        new[] { "Read a field whose value is a reference type, or read this value without createHandle." });
                createdHandle = CreateHandle(sessionId, handleTarget, false, plan, out sessionUsed, out globalUsed);
            }
            else
            {
                sessionUsed = string.IsNullOrEmpty(sessionId) ? 0 : DebugHandleRegistry.SessionCount(sessionId);
                globalUsed = DebugHandleRegistry.GlobalCount;
            }

            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var mainThread = DebugEventClocks.IsCurrentThreadMainThread();
            return new DebugValueReadResult
            {
                RootKind = plan.RootKind,
                RootIdentity = plan.RootIdentity,
                RootTypeName = plan.RootTypeName,
                Path = plan.PathText,
                DeclaringTypeName = plan.LeafDeclaringTypeName,
                ValueTypeName = plan.LeafType == null ? string.Empty : (plan.LeafType.FullName ?? plan.LeafType.Name),
                ValueProjection = projection,
                Value = projected,
                ValueIsNull = value == null,
                NullReason = value == null ? (string.IsNullOrEmpty(nullAtPath) || nullAtRoot ? "actual-null" : "intermediate-null") : string.Empty,
                NullAtPath = value == null && !nullAtRoot ? nullAtPath ?? string.Empty : string.Empty,
                ValueTruncated = valueTruncated,
                ProjectionNote = DebugValueProjection.Note,
                ThreadId = threadId,
                MainThread = mainThread,
                Clocks = DebugClockProvenance.CaptureReadClocks(logicalClock),
                LogicalClocks = DebugLogicalClocks.Snapshot(),
                Consistency = "unsynchronized",
                ConsistencyNote = "Each path segment is read with one reflection read; no business lock is held and no cross-field atomicity is claimed.",
                Handle = createdHandle ?? usedHandle,
                Handles = DebugHandleRegistry.Budget(sessionId, sessionUsed, globalUsed),
                SessionId = sessionId,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                RuntimeId = DebugSessionHost.RuntimeId(),
                DomainGeneration = DebugSessionHost.DomainGeneration()
            };
        }

        // ------------------------------------------------------------------
        // 写入
        // ------------------------------------------------------------------

        /// <summary>effect 前的写入校验：先确认 session 处于 open，再执行与 session 无关的全部目标与值判定。</summary>
        internal static DebugValueWritePlan PrepareWrite(DebugValueWriteRequest request, UnityCliRequestBudget budget)
        {
            if (request == null) throw SchemaInvalid("rootKind", "static-field, selector or handle", "missing");
            RequireOpenSession(request.SessionId);
            return ValidateWrite(request, budget);
        }

        /// <summary>
        /// 一次写入的全部 effect 前判定：解析根与有界路径、字段可写性、提交值字节与类型、句柄目标与预算、
        /// 原值读取与 expectedValue 前置条件。
        ///
        /// 本方法是纯判定：不改变任何目标状态，也不提交 effect barrier。
        /// 因此 effect 之后只可能剩下"写入可能已部分生效但无法证明完整"的失败，
        /// 不存在任何输入类、目标类或前置条件类失败。
        /// </summary>
        internal static DebugValueWritePlan ValidateWrite(DebugValueWriteRequest request, UnityCliRequestBudget budget)
        {
            if (request == null) throw SchemaInvalid("rootKind", "static-field, selector or handle", "missing");
            var plan = ResolvePlan(request.RootKind, request.TypeName, request.FieldName, SelectorOf(request), request.HandleId, request.FieldPath, budget, out _);
            var jsonBytes = request.Value == null ? 0L : Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(request.Value));
            if (jsonBytes > DebugLimits.MaxStateBytes)
            {
                throw new BridgeCommandException(
                    "E_DEBUG_LIMIT_EXCEEDED",
                    "The submitted JSON value exceeds the declared per-value byte budget, so nothing was written.",
                    new Dictionary<string, object>
                    {
                        { "limit", "value-json-bytes" }, { "used", jsonBytes }, { "max", (long)DebugLimits.MaxStateBytes }
                    },
                    new[]
                    {
                        "Send a smaller JSON value; tools describe debug.value.write reports the per-value byte budget."
                    });
            }

            var write = new DebugValueWritePlan { Plan = plan };
            if (plan.Segments.Count == 0 && plan.RootStaticField == null)
                throw Refuse(
                    "root-object-write-not-supported",
                    "Writing a whole resolved Unity selector object is not a structured field write; address an exact field path instead.",
                    plan,
                    new[] { "Pass fieldPath naming the exact field to write." });
            // 不可写目标必须在 barrier 之前拒绝：越过 barrier 后才发现写入不可能，会让调用方误以为状态可能已变化。
            RequireWritableTargets(plan);
            // 句柄目标与预算同样在 barrier 之前判定；真正的创建仍在 barrier 之后。
            PrepareHandle(request, plan, write);
            write.ConvertedValue = ConvertWriteValue(request, plan);

            object current;
            string readFailure;
            string nullAtPath;
            if (!TryReadPlanValue(plan, out current, out readFailure, out nullAtPath))
                throw StaleOrUnresolvable(plan, readFailure);
            // 目标有效性先于一切值判定：链在任意一段（含根）为 null 就没有可写的对象，
            // 这必须在 effect barrier 之前拒绝，否则调用方会收到一个从未开始的“可能已部分生效”报告。
            if (!string.IsNullOrEmpty(nullAtPath))
                throw Refuse(
                    "path-member-null",
                    DescribeNullChain(plan, nullAtPath),
                    plan,
                    new[] { "Read the current value with tools run debug.value.read, then initialize that member first." });
            write.OriginalValue = current;

            if (request.GuardByExpectedValue)
            {
                bool equal;
                string refusal;
                if (!TryCompareGuard(plan.LeafType, current, request.ExpectedValue, out equal, out refusal))
                    throw Refuse(refusal, "The expectedValue guard cannot be evaluated without running user code for this member type.", plan, new[] { "Guard only primitives, strings, enums, decimal, DateTime, TimeSpan, Guid or Unity object references; otherwise use tools run debug.code.execute for a conditional write." });
                write.GuardEvaluated = true;
                write.GuardSatisfied = equal;
                if (!equal)
                    write.GuardConflictDetail = "The current value does not equal expectedValue, so nothing was written.";
            }
            return write;
        }

        /// <summary>
        /// 写入目标必须真的可写。const（literal）与 readonly（init-only）字段不是可变状态：
        /// 反射写入要么被拒绝，要么在语义上违反声明；两者都必须在 effect barrier 之前以稳定规则拒绝。
        ///
        /// 会被写入的字段是：根静态字段（空路径，或根为值类型时的显式写回）与每一个非数组下标分段的字段。
        /// 数组下标分段写的是数组元素本身，不写字段。
        /// </summary>
        /// <summary>
        /// 解析链为 null 时的写入前规则文本。根本身为 null 与中间分段为 null 是同一类 fact 的两种位置，
        /// 都必须给出可证明的原因，且都不能被当成 effect 后的不确定结果。
        /// </summary>
        private static string DescribeNullChain(DebugValuePlan plan, string nullAtPath)
        {
            if (string.Equals(nullAtPath, RootNullMarker, StringComparison.Ordinal))
            {
                return "The root " + (plan == null ? "target" : plan.RootIdentity) + " is null, so the path "
                    + (plan == null ? string.Empty : plan.PathText) + " has no object to traverse and nothing could be written; a structured write never creates intermediate objects.";
            }
            return "The path member " + nullAtPath + " is null, so there is no object to write; a structured write never creates intermediate objects.";
        }

        private static void RequireWritableTargets(DebugValuePlan plan)
        {
            if (plan.RootStaticField != null && (plan.Segments.Count == 0 || (plan.RootValueType != null && plan.RootValueType.IsValueType)))
                RequireWritableField(plan.RootStaticField, plan);
            for (var index = 0; index < plan.Segments.Count; index++)
            {
                var segment = plan.Segments[index];
                if (segment.IsArrayElement || segment.Field == null) continue;
                RequireWritableField(segment.Field, plan);
            }
        }

        /// <summary>单个字段的稳定不可写拒绝；literal 与 init-only 用不同规则名区分，便于调用方机器分支。</summary>
        private static void RequireWritableField(FieldInfo field, DebugValuePlan plan)
        {
            var name = (field.DeclaringType == null ? string.Empty : (field.DeclaringType.FullName ?? field.DeclaringType.Name)) + "." + field.Name;
            if (field.IsLiteral)
                throw FieldNotWritable(
                    "field-is-literal",
                    "Field " + name + " is a constant (const) field: its value belongs to the compiled type and no write can change it.",
                    plan);
            if (field.IsInitOnly)
                throw FieldNotWritable(
                    "field-is-init-only",
                    "Field " + name + " is init-only (readonly), so it is not mutable state after construction and a structured write must not change it.",
                    plan);
        }

        /// <summary>解析句柄目标并在 effect 前判定句柄预算；目标不是引用类型或预算耗尽都在这里拒绝。</summary>
        private static void PrepareHandle(DebugValueWriteRequest request, DebugValuePlan plan, DebugValueWritePlan write)
        {
            var requestedHandle = request.CreateHandle ?? "none";
            if (string.IsNullOrEmpty(requestedHandle) || string.Equals(requestedHandle, "none", StringComparison.Ordinal)) return;
            var target = RootValue(plan);
            if (target == null || target.GetType().IsValueType)
                throw Refuse(
                    "handle-requires-reference-root",
                    "A handle denotes a current managed object, so the resolved root must be a reference type; this root is null or a value type.",
                    plan,
                    new[] { "Address a field whose value is a reference type, or write without createHandle." });
            int sessionUsed;
            int globalUsed;
            var budgetFailure = DebugHandleRegistry.CanCreate(request.SessionId, out sessionUsed, out globalUsed);
            if (budgetFailure != null) throw HandleBudgetExceeded(budgetFailure, sessionUsed, globalUsed);
            write.HandleTarget = target;
            write.CreateStrongHandle = string.Equals(requestedHandle, "strong", StringComparison.Ordinal);
        }

        /// <summary>
        /// 执行写入的唯一 effect：先建立 effect 前已解析并预算过的句柄，再写入并在需要时登记条件恢复记录。
        /// 输入、目标、可写性、类型与前置条件判定全部已在 ValidateWrite 完成，
        /// 因此这里唯一的失败形态是“已开始写入但无法证明完整”，必须以 effect 后不确定语义报告。
        /// </summary>
        internal static DebugValueWriteResult ApplyWrite(DebugValueWritePlan write, DebugValueWriteRequest request)
        {
            var plan = write.Plan;
            var sessionId = request.SessionId ?? string.Empty;
            DebugHandleView handle = null;
            int sessionUsed;
            int globalUsed;
            if (write.HandleTarget != null)
            {
                handle = CreateHandle(sessionId, write.HandleTarget, write.CreateStrongHandle, plan, out sessionUsed, out globalUsed);
            }
            else
            {
                sessionUsed = string.IsNullOrEmpty(sessionId) ? 0 : DebugHandleRegistry.SessionCount(sessionId);
                globalUsed = DebugHandleRegistry.GlobalCount;
            }

            var applied = !write.GuardEvaluated || write.GuardSatisfied;
            DebugValueRestoreView restore = null;
            if (applied)
            {
                string writeFailure;
                if (!TryWritePlanValue(plan, write.ConvertedValue, out writeFailure))
                    throw PostEffectWriteFailure(plan, writeFailure);
                if (request.RegisterRestore)
                    restore = DebugValueRestore.Register(plan, sessionId, write.OriginalValue, write.ConvertedValue, plan.PathText, plan.LeafType == null ? string.Empty : (plan.LeafType.FullName ?? plan.LeafType.Name));
            }

            var previousProjection = DebugValueProjection.Project(write.OriginalValue, out _, out _);
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            return new DebugValueWriteResult
            {
                Outcome = applied ? "applied" : "guard-mismatch",
                Applied = applied,
                GuardEvaluated = write.GuardEvaluated,
                GuardSatisfied = write.GuardSatisfied,
                EffectCategory = "structured-intervention",
                EffectNote = applied
                    ? "One field was written through reflection with a type-checked value. This is a structured intervention, not a property setter and not a method invocation; it is not automatically rolled back and it does not undo other writers."
                    : write.GuardConflictDetail,
                RootKind = plan.RootKind,
                RootIdentity = plan.RootIdentity,
                RootTypeName = plan.RootTypeName,
                Path = plan.PathText,
                DeclaringTypeName = plan.LeafDeclaringTypeName,
                MemberTypeName = plan.LeafType == null ? string.Empty : (plan.LeafType.FullName ?? plan.LeafType.Name),
                PreviousValue = previousProjection,
                WrittenValue = applied ? DebugValueProjection.ProjectValueOnly(write.ConvertedValue) : null,
                StructWriteBack = plan.StructWriteBack,
                StructWriteBackNote = plan.StructWriteBack
                    ? "An intermediate path member is a value type, so the write went through an explicit boxed copy chain and was written back into its parent; the update cannot be silently lost."
                    : "No intermediate value-type copy was involved.",
                ThreadId = threadId,
                MainThread = DebugEventClocks.IsCurrentThreadMainThread(),
                Clocks = DebugClockProvenance.CaptureReadClocks(null),
                Restore = restore,
                Handle = handle,
                Handles = DebugHandleRegistry.Budget(sessionId, sessionUsed, globalUsed),
                SessionId = sessionId,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                RuntimeId = DebugSessionHost.RuntimeId(),
                DomainGeneration = DebugSessionHost.DomainGeneration()
            };
        }

        // ------------------------------------------------------------------
        // 解析
        // ------------------------------------------------------------------

        /// <summary>解析根与有界路径；任何不安全或无界情形都在这里以稳定错误拒绝。</summary>
        internal static DebugValuePlan ResolvePlan(string rootKind, string typeName, string fieldName, DebugValueSelector selector, string handleId, IReadOnlyList<string> fieldPath, UnityCliRequestBudget budget, out DebugHandleView usedHandle)
        {
            usedHandle = null;
            var plan = new DebugValuePlan { RootKind = rootKind ?? string.Empty };
            if (string.Equals(rootKind, "static-field", StringComparison.Ordinal))
            {
                var type = UnityCliBridge.TypeSchemaGenerator.ResolveType(typeName, budget);
                if (type == null) throw new BridgeCommandException("E_TYPE_NOT_FOUND", "Type not found: " + typeName, "Pass a full type name such as MyNamespace.MyType.");
                var field = FindField(type, fieldName, true);
                if (field == null) throw MemberRefusal(type, fieldName, plan);
                RequireNoStaticInitializer(field, plan);
                plan.RootStaticField = field;
                plan.RootTarget = null;
                plan.RootValueType = field.FieldType;
                plan.RootDeclaringTypeName = field.DeclaringType == null ? string.Empty : (field.DeclaringType.FullName ?? field.DeclaringType.Name);
                plan.RootTypeName = plan.RootDeclaringTypeName;
                plan.RootIdentity = "static-field:" + plan.RootDeclaringTypeName + "." + field.Name;
            }
            else if (string.Equals(rootKind, "selector", StringComparison.Ordinal))
            {
                var resolved = selector == null ? null : ResolveSelector(selector, budget);
                if (resolved == null)
                    throw new BridgeCommandException("E_OBJECTREF_REQUIRED", "A selector root requires instanceId, assetPath/guid, an exact hierarchy path or a component selector.", "Pass the selector fields that identify the exact Unity object, or use rootKind static-field or handle.");
                plan.RootTarget = resolved;
                plan.RootValueType = resolved.GetType();
                plan.RootTypeName = resolved.GetType().FullName ?? resolved.GetType().Name;
                plan.RootDeclaringTypeName = plan.RootTypeName;
                plan.RootIdentity = "selector:" + plan.RootTypeName + "#" + resolved.GetInstanceID().ToString(CultureInfo.InvariantCulture);
            }
            else if (string.Equals(rootKind, "handle", StringComparison.Ordinal))
            {
                object target;
                DebugHandleView view;
                var status = DebugHandleRegistry.Resolve(handleId, DebugSessionHost.RuntimeId(), DebugSessionHost.DomainGeneration(), out target, out view);
                if (status == "missing")
                {
                    throw new BridgeCommandException(
                        "E_DEBUG_TARGET_STALE",
                        "No live managed object handle has that handleId in this runtime and domain generation, so no read or write was performed.",
                        new Dictionary<string, object>
                        {
                            { "handleId", handleId ?? string.Empty }, { "handleState", "missing" }, { "reason", "unknown-or-released-handle" },
                            { "runtimeId", DebugSessionHost.RuntimeId() }, { "domainGeneration", DebugSessionHost.DomainGeneration() },
                            { "knownHandles", (long)DebugHandleRegistry.GlobalCount }
                        },
                        new[] { "Read the root again with tools run debug.value.read and take the handleId it returns for this Editor session." });
                }
                if (status != "live")
                {
                    // 失效句柄必须报告 stale，绝不把"GC 后找不到"当成"字段为 null"。
                    throw new BridgeCommandException(
                        "E_DEBUG_TARGET_STALE",
                        "The handle target is no longer usable; its state is reported instead of returning a null field value.",
                        new Dictionary<string, object>
                        {
                            { "handleId", handleId ?? string.Empty }, { "handleState", "stale" },
                            { "reason", view == null ? "target-invalid" : view.StaleReason },
                            { "handleRuntimeId", view == null ? string.Empty : view.RuntimeId },
                            { "handleDomainGeneration", view == null ? string.Empty : view.DomainGeneration },
                            { "runtimeId", DebugSessionHost.RuntimeId() }, { "domainGeneration", DebugSessionHost.DomainGeneration() }
                        },
                        new[] { "Re-read the root with tools run debug.value.read; a handle never rebinds across a runtime or domain change." });
                }
                usedHandle = view;
                plan.RootTarget = target;
                plan.RootValueType = target.GetType();
                plan.RootTypeName = target.GetType().FullName ?? target.GetType().Name;
                plan.RootDeclaringTypeName = plan.RootTypeName;
                plan.RootIdentity = "handle:" + handleId;
            }
            else
            {
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "rootKind is invalid: expected static-field, selector or handle.",
                    new Dictionary<string, object> { { "path", "$.rootKind" }, { "expected", "static-field, selector or handle" }, { "actual", rootKind ?? "null" } },
                    new[] { "Run unitycli tools describe debug.value.read to read the root contract." });
            }

            ParsePath(plan, plan.RootValueType, fieldPath);
            return plan;
        }

        /// <summary>解析有界路径并记录每个分段的反射身份；分段类型由前一分段决定。</summary>
        private static void ParsePath(DebugValuePlan plan, Type rootValueType, IReadOnlyList<string> segments)
        {
            plan.LeafType = rootValueType;
            plan.LeafDeclaringTypeName = plan.RootDeclaringTypeName;
            plan.PathText = string.Empty;
            if (segments == null || segments.Count == 0) return;
            if (segments.Count > DebugLimits.MaxValuePathLength)
            {
                throw new BridgeCommandException(
                    "E_DEBUG_LIMIT_EXCEEDED",
                    "The field path exceeds the declared segment budget, so nothing was read or written.",
                    new Dictionary<string, object>
                    {
                        { "limit", "value-path-segments" }, { "used", (long)segments.Count }, { "max", (long)DebugLimits.MaxValuePathLength }
                    },
                    new[] { "Address a shorter field path; tools describe debug.value.read reports the path budget." });
            }
            var currentType = rootValueType;
            var text = new StringBuilder();
            for (var index = 0; index < segments.Count; index++)
            {
                var raw = segments[index];
                if (string.IsNullOrEmpty(raw) || raw.Trim().Length == 0)
                {
                    throw new BridgeCommandException(
                        "E_SCHEMA_INVALID",
                        "A field path segment must not be empty.",
                        new Dictionary<string, object> { { "path", "$.fieldPath[" + index.ToString(CultureInfo.InvariantCulture) + "]" }, { "expected", "field name or one-dimensional array index" }, { "actual", raw ?? "null" } },
                        new[] { "Pass a field name, or an array index such as 0." });
                }
                var segment = new DebugValueSegment { Text = raw, ValueType = currentType };
                if (currentType != null && currentType.IsArray && currentType.GetArrayRank() == 1)
                {
                    int arrayIndex;
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out arrayIndex) || arrayIndex < 0)
                    {
                        throw Refuse(
                            "array-index-required",
                            "The current value is a one-dimensional array, so this path segment must be a non-negative array index; reading a collection by member name would run user code.",
                            plan,
                            new[] { "Pass the array index, or read the collection element through tools run debug.code.execute." });
                    }
                    segment.ArrayIndex = arrayIndex;
                    segment.ValueType = currentType.GetElementType() ?? typeof(object);
                    currentType = segment.ValueType;
                }
                else
                {
                    if (currentType == null) throw Refuse("path-type-unknown", "The current path value has no declared type to resolve the next field on.", plan, null);
                    var field = FindField(currentType, raw, false);
                    if (field == null) throw MemberRefusal(currentType, raw, plan);
                    RequireNoStaticInitializer(field, plan);
                    segment.Field = field;
                    segment.StaticField = field.IsStatic;
                    segment.ValueType = field.FieldType;
                    currentType = field.FieldType;
                    plan.LeafDeclaringTypeName = field.DeclaringType == null ? string.Empty : (field.DeclaringType.FullName ?? field.DeclaringType.Name);
                }
                if (text.Length > 0) text.Append(PathSeparator);
                text.Append(segment.IsArrayElement ? "[" + segment.Text + "]" : segment.Text);
                plan.Segments.Add(segment);
            }
            plan.PathText = text.ToString();
            plan.LeafType = currentType;
            plan.StructWriteBack = plan.RootValueType != null && (plan.RootValueType.IsValueType || IntermediateValueTypeCopy(plan));
        }

        private static bool IntermediateValueTypeCopy(DebugValuePlan plan)
        {
            for (var index = 0; index < plan.Segments.Count - 1; index++)
            {
                if (plan.Segments[index].ValueType != null && plan.Segments[index].ValueType.IsValueType) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 读取与写入链
        // ------------------------------------------------------------------

        /// <summary>
        /// 读取计划当前的值。返回 false 表示链已不可解析（目标失效或次数不匹配）；
        /// nullAtPath 非空表示链在这里就为 null，那是实际观测结果而不是失败：
        /// 在分段处断开时给出 fieldPath 上最后一个可用的前缀，根本身为 null 时给出 RootNullMarker。
        /// </summary>
        internal static bool TryReadPlanValue(DebugValuePlan plan, out object value, out string failure, out string nullAtPath)
        {
            value = null;
            failure = null;
            nullAtPath = string.Empty;
            if (plan == null)
            {
                failure = "no resolved target plan";
                return false;
            }
            object current;
            try
            {
                current = RootValue(plan);
            }
            catch (Exception exception)
            {
                failure = exception.GetType().Name;
                return false;
            }
            for (var index = 0; index < plan.Segments.Count; index++)
            {
                var segment = plan.Segments[index];
                if (segment.IsArrayElement)
                {
                    var array = current as Array;
                    if (array == null || array.Rank != 1)
                    {
                        failure = "segment " + index.ToString(CultureInfo.InvariantCulture) + " no longer resolves to a one-dimensional array";
                        return false;
                    }
                    if (segment.ArrayIndex >= array.Length)
                    {
                        failure = "segment " + index.ToString(CultureInfo.InvariantCulture) + " is out of range for the current array length";
                        return false;
                    }
                    current = array.GetValue(segment.ArrayIndex);
                    continue;
                }
                if (current == null && !segment.StaticField)
                {
                    // index 0 表示根本身就是 null：根不是 fieldPath 的一部分，因此不能与分段位置共用空串。
                    nullAtPath = index == 0 ? RootNullMarker : PlanPathUpTo(plan, index);
                    value = null;
                    return true;
                }
                try
                {
                    current = segment.Field.GetValue(segment.StaticField ? null : current);
                }
                catch (Exception exception)
                {
                    failure = "segment " + index.ToString(CultureInfo.InvariantCulture) + " could not be read: " + exception.GetType().Name;
                    return false;
                }
            }
            value = current;
            return true;
        }

        /// <summary>
        /// 写入计划当前的值。值类型中间分段通过显式装箱副本链写回，因此 struct 字段写入不会被静默丢弃。
        /// </summary>
        internal static bool TryWritePlanValue(DebugValuePlan plan, object newValue, out string failure)
        {
            failure = null;
            if (plan == null)
            {
                failure = "no resolved target plan";
                return false;
            }
            if (plan.Segments.Count == 0)
            {
                if (plan.RootStaticField == null)
                {
                    failure = "writing the resolved root object itself is not a structured field write";
                    return false;
                }
                try
                {
                    plan.RootStaticField.SetValue(null, newValue);
                }
                catch (Exception exception)
                {
                    // 根静态字段写入失败只能报告“可能已部分生效”，不能被当成 effect 前的拒绝。
                    failure = "the root static field " + plan.RootStaticField.Name + " could not be written: " + exception.GetType().Name;
                    return false;
                }
                return true;
            }
            var containers = new object[plan.Segments.Count];
            object current;
            try
            {
                current = RootValue(plan);
            }
            catch (Exception exception)
            {
                failure = exception.GetType().Name;
                return false;
            }
            for (var index = 0; index < plan.Segments.Count; index++)
            {
                var segment = plan.Segments[index];
                containers[index] = current;
                if (segment.IsArrayElement)
                {
                    var array = current as Array;
                    if (array == null || array.Rank != 1) { failure = "segment " + index.ToString(CultureInfo.InvariantCulture) + " no longer resolves to a one-dimensional array"; return false; }
                    if (segment.ArrayIndex >= array.Length) { failure = "segment " + index.ToString(CultureInfo.InvariantCulture) + " is out of range for the current array length"; return false; }
                    current = array.GetValue(segment.ArrayIndex);
                    continue;
                }
                if (current == null && !segment.StaticField) { failure = "segment " + index.ToString(CultureInfo.InvariantCulture) + " is null, so the target no longer resolves"; return false; }
                current = segment.Field.GetValue(segment.StaticField ? null : current);
            }
            if (plan.LeafType != null && !AcceptsValue(plan.LeafType, newValue))
            {
                failure = "the value does not match the field type " + (plan.LeafType.FullName ?? plan.LeafType.Name);
                return false;
            }
            object child = newValue;
            for (var index = plan.Segments.Count - 1; index >= 0; index--)
            {
                var segment = plan.Segments[index];
                var container = containers[index];
                if (segment.IsArrayElement)
                {
                    var array = container as Array;
                    if (array == null) { failure = "segment " + index.ToString(CultureInfo.InvariantCulture) + " no longer resolves to a one-dimensional array"; return false; }
                    array.SetValue(child, segment.ArrayIndex);
                    child = container;
                    continue;
                }
                try
                {
                    segment.Field.SetValue(segment.StaticField ? null : container, child);
                }
                catch (Exception exception)
                {
                    failure = "segment " + index.ToString(CultureInfo.InvariantCulture) + " could not be written: " + exception.GetType().Name;
                    return false;
                }
                child = container;
            }
            if (plan.RootStaticField != null && plan.RootValueType != null && plan.RootValueType.IsValueType)
                plan.RootStaticField.SetValue(null, child);
            return true;
        }

        /// <summary>规范化的投影文本；条件恢复与前置条件比较共用同一比较口径。</summary>
        internal static string Canonical(object value)
        {
            return UnityCliSerializer.Serialize(DebugValueProjection.ProjectValueOnly(value));
        }

        // ------------------------------------------------------------------
        // 辅助
        // ------------------------------------------------------------------

        private static object RootValue(DebugValuePlan plan)
        {
            return plan.RootStaticField != null ? plan.RootStaticField.GetValue(null) : plan.RootTarget;
        }

        private static string PlanPathUpTo(DebugValuePlan plan, int index)
        {
            var text = new StringBuilder();
            for (var cursor = 0; cursor < index && cursor < plan.Segments.Count; cursor++)
            {
                if (text.Length > 0) text.Append(PathSeparator);
                text.Append(plan.Segments[cursor].IsArrayElement ? "[" + plan.Segments[cursor].Text + "]" : plan.Segments[cursor].Text);
            }
            return text.ToString();
        }

        private static UnityEngine.Object ResolveSelector(DebugValueSelector selector, UnityCliRequestBudget budget)
        {
            if (selector.ComponentInstanceId.HasValue || selector.ComponentIndex.HasValue
                || !string.IsNullOrEmpty(selector.Component) || !string.IsNullOrEmpty(selector.ComponentType))
                return UnityCliBridge.ObjectRefResolver.ResolveComponent(selector, budget);
            return UnityCliBridge.ObjectRefResolver.ResolveObject(selector, budget);
        }

        private static DebugValueSelector SelectorOf(DebugValueReadRequest request)
        {
            return request == null ? null : new DebugValueSelector
            {
                InstanceIdValue = request.InstanceId,
                PathValue = request.Path,
                AssetPathValue = request.AssetPath,
                GuidValue = request.Guid,
                ComponentInstanceIdValue = request.ComponentInstanceId,
                ComponentIndexValue = request.ComponentIndex,
                ComponentValue = request.Component,
                ComponentTypeValue = request.ComponentType
            };
        }

        private static DebugValueSelector SelectorOf(DebugValueWriteRequest request)
        {
            return request == null ? null : new DebugValueSelector
            {
                InstanceIdValue = request.InstanceId,
                PathValue = request.Path,
                AssetPathValue = request.AssetPath,
                GuidValue = request.Guid,
                ComponentInstanceIdValue = request.ComponentInstanceId,
                ComponentIndexValue = request.ComponentIndex,
                ComponentValue = request.Component,
                ComponentTypeValue = request.ComponentType
            };
        }

        /// <summary>按精确名字查找字段；找不到时返回 null，由调用方区分属性与不存在。</summary>
        internal static FieldInfo FindField(Type type, string name, bool staticRoot)
        {
            if (type == null || string.IsNullOrEmpty(name)) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            var field = type.GetField(name, flags);
            if (field != null) return staticRoot && !field.IsStatic ? null : field;
            for (var baseType = type.BaseType; baseType != null; baseType = baseType.BaseType)
            {
                field = baseType.GetField(name, flags);
                if (field == null) continue;
                if (staticRoot && !field.IsStatic) return null;
                return field;
            }
            return null;
        }

        /// <summary>成员不是可安全读取的实例/静态字段时的稳定拒绝。</summary>
        private static BridgeCommandException MemberRefusal(Type type, string name, DebugValuePlan plan)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            var typeName = type.FullName ?? type.Name;
            if (property != null)
                return Refuse(
                    "property-getter-not-executed",
                    "Member " + name + " on " + typeName + " is a property, and a pure read must not execute a property getter.",
                    plan,
                    new[] { "Read an exact field instead; run tools run debug.type.schema to list the real fields.", "Use tools run debug.code.execute when the property getter must actually run." });
            return Refuse(
                "member-not-a-field",
                "No field named " + name + " exists on " + typeName + ".",
                plan,
                new[] { "Run tools run debug.type.schema to read the declared fields of " + typeName + "." });
        }

        /// <summary>静态字段访问不得触发类型初始化：只有不存在静态构造函数的类型才可证明安全。</summary>
        private static void RequireNoStaticInitializer(FieldInfo field, DebugValuePlan plan)
        {
            if (field == null || !field.IsStatic) return;
            var declaringType = field.DeclaringType;
            if (declaringType == null || declaringType.TypeInitializer == null) return;
            var typeName = declaringType.FullName ?? declaringType.Name;
            throw Refuse(
                "static-type-not-initialized",
                "Type " + typeName + " declares a static constructor, so touching its static field " + field.Name
                + " could run type initialisation; a pure read or structured write must not trigger it and cannot prove it already ran.",
                plan,
                new[] { "Use tools run debug.code.execute to read or write this static field explicitly, accepting that type initialisation may run." });
        }

        /// <summary>写入值的类型检查；不匹配时返回稳定 schema 错误而不是静默转换失败。</summary>
        private static object ConvertWriteValue(DebugValueWriteRequest request, DebugValuePlan plan)
        {
            if (plan.LeafType == null) throw SchemaInvalid("fieldPath", "a field path that resolves to a declared type", "empty path on an untyped root");
            object converted;
            try
            {
                converted = UnityCliValueConverter.ConvertValue(request.Value, plan.LeafType);
            }
            catch (Exception exception)
            {
                throw SchemaInvalid("value", plan.LeafType.FullName ?? plan.LeafType.Name, "value of an incompatible shape: " + exception.GetType().Name);
            }
            if (!AcceptsValue(plan.LeafType, converted))
                throw SchemaInvalid("value", plan.LeafType.FullName ?? plan.LeafType.Name, converted == null ? "null" : converted.GetType().FullName);
            return converted;
        }

        private static bool AcceptsValue(Type targetType, object value)
        {
            if (targetType == null) return false;
            var nullable = Nullable.GetUnderlyingType(targetType);
            if (value == null) return nullable != null || !targetType.IsValueType;
            if (nullable != null) return nullable.IsInstanceOfType(value);
            return targetType.IsInstanceOfType(value);
        }

        /// <summary>前置条件比较；只能比较可证明安全的成员类型，否则拒绝而不是猜测相等。</summary>
        private static bool TryCompareGuard(Type leafType, object current, object expectedJson, out bool equal, out string refusalRule)
        {
            equal = false;
            refusalRule = null;
            if (leafType == null)
            {
                refusalRule = "guard-member-unknown";
                return false;
            }
            var nullable = Nullable.GetUnderlyingType(leafType);
            var type = nullable ?? leafType;
            object expected;
            try
            {
                expected = UnityCliValueConverter.ConvertValue(expectedJson, leafType);
            }
            catch (Exception)
            {
                refusalRule = "guard-value-incompatible";
                return false;
            }
            if (current == null || expected == null)
            {
                equal = current == null && expected == null;
                return true;
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                equal = ReferenceEquals(current, expected);
                return true;
            }
            if (type == typeof(string) || type == typeof(bool) || type.IsEnum || type == typeof(char)
                || type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(Guid))
            {
                equal = current.Equals(expected);
                return true;
            }
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)
                || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
            {
                equal = Convert.ToDouble(current, CultureInfo.InvariantCulture) == Convert.ToDouble(expected, CultureInfo.InvariantCulture);
                return true;
            }
            refusalRule = "guard-requires-comparable-member";
            return false;
        }

        /// <summary>session 必须存在且处于 open：已停止的 session 不再接收新的句柄与恢复记录。</summary>
        internal static void RequireOpenSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                throw SchemaInvalid("sessionId", "the id of an open debug session", "missing");
            var info = DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = sessionId });
            var state = info.Session == null ? string.Empty : info.Session.State;
            if (string.Equals(state, UnityCliDebugSessionStates.Open, StringComparison.Ordinal)) return;
            throw new BridgeCommandException(
                "E_DEBUG_CLEANUP_PENDING",
                "The session is not open, so it cannot accept a new handle or restore record.",
                new Dictionary<string, object> { { "sessionId", sessionId }, { "state", state }, { "cleanupPending", info.Session != null && info.Session.CleanupPending } },
                new[]
                {
                    "Run tools run debug.session.info to read the current session state and cleanup detail.",
                    "Run tools run debug.session.create to start a new session for this investigation."
                });
        }

        /// <summary>建立句柄；预算在 effect 前判定，失败时给出占用与释放建议。</summary>
        private static DebugHandleView CreateHandle(string sessionId, object target, bool strong, DebugValuePlan plan, out int sessionUsed, out int globalUsed)
        {
            DebugHandleView view;
            string failure;
            try
            {
                failure = DebugHandleRegistry.Create(sessionId, target, strong, DebugSessionHost.RuntimeId(), DebugSessionHost.DomainGeneration(), out view, out sessionUsed, out globalUsed);
            }
            catch (Exception exception)
            {
                throw Refuse("handle-creation-failed", "The handle could not be created: " + exception.GetType().Name, plan, null);
            }
            if (failure == null) return view;
            throw HandleBudgetExceeded(failure, sessionUsed, globalUsed);
        }

        /// <summary>句柄预算耗尽时的稳定错误；effect 前与 effect 内共用同一构造。</summary>
        private static BridgeCommandException HandleBudgetExceeded(string failure, int sessionUsed, int globalUsed)
        {
            return new BridgeCommandException(
                "E_DEBUG_LIMIT_EXCEEDED",
                "The object handle budget is exhausted, so no handle was created.",
                new Dictionary<string, object>
                {
                    { "limit", failure }, { "sessionUsed", (long)sessionUsed }, { "maxHandlesPerSession", (long)DebugLimits.MaxHandlesPerSession },
                    { "globalUsed", (long)globalUsed }, { "maxHandlesGlobal", (long)DebugLimits.MaxHandlesGlobal }
                },
                new[]
                {
                    "Run tools run debug.session.stop for a session whose handles you no longer need, then retry.",
                    "Re-read or re-write with createHandle none when the handle is not required."
                });
        }

        private static DebugLogicalClockView PublishLogicalClock(string name, object value, DebugValuePlan plan)
        {
            double numeric;
            string rawText;
            if (!TryReadNumber(value, out numeric, out rawText))
                throw Refuse(
                    "logical-clock-value-not-numeric",
                    "A named project logical clock must be a numeric project sequence; this value is not a number.",
                    plan,
                    new[] { "Point logicalClockName at a numeric field that carries the project's logical sequence." });
            return DebugLogicalClocks.Publish(name, numeric, rawText, "field:" + plan.RootIdentity + (string.IsNullOrEmpty(plan.PathText) ? string.Empty : PathSeparator + plan.PathText), DebugSessionHost.DomainGeneration());
        }

        private static bool TryReadNumber(object value, out double numeric, out string rawText)
        {
            numeric = 0d;
            rawText = string.Empty;
            if (value == null) return false;
            if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong)
            {
                var raw = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                numeric = raw;
                rawText = raw.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            if (value is float || value is double || value is decimal)
            {
                var raw = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(raw) || double.IsInfinity(raw)) return false;
                numeric = raw;
                rawText = Convert.ToString(value, CultureInfo.InvariantCulture);
                return true;
            }
            return false;
        }

        /// <summary>
        /// 不可写目标的稳定拒绝。它发生在 effect barrier 之前，且显式代码执行也不能改变结果，
        /// 因此不路由到 debug.code.execute，而是要求换一个可变字段。
        /// </summary>
        private static BridgeCommandException FieldNotWritable(string rule, string message, DebugValuePlan plan)
        {
            return new BridgeCommandException(
                "E_DEBUG_FIELD_NOT_WRITABLE",
                message + " Nothing was written.",
                new Dictionary<string, object>
                {
                    { "rule", rule },
                    { "rootIdentity", plan == null ? string.Empty : plan.RootIdentity },
                    { "path", plan == null ? string.Empty : plan.PathText }
                },
                new[]
                {
                    "Address a mutable field instead: tools run debug.value.read reports the declared fields, and only non-const, non-readonly fields can receive a structured write."
                });
        }

        /// <summary>
        /// 越过 effect barrier 后无法证明写入完整的统一报告：写入可能已部分生效，因此不伪装成 effect 前的拒绝。
        /// 使用与 framework effect 后语义相同的 E_EXECUTION_TIMEOUT，并给出目标身份与恢复指引。
        /// </summary>
        private static BridgeCommandException PostEffectWriteFailure(DebugValuePlan plan, string failure)
        {
            return new BridgeCommandException(
                "E_EXECUTION_TIMEOUT",
                "The structured write crossed its effect barrier but could not be proven complete: " + (string.IsNullOrEmpty(failure) ? "target-invalid" : failure),
                new Dictionary<string, object>
                {
                    { "rootIdentity", plan == null ? string.Empty : plan.RootIdentity },
                    { "path", plan == null ? string.Empty : plan.PathText },
                    { "reason", failure ?? "target-invalid" },
                    { "effectMayBePartial", true }
                },
                new[] { "Re-read the field with tools run debug.value.read before submitting a new request; the write may have partially applied." });
        }

        private static BridgeCommandException StaleOrUnresolvable(DebugValuePlan plan, string failure)
        {
            return new BridgeCommandException(
                "E_DEBUG_TARGET_STALE",
                "The resolved target could not be read in its current state: " + (String.IsNullOrEmpty(failure) ? "target-invalid" : failure),
                new Dictionary<string, object>
                {
                    { "rootIdentity", plan == null ? string.Empty : plan.RootIdentity },
                    { "path", plan == null ? string.Empty : plan.PathText },
                    { "reason", failure ?? "target-invalid" }
                },
                new[] { "Re-read the root with tools run debug.value.read so the target identity is resolved again." });
        }

        private static BridgeCommandException SchemaInvalid(string field, string expected, string actual)
        {
            return new BridgeCommandException(
                "E_SCHEMA_INVALID",
                "Field " + field + " is invalid: expected " + expected + ".",
                new Dictionary<string, object> { { "path", "$." + field }, { "expected", expected }, { "actual", actual ?? "null" } },
                new[] { "Run unitycli tools describe for this Tool to read the field contract, then submit the corrected input." });
        }

        /// <summary>不安全读取的稳定拒绝；总是给出显式代码执行路由。</summary>
        private static BridgeCommandException Refuse(string rule, string message, DebugValuePlan plan, string[] extraActions)
        {
            var actions = new List<string>
            {
                "Use tools run debug.code.execute when this effect must run: a structured read never executes property getters, arbitrary ToString, user serializers or enumerators, and never triggers type initialisation."
            };
            if (extraActions != null) actions.AddRange(extraActions);
            return new BridgeCommandException(
                "E_DEBUG_SCOPE_UNSUPPORTED",
                message,
                new Dictionary<string, object>
                {
                    { "rule", rule },
                    { "rootIdentity", plan == null ? string.Empty : plan.RootIdentity },
                    { "path", plan == null ? string.Empty : plan.PathText }
                },
                actions);
        }
    }

    /// <summary>值工具的 selector 输入；复用现有 ObjectRefResolver 的解析优先级。</summary>
    internal sealed class DebugValueSelector : IUnityCliObjectReferenceInput
    {
        internal int? InstanceIdValue;
        internal string PathValue;
        internal string AssetPathValue;
        internal string GuidValue;
        internal int? ComponentInstanceIdValue;
        internal int? ComponentIndexValue;
        internal string ComponentValue;
        internal string ComponentTypeValue;

        public int? InstanceId { get { return InstanceIdValue; } }
        public string AssetPath { get { return AssetPathValue; } }
        public string Guid { get { return GuidValue; } }
        public string Path { get { return PathValue; } }
        public string Name { get { return null; } }
        public string Target { get { return null; } }
        public int? ComponentInstanceId { get { return ComponentInstanceIdValue; } }
        public int? ComponentIndex { get { return ComponentIndexValue; } }
        public string Component { get { return ComponentValue; } }
        public string ComponentType { get { return ComponentTypeValue; } }
    }
}
#endif
