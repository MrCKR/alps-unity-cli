import { createHash } from "node:crypto";
import { defaultDataDir } from "../platform.js";
import { EditorBlockerObserver, sanitizeEditorBlockers, type EditorBlockerObservation } from "../editor-blocker-observer.js";
import {
  clearInstanceHealthCache,
  selectInstanceForFlags,
  selectObservationInstanceForFlags,
  selectTransitionInstanceForFlags,
  type InstanceSelection,
  type InstanceTarget,
  type RegistrySession,
  type UnityInstance,
} from "../instances.js";
import type { BridgeFlags, BridgeRequest, BridgeResponse, BridgeRuntime } from "./types.js";

export const EDITOR_STATE_OBSERVATION_BUDGET_MS = 10_000;
const BLOCKER_DIAGNOSTIC_BUDGET_MS = 10_000;
export const EDITOR_STATE_BRIDGE_PROBE_BUDGET_MS = 3_500;
const DEFAULT_EXECUTION_TIMEOUT_MS = 60_000;
const REGISTRY_POLL_INTERVAL_MS = 100;
const COMPLETION_CONTRACT_VERSION = 1;
const REQUEST_DIGEST_VERSION = 1;
const INJECTED_INSTANCE_ID = `unity-${"0".repeat(64)}`;
// 仅持久 root coordinator 及其 root-bound 文件 barrier 可以跨 domain reload 绑定同一 immutable deadline。
const ROOT_TRANSITION_COMMANDS = new Set([
  "internal.build.run.start",
  "internal.editor.compile.start",
  "internal.editor.compile.status",
  "internal.editor.refresh.start",
  "internal.editor.refresh.status",
  "internal.asset.refresh.start",
  "internal.asset.refresh.status",
  "internal.package.embed.start",
  "internal.package.embed.status",
  "internal.package.resolve.start",
  "internal.package.resolve.status",
  "internal.package.files.claim",
  "internal.package.files.commit",
  "internal.package.files.prove",
  "internal.package.files.fail",
  "internal.test.run.start",
  "internal.test.run.status",
  "internal.test.run.cancel.plan",
  "internal.test.run.cancel",
  "internal.test.run.cancel.status",
  "internal.navmesh.bake.start",
  "internal.navmesh.bake.status",
  "internal.navmesh.bake.plan",
  "internal.navmesh.bake.snapshot",
  "internal.navmesh.bake.cancel",
  "internal.occlusion.bake.start",
  "internal.occlusion.bake.status",
  "internal.occlusion.bake.plan",
  "internal.occlusion.bake.snapshot",
  "internal.occlusion.bake.cancel",
]);
const TRANSITION_CODES = new Set([
  "E_UNITY_DOMAIN_RELOAD",
  "E_UNITY_COMPILING",
  "E_UNITY_IMPORTING",
  "E_UNITY_PLAY_MODE",
  "E_QUEUE_START_TIMEOUT",
  "E_RUNTIME_NOT_READY",
]);

export type SessionSupervisorMode = "execute" | "read" | "observeNow";

export interface BridgeRequestTemplate {
  requestId: string;
  command: string;
  input: Record<string, unknown>;
  timeoutMs?: number;
  /** 调用者已经冻结的 deadline；supervisor 只能取更早值。 */
  absoluteDeadlineMs?: number;
  observationAbsoluteDeadlineMs?: number;
  dryRun?: boolean;
  mutation?: boolean;
  /** 已由 editor.state 完成的私有选择；不得重跑 selector。 */
  observationSelection?: InstanceSelection;
  /** 私有 continuation capability；只允许 compile start/status 绑定 transition session。 */
  allowTransitionBinding?: boolean;
}

export interface FrozenBridgeRequest {
  readonly payload: BridgeRequest;
  readonly body: string;
  readonly requestDigest: string;
  readonly absoluteDeadlineMs: number;
}

interface ValidatedBinding {
  readonly target: InstanceTarget;
  readonly instanceId: string;
  readonly canonicalProjectPath: string;
  readonly sessionId: string;
  readonly listenerId: string;
  readonly listenerGeneration: number;
  readonly endpoint: string;
  readonly processId: number;
  readonly processStartedAt: string;
}

interface TransportAttestation {
  target: InstanceTarget;
  instanceId: string;
  sessionId: string;
  listenerId: string;
  listenerGeneration: number;
}

interface TransportReply {
  response: BridgeResponse;
  attestation: TransportAttestation;
}

interface SupervisorTarget {
  readonly target: InstanceTarget;
  readonly instanceId: string;
  readonly initialSelection?: InstanceSelection;
  readonly injected: boolean;
}

