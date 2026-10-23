import { editorDialogBlockers, sanitizeEditorBlockers, type EditorBlocker, type EditorDebugPauseBlocker } from "../editor-blocker-observer.js";
import type { MainThreadDebugPause } from "../instances.js";
import { failure } from "../output.js";
import type { ErrorEnvelope } from "../protocol/envelope.js";

/**
 * TS defineTool 统一结构化错误。
 *
 * 由本地 TS 工具抛出,`tools run` 捕获后转换为标准 failure envelope。
 * 适用于 docs/reflect/package 等不属于 Bridge 的离线能力,避免退化成 COMMAND_FAILED。
 */
export class StructuredToolError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly nextActions: string[] = [],
    readonly details?: Record<string, unknown>,
    readonly transient?: boolean,
    readonly retryAfterMs?: number
  ) {
    super(message);
  }

  /** 跨内部抽象边界转发完整错误语义，不丢失 wire retry 元数据。 */
  static from(error: {
    code: string;
    message: string;
    nextActions?: string[];
    details?: unknown;
    transient?: boolean;
    retryAfterMs?: number;
  }): StructuredToolError {
    return new StructuredToolError(
      error.code,
      error.message,
      error.nextActions ?? [],
      error.details as Record<string, unknown> | undefined,
      error.transient,
      error.retryAfterMs
    );
  }
}

/** 仅转发已公开的 blocker DTO，禁止旧字段或内部观察数据穿透。 */
export function blockersFromDetails(details: Record<string, unknown> | undefined): EditorBlocker[] | undefined {
  if (!details) return undefined;
  const blockers = sanitizeEditorBlockers(details.blockers);
  return blockers.length > 0 ? blockers : undefined;
}

/** effect 已开始后的 timeout 保留原错误码，并提供当前 blocker 的明确恢复操作。 */
export function blockerTimeoutMessage(operation: string, blockers: EditorBlocker[] | undefined): string {
  if (!blockers?.[0]) return `${operation} did not return a terminal result before the request deadline.`;
  const dialog = editorDialogBlockers(blockers)[0];
  return dialog
    ? `${operation} did not return a terminal result before the request deadline; modal dialog '${dialog.title}' was observed.`
    : `${operation} did not return a terminal result before the request deadline; the Editor main thread is paused at debug pause site ${blockers[0].type === "debug-pause" ? blockers[0].pauseId : "unknown"}.`;
}

/** 超时恢复操作：先引导读 editor.state 等待过渡完成，再处理 modal dialog；两者同时给出，不因 dialog 丢弃等待引导。 */
export function blockerNextActions(): string[] {
  return [
    "Run unitycli tools run editor.state to check whether the Editor is still transitioning and wait until it reports ready.",
    "Run unitycli tools run editor.dialogs to inspect the current modal dialog.",
    "Run unitycli tools run editor.dialog-click with a current dialogId and exact button label.",
  ];
}

/** 观察到的 blocker 里是否由当前 live pause publication 证明主线程被故意暂停。 */
export function debugPauseBlocker(blockers: readonly EditorBlocker[] | undefined): EditorDebugPauseBlocker | undefined {
  return blockers?.find((blocker): blocker is EditorDebugPauseBlocker => blocker.type === "debug-pause");
}

/**
 * effect 前的 blocked 拒绝。
 *
 * 故意暂停与 Windows modal 是两种不同事实：modal 由本进程独立观察，只能等用户处理；
 * 主线程暂停有已发布的解除路径，必须给出 `E_DEBUG_MAIN_THREAD_PAUSED` 与具体 resume/info 路由。
 * 本函数只做归因，不决定可达集合：暂停期间哪些能力真正可达永远由 Bridge 的同一控制通道判定。
 */
