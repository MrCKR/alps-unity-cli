#if UNITY_EDITOR
namespace UnityCli
{
    /// <summary>
    /// debug 域的共享 notes 文本。机制、配额与恢复流程只在此处维护一次，
    /// 由多个 descriptor 的 Notes 字段引用；不进入默认 describe 输出。
    /// </summary>
    internal static class DebugDomainNotes
    {
        /// <summary>debug 输入设备后端：所有权与生命周期。</summary>
        internal static readonly string[] InputOwnership = new[]
        {
            "Ownership and lifetime: a session owns at most one virtual device per kind, created on the first submission and ended by debug.input-release with scope all, by session stop or by Play exit; a press with until-release stays pressed until one of those happens, and a press with hold-ms is released on the monotonic clock at the central scheduler tick on the Unity main thread.",
            "Conflicts are refused before any effect: a key this owner already holds under a different end boundary, a device with this owner's exact target name created by another owner, an unreadable device list, an exhausted input plan budget and a full record budget all fail before the effect barrier, so a refused call never pressed anything. This owner never resets, rewrites or takes over a real device or another owner's device.",
            "This Tool is only the optional device backend; it does not replace general business command injection."
        };

        /// <summary>输入释放诚实性与证据要求。</summary>
        internal static readonly string[] InputEvidence = new[]
        {
            "Release honesty: released is reported only when the cleared state was confirmed by reading the device back, or when the device is provably absent from the backend device list; otherwise the record is pending with the exact reason and the remaining work is completed by the central scheduler tick.",
            "A submitted device event never proves that the business read the input: prove acceptance with a hook capture, a program check, a value read or a sealed debug.run window. Every dispatch and release appends one trigger-source event with kind input-dispatch, execution point input-dispatch and source trigger, so debug.events-read can read the same fact and debug.run-begin can require it."
        };

        /// <summary>输入设备与 record 预算。</summary>
        internal static readonly string[] InputBudget = new[]
        {
            "Limits: 2 virtual devices per session (one key device and one pointer device), 8 keys and 3 pointer buttons per dispatch, 16 retained input records per session, 60000 ms maximum declared hold boundary, 16 pending main-thread release requests, and the shared 1024 planned injections and inputs per session."
        };

        /// <summary>输入设备后端可用性判定。</summary>
        internal static readonly string[] InputBackend = new[]
        {
            "Availability honesty: the device backend requires the optional Input System API to be loaded and every API member this owner needs to be present, so a missing package reports status unavailable with reason backend-not-loaded, and a package whose shape does not match reports api-member-missing plus the bounded missingApiMembers list. Nothing is ever installed and no existing device is ever listed as owned.",
            "General input is broader than a keyboard: an input can be a project method call, a message enqueue, protocol data, parameter replacement, result replacement or a device event, so the general paths are debug.method-invoke for a business entry, debug.hook-install for parameter, result or skip intervention on an interceptable managed wrapper, debug.code-execute for explicit code, and debug.program-run for a bounded program entry or a cross-thread delivery to a thread-safe point. None of them is tied to a single logical frame."
        };

        /// <summary>结构化写入的恢复记录、句柄与暂停路由规则。</summary>
        internal static readonly string[] ValueWriteFacts = new[]
        {
            "Restore record: on success this Tool registers a conditional restore record with the session, holding the original value, target identity and write time, so debug.session-stop can restore the field later. The restore happens only while the target is still valid and the current value still equals the value this session wrote; any other write is reported as a conflict instead of being overwritten. The record keeps the resolved target identity for the lifetime of the session and is itself a recorded intervention: it is not an automatic rollback and does not undo other writers.",
            "Handles and value types: createHandle accepts none, weak or strong; a strong handle keeps the target alive, so it is recorded as an intervention that changes object lifetime, and both modes are bounded per session and per process with E_DEBUG_LIMIT_EXCEEDED when exhausted. Handles and restore records belong to an open session, and a stopping or stopped session is refused with E_DEBUG_CLEANUP_PENDING. When an intermediate path member is a value type the write goes through an explicit boxed copy chain and is written back into its parent, reported as structWriteBack instead of silently losing the update.",
            "Reload, pause and partial effects: restore records and handles are runtime state, so a Domain Reload or Play exit invalidates them and the field keeps whatever value it had. Naming pauseId executes the write on that pause site's own paused thread instead of the normal path; an unknown or already released pauseId returns E_DEBUG_PAUSE_STALE. A request that is cancelled or disconnects after the effect barrier may leave the field written, so re-read the field before retrying, and a failure discovered after the barrier is reported as E_EXECUTION_TIMEOUT with effectMayBePartial true. Limits: " + DebugLimits.ValueSummary()
        };