export interface SessionSupervisorDependencies {
  now(): number;
  sleep(ms: number): Promise<void>;
  select(flags: BridgeFlags, dataDir: string, target: InstanceTarget): Promise<InstanceSelection>;
  selectObservation(flags: BridgeFlags, dataDir: string, remainingMs: number, target: InstanceTarget): Promise<InstanceSelection>;
  selectTransition(flags: BridgeFlags, dataDir: string, remainingMs: number, target: InstanceTarget): Promise<InstanceSelection>;
  post(binding: ValidatedBinding, request: FrozenBridgeRequest, remainingMs: number): Promise<TransportReply>;
  observer: EditorBlockerObserver;
}

/** session supervisor 的结构化失败；上层只负责转换为公开 error envelope。 */
export class SessionSupervisorError extends Error {
  public constructor(
    public readonly code: string,
    message: string,
    public readonly nextActions: string[],
    public readonly details?: Record<string, unknown>,
    public readonly transient = false,
    public readonly retryAfterMs = 0,
  ) {
    super(message);
    this.name = "SessionSupervisorError";
  }
}

/**
 * Bridge-backed 调用的唯一 live session owner。
 * 它冻结一个 absolute deadline 和 request body，并独占 registry wait、listener rebind 与 response identity 校验。
 */
export class SessionSupervisor {
  private readonly dependencies: SessionSupervisorDependencies;

  public constructor(
    private readonly flags: BridgeFlags,
    private readonly runtime: BridgeRuntime,
    dependencies: Partial<SessionSupervisorDependencies> = {},
  ) {
    const injectedSelect = dependencies.select;
    this.dependencies = {
      now: dependencies.now ?? Date.now,
      sleep: dependencies.sleep ?? sleep,
      select: injectedSelect ?? ((flags, dataDir, target) => selectInstanceForFlags(flags, dataDir, process.cwd(), target)),
      selectObservation: dependencies.selectObservation ?? ((flags, dataDir, remainingMs, target) => selectObservationInstanceForFlags(flags, dataDir, remainingMs, process.cwd(), target)),
      // 隔离测试若注入普通 selector，则 transition 路径复用该 fake；真实运行使用严格 transition selector。
      selectTransition: dependencies.selectTransition ?? (injectedSelect
        ? async (flags, dataDir, _remainingMs, target) => injectedSelect(flags, dataDir, target)
        : (flags, dataDir, remainingMs, target) => selectTransitionInstanceForFlags(flags, dataDir, remainingMs, process.cwd(), target)),
      post: dependencies.post ?? postFrozenBridgeRequest,
      observer: dependencies.observer ?? new EditorBlockerObserver(),
    };
  }

  /** Tool/action 入口；mutation 由 Bridge journal 按同一 root request/digest 去重并恢复 terminal。 */
  public execute(template: BridgeRequestTemplate): Promise<BridgeResponse> {
    return this.run("execute", template);
  }

  /** 普通读取型Tool与live catalog入口；generation变化后只允许整次重新读取。 */
  public read(template: BridgeRequestTemplate): Promise<BridgeResponse> {
    return this.run("read", { ...template, mutation: false });
  }

  /** editor.state 专用即时入口；固定预算内只观察一次，不等待状态改变。 */
  public observeNow(template: BridgeRequestTemplate): Promise<BridgeResponse> {
    return this.run("observeNow", { ...template, mutation: false });
  }

