import { CLICK_DIALOG_MIN_EXECUTION_BUDGET_MS, clickObservedDialog, observeEditorDialogs } from "../../../editor-dialogs.js";
import { InstanceRegistryError, selectObservationInstanceForFlags } from "../../../instances.js";
import { defineTool, type ToolContext } from "../../define.js";
import { StructuredToolError } from "../../errors.js";

export interface EditorDialogClickRequest { dialogId: string; button: string; }
export interface EditorDialogClickResult { clicked: true; dialogId: string; title: string; button: string; }

export default defineTool<EditorDialogClickRequest, EditorDialogClickResult>({
  name: "editor.dialog.click",
  summary: "Click one enabled button on a currently observed Unity desktop dialog.",
  description: "Re-observes the selected canonical Unity project, invokes the exact enabled button label returned by editor.dialogs, and proves that the original dialog disappeared or is no longer modal.",
  group: "editor",
  target: "editor",
  destructiveHint: false,
  supportsDryRun: false,
  playModeSupport: "supported",
  sideEffects: ["desktop-dialog-click"],
  timeoutMs: 30_000,
  handler: "offline",
  supportStatus: "cli-only",
  examples: [{ description: "Click the Cancel button returned by the dialog Context.", argv: ["tools", "run", "editor.dialog.click", "--input", '{"dialogId":"dialog_example","button":"Cancel"}'] }],
  async execute(request, context) {
    if (!context.dataDir) throw new StructuredToolError("E_INSTANCE_NOT_FOUND", "Unity CLI data directory is required to resolve the selected Editor instance.", ["Pass --instance <instanceId|projectPath|alias> or run from a Unity project directory."]);
    const deadlineMs = context.absoluteDeadlineMs ?? Date.now() + 30_000;
    let instance;
    try { instance = (await selectObservationInstanceForFlags(context.flags ?? {}, context.dataDir, Math.max(0, deadlineMs - Date.now()))).instance; }
    catch (error) { if (error instanceof InstanceRegistryError) throw new StructuredToolError(error.code, error.message, error.nextActions, error.details); throw error; }
    if (instance.status === "conflict") throw new StructuredToolError("E_INSTANCE_CONFLICT", "Multiple verified Unity Editor sessions claim the selected project identity.", ["Close duplicate Unity Editor processes before clicking a dialog."]);
    if (instance.status === "unverifiable") throw new StructuredToolError("E_INSTANCE_UNVERIFIABLE", "Unity Editor process ownership could not be verified safely.", ["Run unity_cli instances list or tools run editor.state to observe the identity again."]);
    const observed = await observeEditorDialogs({ instanceId: instance.instanceId, canonicalProjectPath: instance.canonicalProjectPath, displayProjectPath: instance.displayProjectPath }, Math.max(0, deadlineMs - Date.now()));
    const recovery = ["Run tools run editor.dialogs, then run tools run editor.dialog.click with a current dialogId and exact enabled button label."];
    if (observed.outcome === "unproven" && observed.reason === "process-claims-unverifiable") throw new StructuredToolError("E_INSTANCE_UNVERIFIABLE", "Unity Editor process ownership could not be verified safely.", ["Run unity_cli instances list or tools run editor.state to observe the identity again."]);
    if (observed.outcome === "unproven" || observed.outcome === "unsupported") throw new StructuredToolError("E_DIALOG_OBSERVATION_FAILED", "Windows dialog observation could not be proven for the selected Unity project.", recovery);
    if (observed.outcome === "conflict") throw new StructuredToolError("E_INSTANCE_CONFLICT", "Multiple verified Unity Editor processes claim the selected project identity.", recovery);
    if (observed.outcome === "offline") throw new StructuredToolError("E_INSTANCE_NOT_RUNNING", "The selected Unity project has no attributable Unity process.", recovery);
    if (observed.outcome !== "observed") throw new StructuredToolError("E_DIALOG_OBSERVATION_FAILED", "Windows dialog observation could not be proven for the selected Unity project.", recovery);
    if (observed.dialogs.length === 0) throw new StructuredToolError("E_DIALOG_NOT_FOUND", "No modal dialog is currently visible for the selected Unity project.", recovery);
    const dialog = observed.dialogs.find((value) => value.dialogId === request.dialogId);
    if (!dialog) throw new StructuredToolError("E_DIALOG_STALE", "The requested dialogId is no longer current for the selected Unity project.", recovery);
    const matches = dialog.buttonTargets.filter((value) => value.label === request.button && value.enabled);
    if (matches.length === 0) throw new StructuredToolError("E_DIALOG_BUTTON_NOT_FOUND", "The requested enabled button is not present on the current dialog.", recovery);
    if (matches.length > 1) throw new StructuredToolError("E_DIALOG_BUTTON_AMBIGUOUS", "Multiple enabled dialog buttons have the requested label.", recovery);
    if (deadlineMs - Date.now() < CLICK_DIALOG_MIN_EXECUTION_BUDGET_MS) throw new StructuredToolError("E_EXECUTION_TIMEOUT", "The request deadline elapsed before enough time remained to prove the dialog click.", recovery, { reason: "pre-effect-budget" });
    await context.commitEffect?.();
    const result = await clickObservedDialog(dialog, matches[0], deadlineMs);
    if (result.outcome === "stale") throw new StructuredToolError("E_DIALOG_STALE", "The dialog changed before the click effect could be applied.", recovery);
    if (result.outcome === "button-not-found") throw new StructuredToolError("E_DIALOG_BUTTON_NOT_FOUND", "The requested enabled button changed before the click effect could be applied.", recovery);
    if (result.outcome !== "clicked") throw new StructuredToolError("E_DIALOG_CLICK_UNPROVEN", "The dialog click did not prove that the original modal dialog closed.", recovery, result.reason ? { reason: result.reason } : undefined);
    return { clicked: true, dialogId: dialog.dialogId, title: dialog.title, button: request.button };
  },
});
