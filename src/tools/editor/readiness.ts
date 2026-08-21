import { readEditorState, type EditorStateResult } from "./state.js";

interface EditorReadinessContext {
  flags?: Record<string, string | boolean>;
  dataDir?: string;
  rootAdmission?: unknown;
  bridgeExecute?: unknown;
}

/**
 * durable root 接受前等待 Editor 离开瞬时 transition。
 * 该循环只读取 editor.state，不提交业务 effect，也不会重放任何 root。
 */
export async function waitForEditorTransitionEnd(
  context: EditorReadinessContext,
  requestId: string,
  deadlineMs: number,
): Promise<EditorStateResult> {
  for (;;) {
    const state = await readEditorState({
      requestId,
      flags: context.flags,
      dataDir: context.dataDir,
      rootAdmission: context.rootAdmission,
      bridgeExecute: context.bridgeExecute,
      absoluteDeadlineMs: deadlineMs,
    } as never);
    if (state.status !== "transitioning") return state;

    const remainingMs = deadlineMs - Date.now();
    if (remainingMs <= 0) return state;
    await new Promise<void>((resolveDelay) => setTimeout(resolveDelay, Math.min(250, remainingMs)));
  }
}