  /** 首次调用时冻结 identity、deadline 与逐字节 request body；恢复过程不得重建。 */
  private async run(mode: SessionSupervisorMode, template: BridgeRequestTemplate): Promise<BridgeResponse> {
    const startedAtMs = this.dependencies.now();
    if (template.allowTransitionBinding === true && !ROOT_TRANSITION_COMMANDS.has(template.command)) {
      throw new SessionSupervisorError("E_INTERNAL_INVOCATION_INVALID", "Transition binding is restricted to private durable root coordinator commands.", [], { command: template.command });
    }
    const timeoutMs = normalizeTimeoutMs(template.timeoutMs, mode);
    // caller 已冻结业务 deadline 时，未显式提供更短 timeout 不得被 Supervisor 默认预算再次截断。
    const absoluteDeadlineMs = mode === "observeNow"
      ? Math.min(template.observationAbsoluteDeadlineMs ?? startedAtMs + timeoutMs, startedAtMs + EDITOR_STATE_OBSERVATION_BUDGET_MS)
      : template.absoluteDeadlineMs !== undefined && template.timeoutMs === undefined
        ? template.absoluteDeadlineMs
        : Math.min(template.absoluteDeadlineMs ?? Number.POSITIVE_INFINITY, startedAtMs + timeoutMs);
    if (mode === "observeNow") return this.runObservation(template, startedAtMs, absoluteDeadlineMs);
    // 首次 dispatch 前必须保留一次 bounded blocker probe；ready 快路径不会等待这段预算。
    const preDispatchDeadlineMs = reserveBlockerObservationBudget(startedAtMs, absoluteDeadlineMs);
    const sessionTarget = bridgeTarget(template.command);
    const target = await this.resolveTarget(sessionTarget, preDispatchDeadlineMs, startedAtMs, absoluteDeadlineMs, template.allowTransitionBinding === true);
    const request = freezeRequest(template, target.instanceId, absoluteDeadlineMs);

    if (target.injected) {
      return this.dispatchInjected(request);
    }

    let binding: ValidatedBinding;
    try {
      binding = await this.bind(target, preDispatchDeadlineMs, false, target.initialSelection, template.allowTransitionBinding === true);
    } catch (error) {
      const observed = target.initialSelection
        ? await this.observeInstance(target.initialSelection.instance, absoluteDeadlineMs)
        : undefined;
      if (observed?.outcome === "observed-blocked") throw blockedObservation(target.instanceId, observed, target.initialSelection?.instance.blockers);
      if (observed?.outcome === "conflict") throw blockerObservationConflict(target.instanceId, observed);
      throw error;
    }
    let dispatchAttempted = false;
    for (;;) {
      const remainingMs = remainingBudget(absoluteDeadlineMs, this.dependencies.now());
      if (remainingMs <= 0) {
        if (mode === "execute" && template.mutation !== false && dispatchAttempted)
          throw await this.executionTimeoutWithObservation(target.instanceId, binding, absoluteDeadlineMs, "The absolute deadline elapsed after dispatch without a terminal response.");
        throw deadlineError(mode, target.instanceId, startedAtMs, absoluteDeadlineMs);
      }

      try {
        dispatchAttempted = true;
        const reply = await this.dependencies.post(binding, request, remainingMs);
        if (!attestationMatches(binding, reply.attestation)) {
          binding = await this.bindReplacement(target, absoluteDeadlineMs, binding, template.allowTransitionBinding === true);
          continue;
        }
        assertResponseRequestId(request.payload.requestId, reply.response);
        // Bridge 已给出 canonical terminal response 时优先返回。
        if (!shouldRecoverTransition(reply.response)) return reply.response;
        await this.waitBeforeRecovery(reply.response, absoluteDeadlineMs);
        clearInstanceHealthCache();
        binding = await this.bind(target, absoluteDeadlineMs, false, undefined, template.allowTransitionBinding === true);
      } catch (error) {
        const mutationWasDispatched = mode === "execute" && template.mutation !== false && dispatchAttempted;
        if (error instanceof SessionSupervisorError) {
          if (mutationWasDispatched) throw await this.executionTimeoutWithObservation(target.instanceId, binding, absoluteDeadlineMs, `Post-dispatch recovery failed with ${error.code}.`);
          if (!mutationWasDispatched && error.code === "E_UNITY_BLOCKED") {
            const observed = await this.observeBlockers(binding, absoluteDeadlineMs);
            if (observed?.outcome === "observed-blocked") throw blockedObservation(target.instanceId, observed, error.details?.blockers);
            if (observed?.outcome === "conflict") throw blockerObservationConflict(target.instanceId, observed);
          }
          throw error;
        }
        const observed = await this.observeBlockers(binding, absoluteDeadlineMs);
        if (observed?.outcome === "observed-blocked") {
          if (mutationWasDispatched) throw executionTimeout(target.instanceId, "A modal blocker was observed after POST began before a terminal response.", observed);
          throw blockedObservation(target.instanceId, observed);
        }
        const remainingAfterFailureMs = remainingBudget(absoluteDeadlineMs, this.dependencies.now());
        if (remainingAfterFailureMs > 0) {
          await this.dependencies.sleep(Math.min(REGISTRY_POLL_INTERVAL_MS, remainingAfterFailureMs));
        }
        clearInstanceHealthCache();
        try {
          binding = await this.bind(target, absoluteDeadlineMs, false, undefined, template.allowTransitionBinding === true);
        } catch (bindError) {
          if (mutationWasDispatched) throw await this.executionTimeoutWithObservation(target.instanceId, binding, absoluteDeadlineMs, `Post-dispatch listener rebind failed with ${errorCode(bindError)}.`);
          throw bindError;
        }
      }
    }
  }

  /** 业务 deadline 之后只执行一次独立的只读诊断；绝不重放或延长业务请求。 */
  private async executionTimeoutWithObservation(instanceId: string, binding: ValidatedBinding, _deadlineMs: number, reason: string): Promise<SessionSupervisorError> {
    const observationDeadlineMs = this.dependencies.now() + BLOCKER_DIAGNOSTIC_BUDGET_MS;
    return executionTimeout(instanceId, reason, await this.observeBlockers(binding, observationDeadlineMs));
  }