        /// <summary>debug session 的身份、持久与槽位规则。</summary>
        internal static readonly string[] SessionLifecycle = new[]
        {
            "The definition is bound to the canonical project path plus the current editorSessionId, is published atomically to the single per-project debug session file, and so survives a Domain Reload inside the same Editor session but never an Editor restart. A definition holds one session slot from creation until debug.session-remove deletes it: debug.session-stop closes future dispatch but keeps the definition and its evidence, so a stopped definition still occupies its slot, and debug.session-list with state stopped finds reclaimable definitions."
        };

        /// <summary>session cleanup 的顺序与 pending 判定。</summary>
        internal static readonly string[] SessionCleanup = new[]
        {
            "Cleanup order: stop new dispatch and pending injections, issue cooperative cancellation and resume, release virtual input, close the owner's observation and listeners, request detach of the owner's hooks and lookups, run explicit and still-applicable finite cleanup entries, release waits and handles, then emit the report. Stage one runs before the first cleanup entry, so a hit on any thread after this call starts never enters user code again while an in-flight frame completes on its own frame.",
            "cleanupPending is derived from the session's real state: it is true whenever a registered resource, a handle whose release is unconfirmed or a deferred cleanup entry remains, and the report names every such item with its exact reason. Repeating stop is the supported retry, and debug.session-remove is refused with E_DEBUG_CLEANUP_PENDING until that cleanup completes. Cleanup only touches this session's own resources."
        };

        /// <summary>session 上限与触发预算动态摘要。</summary>
        internal static readonly string[] SessionLimits = new[]
        {
            DebugLimits.SessionSummary(),
            DebugLimits.TriggerSummary()
        };

        /// <summary>session 创建类 Tool 的机制说明。</summary>
        internal static readonly string[] SessionCreate = Concat(SessionLifecycle, SessionLimits);

        /// <summary>session 清理与管理类 Tool 的机制说明。</summary>
        internal static readonly string[] SessionManage = Concat(SessionCleanup, SessionLifecycle, SessionLimits);

        /// <summary>evidence window 的判定、覆盖与捕获语义。</summary>
        internal static readonly string[] RunEvidence = new[]
        {
            "Quantifiers: once needs at least one retained matching event with passed=true; every needs at least one retained match and every retained match passed; whole-window additionally requires caller-declared coverage proof and an observed end condition; absent requires that no matching event exists and that coverage is proven, because a negative claim is only provable with proven coverage. A retained match with passed=false is a complete counterexample for once, every and whole-window, and for absent any matching event is the counterexample.",
            "Coverage honesty: every per-check result carries two independent facts, coverageRequired (whether that quantifier consumes a caller-declared coverage requirement) and coverageProven (whether the window interval over that check's required sources was readable). A matching event from a sampled stream never proves a check and the check reports sampled-only and stays indeterminate. A boolean produced by user code never becomes coverage proof; declaredCoverage is returned as declared and never counted as proof.",
            "Configuration segments: install the program, hook or assertion the claim observes before begin, because a program or hook installed, replaced, enabled or disabled inside the window opens a new configuration segment, and a strict window (allowSegments false) then reports configuration-changed, while allowSegments true reports the change per segment. Assertion failures are NOT errors: debug.run-end, debug.run-wait and debug.run-info answer ok:true with a verdict of passed, failed or indeterminate, and no assertion-failed error code exists."
        };

        /// <summary>evidence window 的捕获与预算事实。</summary>
        internal static readonly string[] RunFacts = new[]
        {
            "Capture: an atomic boundary sequence from the single event store, that instant's dropped and append-failure counts, and the central scheduler queue watermark (pendingDeliveries, queueHighWaterMark and drivers) decide which events had entered the store. When the scheduler still holds pending deliveries or drivers at the boundary, the events they will produce are outside the window and the verdict reports queue-not-drained instead of pretending to be complete. A sealed window keeps its frozen boundary, capture and bounded evidence payload, and a Domain Reload or Editor restart removes it, after which an old runId answers E_DEBUG_RUN_NOT_FOUND.",
            "Budget: " + DebugLimits.RunSummary()
        };