export function blockedEditorStateError(blockedMessage: string, blockers: readonly EditorBlocker[] | undefined): StructuredToolError {
  const pause = debugPauseBlocker(blockers);
  if (!pause) return new StructuredToolError("E_UNITY_BLOCKED", blockedMessage, ["Resolve the Editor blocker, then retry."], { blockers: [...(blockers ?? [])] });
  return new StructuredToolError(
    "E_DEBUG_MAIN_THREAD_PAUSED",
    `The Unity Editor main thread is deliberately paused at debug pause site ${pause.pauseId}, so this request was refused before any effect instead of waiting behind the paused main-thread queue.`,
    [
      "Run unitycli tools run debug.pause-list to read the pause site that is holding the main thread.",
      `Run unitycli tools run debug.pause-resume with pauseId ${pause.pauseId} to release the site and let the main thread continue.`,
      "While the site is held you can still read debug.events-read, debug.hook-list, debug.pause-inspect, debug.session-list, debug.program-list and debug.thread-list, then release the site before submitting business mutations again.",
    ],
    {
      pauseId: pause.pauseId,
      threadId: pause.threadId,
      mainThread: true,
      timeoutKind: pause.timeoutKind,
      remainingMs: pause.remainingMs,
      pausedForMs: pause.pausedForMs,
      pauseTool: pause.infoTool,
      resumeTool: pause.resumeTool,
    },
    false,
    0,
  );
}

/**
 * dispatch 已开始、终态被故意主线程暂停挡住时的 typed partial outcome。
 *
 * 它与 effect 之前的 `E_DEBUG_MAIN_THREAD_PAUSED` 拒绝是两种不同事实：前者在任何 effect 之前被拒绝，
 * 这里则是请求已经发出、可能已经生效却拿不到终态。所以 `outcome` 必须是 `may-have-started` 而不是 `unknown`，
 * 并且必须带出当前 live publication 的现场事实与可达解除路由。
 * 这里不重投、不预占、不强制解除：是否重新提交由调用方决定。
 *
 * 信封形状是固定的：`ok:false` 且 `error.code` 为 `E_DEBUG_MAIN_THREAD_PAUSED`，
 * 分类事实全部在 `error.details` —— `outcome`/`dispatch` 恒为 `may-have-started`、`replay` 为 `new-decision-required`，
 * `pauseFacts` 说明边界事实是否从当前 publication 读到；`transient` 为 false、`retryAfterMs` 为 0。
 * 边界事实读不到时只说读不到，绝不伪造 `timeoutKind`/`remainingMs`。
 */
export function debugPausePartialOutcome(requestId: string, pause: MainThreadDebugPause, retainedDetails: Record<string, unknown>, boundaryFactsUnread = false): ErrorEnvelope {
  const base = `The request was dispatched and may have started, but the Unity Editor main thread is deliberately paused at debug pause site ${pause.pauseId}, so no terminal result could be returned and the dispatch stays unproven rather than failed.`;
  return failure(
    requestId,
    "E_DEBUG_MAIN_THREAD_PAUSED",
    boundaryFactsUnread
      ? `${base} The live boundary facts (timeoutKind, remainingMs) were not read from the current publication, so this answer names the pause but not its boundary.`
      : base,
    [
      "Run unitycli tools run debug.pause-list to read the pause site that is holding the main thread.",
      `Run unitycli tools run debug.pause-resume with pauseId ${pause.pauseId} to release the site and let the paused call continue.`,
      "Read back the affected business state before deciding whether to submit a new request; this dispatch is never replayed automatically.",
    ],
    {
      ...retainedDetails,
      outcome: "may-have-started",
      dispatch: "may-have-started",
      replay: "new-decision-required",
      pauseId: pause.pauseId,
      threadId: pause.threadId,
      mainThread: true,
      ...(pause.timeoutKind ? { timeoutKind: pause.timeoutKind } : {}),
      ...(pause.remainingMs === undefined ? {} : { remainingMs: pause.remainingMs }),
      pauseFacts: boundaryFactsUnread ? "not-read-in-time" : "read",
      pauseTool: "debug.pause-list",
      resumeTool: "debug.pause-resume",
    },
    { transient: false, retryAfterMs: 0 },
  );
}