  /** 失败路径的 observer 严格复用调用 deadline。 */
  private async observeBlockers(binding: ValidatedBinding, deadlineMs: number): Promise<EditorBlockerObservation | undefined> {
    if (binding.target === "runtime") return undefined;
    return this.observeIdentity({
      instanceId: binding.instanceId,
      canonicalProjectPath: binding.canonicalProjectPath,
      displayProjectPath: binding.canonicalProjectPath,
    }, deadlineMs, { processId: binding.processId, processStartedAt: binding.processStartedAt });
  }

  /** selector/bind 尚未建立 listener 时仍可基于 canonical identity 观察唯一进程。 */
  private async observeInstance(instance: UnityInstance, deadlineMs: number): Promise<EditorBlockerObservation | undefined> {
    if (instance.target === "runtime") return undefined;
    const session = instance.activeSession
      ? { processId: instance.activeSession.processId, processStartedAt: instance.activeSession.processStartedAt }
      : undefined;
    return this.observeIdentity({
      instanceId: instance.instanceId,
      canonicalProjectPath: instance.canonicalProjectPath,
      displayProjectPath: instance.displayProjectPath,
    }, deadlineMs, session);
  }

  /** observer 失败本身不得覆盖原 selector/transport 错误。 */
  private async observeIdentity(identity: { instanceId: string; canonicalProjectPath: string; displayProjectPath: string }, deadlineMs: number, session?: Pick<RegistrySession, "processId" | "processStartedAt">): Promise<EditorBlockerObservation | undefined> {
    if (remainingBudget(deadlineMs, this.dependencies.now()) < 1) return undefined;
    try { return await this.dependencies.observer.observe(identity, deadlineMs, session); }
    catch { return undefined; }
  }

  /** editor.state 只进行一次 selector、bind 和 POST；不等待、重绑或重投。 */
  private async runObservation(template: BridgeRequestTemplate, startedAtMs: number, absoluteDeadlineMs: number): Promise<BridgeResponse> {
    const selection = template.observationSelection ?? await this.selectObservation(absoluteDeadlineMs, startedAtMs);
    const instance = selection.instance;
    if (instance.status === "offline" || instance.status === "conflict" || !instance.activeSession) {
      throw new SessionSupervisorError(instance.status === "conflict" ? "E_INSTANCE_CONFLICT" : "E_INSTANCE_NOT_RUNNING", "No unique live Unity session is available for a Bridge observation.", [], publicSessionDetails(instance));
    }
    const request = freezeRequest(template, instance.instanceId, absoluteDeadlineMs);
    const binding = bindingFrom(instance);
    const remainingMs = remainingBudget(absoluteDeadlineMs, this.dependencies.now());
    if (remainingMs <= 0) throw deadlineError("observeNow", instance.instanceId, startedAtMs, absoluteDeadlineMs);
    const probeTimeoutMs = Math.min(remainingMs, EDITOR_STATE_BRIDGE_PROBE_BUDGET_MS);
    if (this.runtime.bridgeExecute) return this.dispatchInjected(request, probeTimeoutMs);
    try {
      const reply = await this.dependencies.post(binding, request, probeTimeoutMs);
      if (!attestationMatches(binding, reply.attestation)) throw identityMismatch(instance.instanceId);
      assertResponseRequestId(request.payload.requestId, reply.response);
      return reply.response;
    } catch (error) {
      if (error instanceof SessionSupervisorError) throw error;
      throw connectionFailure(instance.instanceId, error);
    }
  }

  private async selectObservation(absoluteDeadlineMs: number, startedAtMs: number): Promise<InstanceSelection> {
    const selectorBudgetMs = remainingBudget(absoluteDeadlineMs, this.dependencies.now());
    if (selectorBudgetMs <= 0) throw deadlineError("observeNow", "unknown", startedAtMs, absoluteDeadlineMs);
    return this.dependencies.selectObservation(this.flags, this.runtime.dataDir ?? defaultDataDir(), selectorBudgetMs, "editor");
  }

  /** 测试 transport 仍通过 supervisor，但不伪造 registry/listener generation。 */
  private async dispatchInjected(request: FrozenBridgeRequest, timeoutMs?: number): Promise<BridgeResponse> {
    if (!this.runtime.bridgeExecute) throw connectionFailure(request.payload.instanceId, new Error("Injected Bridge transport is unavailable."));
    try {
      const operation = this.runtime.bridgeExecute(request.payload);
      const response = timeoutMs === undefined ? await operation : await Promise.race([
        operation,
        sleep(timeoutMs).then(() => { throw new SessionSupervisorError("E_EXECUTION_TIMEOUT", "The injected Bridge observation exceeded its absolute deadline.", ["Inspect editor.state and retry only after Unity is responsive."], { instanceId: request.payload.instanceId, timeoutMs }); }),
      ]);
      assertResponseRequestId(request.payload.requestId, response);
      return response;
    } catch (error) {
      if (error instanceof SessionSupervisorError) throw error;
      throw connectionFailure(request.payload.instanceId, error);
    }
  }