        /// <summary>run 创建类 Tool 的完整机制说明。</summary>
        internal static readonly string[] RunBegin = Concat(RunEvidence, RunFacts, new[] { "Step entry context members: " + UnityCliDebugProgramAbi.StepContextAbi() });

        /// <summary>run 判定与观察类 Tool 的机制说明。</summary>
        internal static readonly string[] RunRead = Concat(RunEvidence, RunFacts);

        /// <summary>program 执行点语义。</summary>
        internal static readonly string[] ProgramSites = new[]
        {
            "Execution sites are never interchangeable: editor-tick schedules the entry on the main-thread central editor tick; method-prefix and method-postfix claim the next hit of the matching exact target; signal runs when an explicitly delivered signal reaches the bound execution point; worker-thread claims the next hit that happens off the main thread; player-loop-phase runs on a real Unity PlayerLoop phase while the Editor is playing, requires phase (before-update, after-update, fixed-update or late-update), is refused in Edit mode with E_DEBUG_PROGRAM_SITE_UNAVAILABLE, is never served by the editor tick, and refuses an undeclared phase name with E_DEBUG_PLAYER_LOOP_PHASE_UNKNOWN.",
            "A run that takes the execution right on the main thread uses bounded synchronous steps: a step entry returning Continue(wait) keeps its single continuous driver and resumes on later occurrences of its own declared execution point until it returns Finish() or its deadline closes it as cancelled. A dispatch-site run stays scheduled until the exact target is really hit, then that hitting thread runs the entry once synchronously and the run completes, so a worker-thread run is a single synchronous call on the thread that made the original call."
        };

        /// <summary>program run 状态机、期限与预算。</summary>
        internal static readonly string[] ProgramLifecycle = new[]
        {
            "State machine: scheduled, running, completed, failed, cancelled or interrupted. A pending run that expires is closed as cancelled and never executes late; once synchronous code started, expiry only requests cancellation, the state keeps reporting that the code is still in flight, and the terminal state is published only after the original code returned and cleanup is provable.",
            "durationMs is required and bounds only this run: the run is closed when the driver exceeds it, and a dispatch-site run whose target never arrives is closed as cancelled with E_DEBUG_PROGRAM_RUN_EXPIRED when durationMs elapses. Each program has at most one continuous step driver and the global active run budget counts every non-terminal run. Activation attaches the bindings declared by the current revision through the single hook host, so program-owned bindings and other intervention owners on the same target share one accounting and a conflicting second owner is refused before any effect.",
            "Reload and recovery: a Domain Reload or Play segment end invalidates every non-terminal run as interrupted and the program returns to dormant; a stopped session neither accepts a new run nor keeps its program resources."
        };

        /// <summary>program 触发遇到主线程暂停时的部分结局。</summary>
        internal static readonly string[] ProgramPauseOutcome = new[]
        {
            "A main-thread trigger that reaches a declared main-thread pause inside its entry cannot return a terminal response: the main-thread queue stops while that site is held, so the trigger answers ok:false with outcome and dispatch \"may-have-started\", replay \"new-decision-required\", the live pause facts (pauseId, threadId, timeoutKind, remainingMs), pauseTool debug.pause-list and resumeTool debug.pause-resume, with error.transient false and error.retryAfterMs 0. That partial outcome is neither a failure nor an unknown outcome: the run keeps its own state machine, the caller reads the same programRunId again after the site is released, and nothing is replayed automatically.",
            "A main-thread pause longer than the remaining request budget cannot be observed by the request holding it, and one off-main control wait is separately bounded to 30000 ms. A caller that needs the terminal answer must trigger the pause from a dispatch execution site whose creation result is answered before the pause starts, declare a shorter bounded pauseTimeout, or declare pauseTimeout unbounded and release it with debug.pause-list then debug.pause-resume."
        };

        /// <summary>program 入口 ABI、上限与动态摘要。</summary>
        internal static readonly string[] ProgramAbi = new[]
        {
            "Entry ABI: " + UnityCliDebugProgramAbi.SignatureTable(),
            UnityCliDebugProgramAbi.StepContextAbi(),
            DebugLimits.ProgramSummary()
        };