  /** 首次 selector 解析只发生一次；后续恢复固定使用 instanceId，alias 变化不会改绑目标。 */
  private async resolveTarget(target: InstanceTarget, selectionDeadlineMs: number, startedAtMs: number, observationDeadlineMs: number, allowTransitionBinding: boolean): Promise<SupervisorTarget> {
    if (this.runtime.bridgeExecute) return { target, instanceId: INJECTED_INSTANCE_ID, injected: true };
    try {
      const selection = await this.selectWithinDeadline(this.flags, target, selectionDeadlineMs, () => deadlineError("execute", "unknown", startedAtMs, observationDeadlineMs), allowTransitionBinding);
      return { target, instanceId: selection.instance.instanceId, initialSelection: selection, injected: false };
    } catch (selectionError) {
      // registry尚未注册时，普通Tool仍应在同一次失败中报告唯一canonical Unity process的modal。
      const remainingMs = remainingBudget(observationDeadlineMs, this.dependencies.now());
      if (target === "editor" && remainingMs > 0) {
        try {
          const observationSelection = await this.dependencies.selectObservation(this.flags, this.runtime.dataDir ?? defaultDataDir(), remainingMs, target);
          const observed = await this.observeInstance(observationSelection.instance, observationDeadlineMs);
          if (observed?.outcome === "observed-blocked") throw blockedObservation(observationSelection.instance.instanceId, observed);
          if (observed?.outcome === "conflict") throw blockerObservationConflict(observationSelection.instance.instanceId, observed);
        } catch (observationError) {
          if (observationError instanceof SessionSupervisorError) throw observationError;
        }
      }
      throw selectionError;
    }
  }

  /** selector 也消费 immutable deadline；超时后的后台读取结果不得触发 POST。 */
  private async selectWithinDeadline(flags: BridgeFlags, target: InstanceTarget, absoluteDeadlineMs: number, timeoutError: () => SessionSupervisorError, allowTransitionBinding = false): Promise<InstanceSelection> {
    const remainingMs = remainingBudget(absoluteDeadlineMs, this.dependencies.now());
    if (remainingMs <= 0) throw timeoutError();
    let timer: NodeJS.Timeout | undefined;
    try {
      const selection = allowTransitionBinding
        ? this.dependencies.selectTransition(flags, this.runtime.dataDir ?? defaultDataDir(), remainingMs, target)
        : this.dependencies.select(flags, this.runtime.dataDir ?? defaultDataDir(), target);
      return await Promise.race([
        selection,
        new Promise<InstanceSelection>((_resolve, reject) => { timer = setTimeout(() => reject(timeoutError()), remainingMs); }),
      ]);
    } finally {
      if (timer) clearTimeout(timer);
    }
  }

  /** blocked/conflict/offline 不等待；仅 transitioning 在同一 absolute deadline 内轮询。 */
  private async bind(
    target: SupervisorTarget,
    absoluteDeadlineMs: number,
    observeOnly: boolean,
    initialSelection?: InstanceSelection,
    allowTransitionBinding = false,
  ): Promise<ValidatedBinding> {
    let selection = initialSelection;
    for (;;) {
      if (!selection) {
        selection = await this.selectWithinDeadline({ instance: target.instanceId }, target.target, absoluteDeadlineMs, () => selectionTransitionTimeout(target.instanceId, absoluteDeadlineMs), allowTransitionBinding);
      }
      const instance = selection.instance;
      if (instance.status === "blocked") throw blocked(instance);
      if (instance.status === "ready" || (allowTransitionBinding && instance.status === "transitioning" && instance.activeSession)) return bindingFrom(instance);
      if (observeOnly) throw observedTransition(instance);
      if (remainingBudget(absoluteDeadlineMs, this.dependencies.now()) <= 0) {
        throw transitionTimeout(instance, absoluteDeadlineMs);
      }
      await this.dependencies.sleep(Math.min(REGISTRY_POLL_INTERVAL_MS, remainingBudget(absoluteDeadlineMs, this.dependencies.now())));
      selection = undefined;
    }
  }

  /** response attestation 不匹配时必须等到 registry 出现不同 generation，禁止向同一旧 listener 反复 POST。 */
  private async bindReplacement(target: SupervisorTarget, absoluteDeadlineMs: number, previous: ValidatedBinding, allowTransitionBinding = false): Promise<ValidatedBinding> {
    for (;;) {
      const selection = await this.selectWithinDeadline({ instance: target.instanceId }, target.target, absoluteDeadlineMs, () => selectionTransitionTimeout(target.instanceId, absoluteDeadlineMs), allowTransitionBinding);
      if (selection.instance.status === "blocked") throw blocked(selection.instance);
      if (selection.instance.status === "ready" || (allowTransitionBinding && selection.instance.status === "transitioning" && selection.instance.activeSession)) {
        const candidate = bindingFrom(selection.instance);
        if (bindingKey(candidate) !== bindingKey(previous)) return candidate;
      }
      if (remainingBudget(absoluteDeadlineMs, this.dependencies.now()) <= 0) {
        throw transitionTimeout(selection.instance, absoluteDeadlineMs);
      }
      await this.dependencies.sleep(Math.min(REGISTRY_POLL_INTERVAL_MS, remainingBudget(absoluteDeadlineMs, this.dependencies.now())));
      clearInstanceHealthCache();
    }
  }

  /** Bridge 建议等待只消耗已有 deadline，不创建新的 attempt budget。 */
  private async waitBeforeRecovery(response: BridgeResponse, absoluteDeadlineMs: number): Promise<void> {
    const retryAfterMs = response.ok === false && Number.isFinite(response.error.retryAfterMs)
      ? Math.max(0, response.error.retryAfterMs)
      : REGISTRY_POLL_INTERVAL_MS;
    const remainingMs = remainingBudget(absoluteDeadlineMs, this.dependencies.now());
    if (remainingMs <= 0) return;
    await this.dependencies.sleep(Math.min(retryAfterMs, remainingMs));
  }
}

/** immutable Bridge request v2；任何 recovery 都复用这里生成的 body。 */
export function freezeRequest(template: BridgeRequestTemplate, instanceId: string, absoluteDeadlineMs: number): FrozenBridgeRequest {
  const payload: BridgeRequest = {
    protocolVersion: 2,
    requestId: template.requestId,
    instanceId,
    command: template.command,
    input: template.input,
    execution: {
      absoluteDeadlineUtc: new Date(absoluteDeadlineMs).toISOString(),
      completionContractVersion: COMPLETION_CONTRACT_VERSION,
      requestDigestVersion: REQUEST_DIGEST_VERSION,
      ...(template.dryRun ? { dryRun: true } : {}),
    },
  };
  const body = JSON.stringify(payload);
  const requestDigest = computeRequestDigestV1(instanceId, Buffer.from(body, "utf8"));
  return { payload: deepFreeze(JSON.parse(body) as BridgeRequest), body, requestDigest, absoluteDeadlineMs };
}

/** RequestDigest v1 使用 exact request bytes；只用于验证与 journal correlation，不进入 public envelope。 */
export function computeRequestDigestV1(instanceId: string, exactRequestBytes: Uint8Array): string {
  return createHash("sha256")
    .update("unity-cli-request-v1", "utf8")
    .update(Buffer.from([0]))
    .update(instanceId, "utf8")
    .update(Buffer.from([0]))
    .update(exactRequestBytes)
    .digest("hex");
}

/** HTTP timeout 始终取 immutable absolute deadline 的剩余预算。 */
export function bridgeHttpTimeoutMs(payload: BridgeRequest, now = Date.now()): number {
  const deadline = Date.parse(payload.execution.absoluteDeadlineUtc);
  return Number.isFinite(deadline) ? Math.max(0, deadline - now) : 0;
}

/** 真实 transport 只接收 supervisor 私有 binding，并返回不进入业务 envelope 的 identity attestation。 */
async function postFrozenBridgeRequest(binding: ValidatedBinding, request: FrozenBridgeRequest, remainingMs: number): Promise<TransportReply> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), Math.max(1, remainingMs));
  try {
    const response = await fetch(new URL("/bridge/execute", binding.endpoint), {
      method: "POST",
      headers: {
        "content-type": "application/json",
        "x-unity-cli-expected-instance-id": binding.instanceId,
        "x-unity-cli-expected-session-id": binding.sessionId,
        "x-unity-cli-expected-target": binding.target,
        "x-unity-cli-expected-listener-id": binding.listenerId,
        "x-unity-cli-expected-listener-generation": String(binding.listenerGeneration),
      },
      body: request.body,
      signal: controller.signal,
    });
    const envelope = await response.json() as BridgeResponse;
    if (!response.ok && envelope.ok !== false) throw new Error(`Bridge returned HTTP ${response.status}.`);
    return {
      response: envelope,
      attestation: {
        target: (response.headers.get("x-unity-cli-target") ?? "") as InstanceTarget,
        instanceId: response.headers.get("x-unity-cli-instance-id") ?? "",
        sessionId: response.headers.get("x-unity-cli-session-id") ?? "",
        listenerId: response.headers.get("x-unity-cli-listener-id") ?? "",
        listenerGeneration: Number(response.headers.get("x-unity-cli-listener-generation")),
      },
    };
  } finally {
    clearTimeout(timeout);
  }
}