        /// <summary>program 运行类 Tool 的完整机制说明。</summary>
        internal static readonly string[] ProgramRun = Concat(ProgramSites, ProgramLifecycle, ProgramPauseOutcome, ProgramAbi);

        /// <summary>program 观察与管理类 Tool 的机制说明。</summary>
        internal static readonly string[] ProgramManage = Concat(ProgramLifecycle, ProgramAbi);

        /// <summary>暂停现场的生命周期、mailbox 与恢复规则。</summary>
        internal static readonly string[] PauseLifecycle = new[]
        {
            "Lifecycle: a pause site belongs to one Editor domain generation and one Play segment, so a Domain Reload, Play exit or Editor restart removes it and later calls report the new generation's facts. A resumed, timed-out or released pauseId is never listed again and never revived; any pause Tool called with it returns E_DEBUG_PAUSE_STALE with its terminal state. A CLI request timeout, a debug.pause-wait expiry and a CLI exit never release a site; an unbounded pause is released by debug.pause-resume, by session stop for a site whose breakpoint declared a sessionId, or by reload, Play exit and Editor shutdown.",
            "Mailbox: one operation executes per site at a time and at most the declared number of requests may wait; while a site serves a request it does not enter a nested pause, and a request refused by that bound returns E_DEBUG_LIMIT_EXCEEDED with the current load. Only the paused thread is guaranteed stopped; other threads, servers, external clocks and object mutation continue while it is paused.",
            "Cancellation: the request deadline and cancellation token end the caller's wait (and the bounded read or confirmation), never the user code that is already running or the pause itself unless the Tool says so.",
            // 运行时真实上限文本由 DebugLimits 单一来源提供；离线预算扫描只统计字面量。
            DebugLimits.PauseSummary()
        };

        /// <summary>暂停写入类 Tool 的风险说明。</summary>
        internal static readonly string[] PauseRisk = new[]
        {
            "Risk: these Tools run on, or release, a business call that was stopped mid-invocation; resuming continues that call and other threads may already block on locks it holds, while caller code that waits on something the paused thread owns can deadlock."
        };

        /// <summary>暂停读取类 Tool 的共用机制说明。</summary>
        internal static readonly string[] PauseRead = Concat(PauseLifecycle);

        /// <summary>暂停写入类 Tool 的共用机制说明。</summary>
        internal static readonly string[] PauseWrite = Concat(PauseLifecycle, PauseRisk);

        /// <summary>输入设备 Tool 的共用机制说明。</summary>
        internal static readonly string[] InputDevice = Concat(InputOwnership, InputEvidence, InputBudget);

        /// <summary>输入只读观察 Tool 的共用机制说明。</summary>
        internal static readonly string[] InputRead = Concat(InputBackend, InputOwnership, InputBudget);

        /// <summary>hook 容量与 session 事件配额。</summary>
        internal static readonly string[] HookQuotas = new[]
        {
            "Capacity: 128 hook definitions (16 per target), 128 KiB per callback source, 512 loaded compile artifacts (each charged a 256 KiB per-assembly allowance), 128 MiB per domain across all compile shapes (code execution, hook callbacks, programs, pause evaluate) and 256 distinct adapter identities per domain. Only a domain reload reclaims them; a repeated identical source reuses its artifact.",
            "Session limits: 4096 events / 8 MiB per session (32 MiB global), 32 KiB state per hook, 256 observed threads and 64 active frames per thread, 64 pending main-thread supplements, 120 s restore budget per domain generation."
        };

        /// <summary>hook 命中线程与在途调用语义。</summary>
        internal static readonly string[] HookThreadScope = new[]
        {
            "Thread scope: a hit is dispatched on the managed thread that made the original call and callbacks run inline on that same thread, so the recorded thread id is the original call's thread. Only main-thread hits carry the Unity frame number; worker-thread hits carry the monotonic clock plus the last frame observed on the main thread, and a Unity object identity that cannot be read on a worker thread is delivered later as a separate timestamped supplement event.",
            "Disabling, removing or replacing a definition closes it to new entries, keeps calls already inside on the revision they acquired at their prefix, and never claims that in-flight original calls have ended; another owner's patch on the same target stays in place."
        };