function bindingFrom(instance: UnityInstance): ValidatedBinding {
  const session = instance.activeSession;
  if (!session) {
    throw new SessionSupervisorError("E_INSTANCE_NOT_RUNNING", "The selected Unity project has no validated Bridge listener.", [
      "Open this exact Unity project in Unity Editor.",
      "Run unity_cli instances list to inspect its current state.",
    ], publicSessionDetails(instance));
  }
  return {
    target: session.target,
    instanceId: instance.instanceId,
    canonicalProjectPath: instance.canonicalProjectPath,
    sessionId: session.sessionId,
    listenerId: session.listener.listenerId,
    listenerGeneration: session.listener.generation,
    endpoint: session.listener.endpoint.url,
    processId: session.processId,
    processStartedAt: session.processStartedAt,
  };
}

function bindingKey(binding: ValidatedBinding): string {
  return `${binding.target}:${binding.sessionId}:${binding.listenerId}:${binding.listenerGeneration}`;
}

function attestationMatches(binding: ValidatedBinding, attestation: TransportAttestation): boolean {
  return attestation.target === binding.target
    && attestation.instanceId === binding.instanceId
    && attestation.sessionId === binding.sessionId
    && attestation.listenerId === binding.listenerId
    && attestation.listenerGeneration === binding.listenerGeneration;
}

/** runtime-tool namespace 始终绑定独立 development Player；其余命令绑定 Editor。 */
function bridgeTarget(command: string): InstanceTarget {
  return command === "runtime-tool.list" || command === "runtime-tool.run" ? "runtime" : "editor";
}

function normalizeTimeoutMs(value: number | undefined, mode: SessionSupervisorMode): number {
  if (mode === "observeNow") {
    if (value === undefined) return EDITOR_STATE_OBSERVATION_BUDGET_MS;
    if (!Number.isFinite(value) || value <= 0) return 0;
    return Math.min(value, EDITOR_STATE_OBSERVATION_BUDGET_MS);
  }
  if (value === undefined) return DEFAULT_EXECUTION_TIMEOUT_MS;
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new SessionSupervisorError("E_SCHEMA_INVALID", "--timeout-ms must be a positive integer.", [
      "Pass --timeout-ms with a positive integer within the Tool descriptor limit.",
    ], { field: "execution.timeoutMs", actual: value });
  }
  return value;
}

function publicSessionDetails(instance: UnityInstance): Record<string, unknown> {
  return {
    instanceId: instance.instanceId,
    target: instance.target ?? null,
    sessionId: instance.sessionId ?? null,
    status: instance.status,
    progress: instance.progress,
    blockers: sanitizeEditorBlockers(instance.blockers),
    lastHeartbeatAt: instance.lastHeartbeatAt ?? null,
  };
}

function blocked(instance: UnityInstance): SessionSupervisorError {
  return new SessionSupervisorError("E_UNITY_BLOCKED", "Unity Editor is blocked by an external modal window or non-recoverable session condition.", [
    "Run unity_cli tools run editor.state for the current blocker snapshot.",
    "Resolve the reported blocker in the selected Unity Editor process, then retry.",
  ], publicSessionDetails(instance));
}

function observedTransition(instance: UnityInstance): SessionSupervisorError {
  return new SessionSupervisorError("E_UNITY_TRANSITIONING", "Unity Editor is currently transitioning.", [
    "Use the editor.state snapshot to inspect current progress.",
  ], publicSessionDetails(instance), true, REGISTRY_POLL_INTERVAL_MS);
}

function selectionTransitionTimeout(instanceId: string, absoluteDeadlineMs: number): SessionSupervisorError {
  return new SessionSupervisorError("E_UNITY_TRANSITION_TIMEOUT", "Unity instance selection did not complete before the request deadline.", [
    "Run unity_cli tools run editor.state to inspect current progress or blockers.",
  ], { instanceId, absoluteDeadlineUtc: new Date(absoluteDeadlineMs).toISOString() });
}

function transitionTimeout(instance: UnityInstance, absoluteDeadlineMs: number): SessionSupervisorError {
  return new SessionSupervisorError("E_UNITY_TRANSITION_TIMEOUT", "Unity Editor did not reach a validated ready generation before the request deadline.", [
    "Run unity_cli tools run editor.state to inspect current progress or blockers.",
    "Retry with a larger --timeout-ms only when the transition is expected to take longer.",
  ], { ...publicSessionDetails(instance), absoluteDeadlineUtc: new Date(absoluteDeadlineMs).toISOString() });
}

function deadlineError(mode: SessionSupervisorMode, instanceId: string, startedAtMs: number, absoluteDeadlineMs: number): SessionSupervisorError {
  return new SessionSupervisorError(
    "E_EXECUTION_TIMEOUT",
    "The absolute request deadline elapsed before a terminal result was proven.",
    ["Inspect editor.state and retry only after Unity is responsive."],
    { instanceId, elapsedMs: Math.max(0, absoluteDeadlineMs - startedAtMs), absoluteDeadlineUtc: new Date(absoluteDeadlineMs).toISOString() },
  );
}

function executionTimeout(instanceId: string, reason: string, observation?: EditorBlockerObservation): SessionSupervisorError {
  const blockers = observation?.outcome === "observed-blocked" ? sanitizeEditorBlockers(observation.blockers) : [];
  const title = blockers[0]?.title;
  return new SessionSupervisorError("E_EXECUTION_TIMEOUT", title
    ? `The request did not return a terminal result before its deadline; modal dialog '${title}' was observed.`
    : "The request did not return a terminal result before its deadline.", blockerNextActions(), { instanceId, reason, ...(blockers.length > 0 ? { blockers } : {}) });
}

function blockedObservation(instanceId: string, observation: EditorBlockerObservation, existing?: unknown): SessionSupervisorError {
  const blockers = sanitizeEditorBlockers([...(Array.isArray(existing) ? existing : []), ...observation.blockers]);
  const title = blockers[0]?.title ?? "an external modal window";
  return new SessionSupervisorError("E_UNITY_BLOCKED", `Unity Editor is blocked by observed dialog '${title}'.`, blockerNextActions(), { instanceId, blockers });
}

function blockerNextActions(): string[] {
  return [
    "Run unity_cli tools run editor.dialogs to inspect the current modal dialog.",
    "Run unity_cli tools run editor.dialog.click with a current dialogId and exact button label.",
  ];
}

function blockerObservationConflict(instanceId: string, observation: EditorBlockerObservation): SessionSupervisorError {
  return new SessionSupervisorError("E_INSTANCE_CONFLICT", "Multiple or unverifiable Unity processes claim the selected canonical project identity.", [
    "Close duplicate Unity Editor processes and retry only after a single process owns this project.",
  ], { instanceId, reason: observation.reason ?? "process-claim-conflict" });
}

function identityMismatch(instanceId: string): SessionSupervisorError {
  return new SessionSupervisorError("E_INSTANCE_IDENTITY_MISMATCH", "Bridge response identity does not match the validated listener generation.", [
    "Run unity_cli instances list and retry after the selected session is stable.",
  ], { instanceId });
}

function connectionFailure(instanceId: string, error: unknown): SessionSupervisorError {
  return new SessionSupervisorError("BRIDGE_CONNECTION_FAILED", errorMessage(error), [
    "Run unity_cli instances list and inspect the selected Unity session.",
    "Run unity_cli tools run editor.state for transition or blocker details.",
  ], { instanceId }, true, REGISTRY_POLL_INTERVAL_MS);
}

function shouldRecoverTransition(response: BridgeResponse): boolean {
  return response.ok === false && response.error.transient === true && TRANSITION_CODES.has(response.error.code);
}

function assertResponseRequestId(requestId: string, response: BridgeResponse): void {
  if (response.requestId !== requestId) {
    throw new SessionSupervisorError("E_BRIDGE_RESPONSE_MISMATCH", "Bridge response requestId does not match the immutable root request.", [
      "Inspect UnityCliBridge.log and retry only after the selected listener is stable.",
    ], { expectedRequestId: requestId, actualRequestId: response.requestId });
  }
}

/** 初始 selector/bind 失败时至少保留一次 Windows blocker observation；短调用按总预算三分之一封顶。 */
function reserveBlockerObservationBudget(startedAtMs: number, absoluteDeadlineMs: number): number {
  const total = Math.max(1, absoluteDeadlineMs - startedAtMs);
  const reserve = Math.min(EDITOR_STATE_OBSERVATION_BUDGET_MS, Math.max(1, Math.floor(total / 3)));
  return Math.max(startedAtMs + 1, absoluteDeadlineMs - reserve);
}

function remainingBudget(absoluteDeadlineMs: number, now: number): number {
  return Math.max(0, absoluteDeadlineMs - now);
}

function deepFreeze<T>(value: T): T {
  if (value && typeof value === "object") {
    Object.freeze(value);
    for (const child of Object.values(value as Record<string, unknown>)) deepFreeze(child);
  }
  return value;
}

function errorCode(error: unknown): string {
  return error instanceof SessionSupervisorError ? error.code : error instanceof Error ? error.name : "UNKNOWN_ERROR";
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}