        /// <summary>hook listener 触发器与 phase 语义。</summary>
        internal static readonly string[] HookListener = new[]
        {
            "Listener: the optional listener object filters the definition before any callback runs and is the only supported way to condition a hook on hit count, thread, instance identity, argument or field value. It requires the sessionId of an open debug session that owns the planned injection.",
            "trigger defaults to method-hit; log reacts to the threaded log callback, host-signal to an explicit host signal, and player-loop-phase to a real Unity PlayerLoop phase in Editor Play. player-loop-phase requires phase (before-update, after-update, fixed-update or late-update) and installs that framework-owned phase site; the editor tick is not a listener trigger, so a main-thread listener runs through debug.program-run instead. A phase site is refused in Edit mode with E_DEBUG_PROGRAM_SITE_UNAVAILABLE and an undeclared phase name with E_DEBUG_PLAYER_LOOP_PHASE_UNKNOWN.",
            "A phase arrival has no method instance and no arguments, so such a listener can only be satisfied by field conditions: an argument condition or a declared instanceIdentity is refused with E_DEBUG_PHASE_TRIGGER_REQUIRES_INVOCATION before any phase site is installed. Every satisfied phase arrival records a trigger event with delivery facts (triggeredAt, queuedAt, startedAt, trigger thread, execution thread, revision, expired) and executionPoint trigger:player-loop-phase:<phase>, captures declared static fields, records each action that needs a method instance or its arguments as a coverage event named phase-action-requires-invocation, and consumes one listener hit; when a finite maxHits is exhausted the phase site is released through the same path as remove, disable and session stop. maxHits is a finite hit budget where 1 means one-shot and the budget is not rearmed after a domain reload.",
            "Conditions: threadId restricts the hit thread, instanceIdentity must equal the identity string the events report, and each condition compares the projected value of one argument index or field with the canonical JSON of a literal value using equals or not-equals. A hit that fails a condition or arrives after the budget is exhausted counts as filtered and never runs the callback. Limits: 8 listener conditions and 1000000 hits per finite listener."
        };

        /// <summary>hook pause 断点的时长与线程语义。</summary>
        internal static readonly string[] HookPause = new[]
        {
            "Breakpoint duration: a pause breakpoint must declare pauseTimeout with no default value; bounded requires a positive integer durationMs and unbounded forbids it. A bounded pause releases itself when its monotonic budget elapses (a CLI request timeout, Unity pause or timeScale never extends it) and records pause-timeout, while one off-main control wait is separately bounded to 30000 ms.",
            "A pause stops the thread that hit it, so the trigger-thread choice decides whether the triggering request can still be answered: a main-thread hit stops the Editor queue that would answer it, and that request fails with ok:false, error.code E_DEBUG_MAIN_THREAD_PAUSED and error.details outcome and dispatch \"may-have-started\", replay \"new-decision-required\", pauseId, threadId, timeoutKind and remainingMs, with error.transient false and error.retryAfterMs 0, so the dispatch stays unproven rather than failed and is never replayed automatically. A worker-thread hit pauses only that worker thread. A caller that needs a terminal answer must keep durationMs below the 30000 ms off-main bound, let the hit arrive through a dispatch site whose creation result is answered before the pause starts, or declare pauseTimeout unbounded and release it with debug.pause-list then debug.pause-resume."
        };

        /// <summary>hook 回调失败、所有权与冲突规则。</summary>
        internal static readonly string[] HookCallbackRules = new[]
        {
            "Callback overrun is only detected after a callback returns, so it is not a preemptive timeout: an infinite loop, a long sleep or a deadlock inside a callback keeps the Editor unresponsive, and disable or remove cannot terminate a callback that is already running. Callbacks run inside the game call chain of the selected Editor session and can interleave with other CLI mutations on that same session; disable the definition first when an isolated observation window is required.",
            "Callback failure: a throwing callback is caught by the host, is never propagated to the game call, and the definition becomes faulted with faultCode E_HOOK_CALLBACK_FAILED plus the projected exception message for the revision it happened under. That revision then stops dispatching, and the intercepted call keeps its original behaviour because a staged argument, a skipped original body or a replaced result is never applied. A hit is not a request, so the failure reaches no caller: read faultCode through debug.hook-info lastFault or the fault event through debug.events-read with kind=fault, then correct the source with debug.hook-replace and recover with debug.hook-enable. A fault stops only the definition that faulted.",
            "Conflict rule: on one exact target at most one definition with callback code may be active, because a callback body can write anything and its write scope cannot be proven statically; a second one is rejected with E_DEBUG_RESOURCE_CONFLICT before any effect, while capture-only observations may coexist in a deterministic order (priority descending, then install order). What cannot be proven statically stays the caller's responsibility: whether a callback mutates shared state outside the target, and what a captured field value means when a coexisting callback also writes that field during the same invocation.",
            "Program ownership: a definition owned by a program binding cannot be changed by debug.hook-install, debug.hook-replace, debug.hook-enable, debug.hook-disable or debug.hook-remove; those calls are refused with E_HOOK_OWNED_BY_PROGRAM, and the program must be changed through debug.program-replace or released through debug.program-stop."
        };

        /// <summary>新增 hook 定义时需要的完整机制说明。</summary>
        internal static readonly string[] HookInstall = Concat(HookListener, HookPause, HookCallbackRules, HookQuotas);

        /// <summary>管理已有 hook 定义（删除、启用、禁用、替换）时需要的机制说明。</summary>
        internal static readonly string[] HookManage = Concat(HookCallbackRules, HookQuotas, HookThreadScope);

        /// <summary>code-execute 的引用、作用域与恢复细节。</summary>
        internal static readonly string[] CodeExecuteDetails = new[]
        {
            "References: every locatable assembly already loaded in the Editor AppDomain is referenced for the whole domain generation and refs only appends more; nothing can be removed, so a type name that another loaded assembly also declares is a CS0433 ambiguity (System.Tuple exists in both mscorlib and ExCSS.Unity) and you must use object[] or a type you declare.",
            "Scope: the whole body is one method-body scope, so a name declared at its top level cannot be redeclared in a nested block, loop, lambda or local function (CS0136).",
            "No capability reduction: infinite loops, long sleeps, blocking waits and lock contention are not rejected. Timeout stops only CLI waiting and cannot interrupt Run, so state may be partial or outcome-unknown rather than proof that execution stopped, and recovery may require terminating the Editor. While the main thread is paused, each off-main control wait is bounded to 30000 ms, so a pause longer than the remaining --timeout-ms yields the partial E_DEBUG_MAIN_THREAD_PAUSED outcome. External command hosts must wait for this CLI process to exit and allow at least 5000 ms beyond --timeout-ms."
        };

        /// <summary>console-read 的固定预算维度与恢复方式。</summary>
        internal static readonly string[] ConsoleReadBudget = new[]
        {
            "Filtering advances over raw positions and every entry consumes the shared scan/reflection/getter/main-thread/UTF-8 budget. Each dimension is clamped to its absolute limit: mainThreadMilliseconds 2000, scannedRecords 100000, getters 10000, serializedUtf8Bytes 8 MiB, so requesting a larger budget changes nothing. The recovery for a truncated or exhausted scan is to narrow type/filter or lower count.",
            "E_REQUEST_BUDGET_LIMIT_EXCEEDED reports the exhausted dimension and replaces the page because publishing any terminal result also needs budget."
        };

        /// <summary>editor menu 执行的已知限制。</summary>
        internal static readonly string[] MenuExecuteLimits = new[]
        {
            "Known limits of driving arbitrary menus: an item that opens a modal dialog blocks the reply until the dialog closes, an item that triggers a domain reload or an Editor quit can end the session before the reply, and heavy imports can exceed the timeout. After E_EXECUTION_TIMEOUT inspect the Editor before submitting a new request.",
            "The preflight runs the menu item's own validation function, so a validate function with side effects is executed before the effect barrier. No dry-run exists because a menu path cannot be simulated."
        };

        /// <summary>按顺序拼接多个 notes 块，保持共享文本只有一份。</summary>
        private static string[] Concat(params string[][] parts)
        {
            var total = 0;
            foreach (var part in parts) total += part.Length;
            var result = new string[total];
            var index = 0;
            foreach (var part in parts)
            {
                System.Array.Copy(part, 0, result, index, part.Length);
                index += part.Length;
            }
            return result;
        }
    }
}
#endif
