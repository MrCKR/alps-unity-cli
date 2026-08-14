import { observeEditorDialogs, publicDialog, type EditorDialog } from "../../editor-dialogs.js";
import { InstanceRegistryError, selectObservationInstanceForFlags } from "../../instances.js";
import { defineTool, type ToolContext } from "../define.js";
import { StructuredToolError } from "../errors.js";

export interface EditorDialogsRequest { dialogId?: string; }
export interface EditorDialogsResult { dialogs: EditorDialog[]; }

export default defineTool<EditorDialogsRequest, EditorDialogsResult>({
  name: "editor.dialogs",
  summary: "Read current modal desktop dialogs for the selected Unity Editor project.",
  description: "Read Windows modal dialogs for the uniquely attributable selected Unity project without contacting Unity Bridge or changing desktop state. Optionally filter the current observation by dialogId.",
  group: "editor",
  destructiveHint: false,
  sideEffects: [],
  handler: "offline",
  target: "editor",
  playModeSupport: "supported",
  examples: [{ description: "Read every current modal dialog.", argv: ["tools", "run", "editor.dialogs", "--input", "{}"], input: {} }],
  async execute(request, context) { return readEditorDialogs(request, context); },
});

/** 只观察选定 Editor 的当前模态窗口，不执行桌面操作。 */
export async function readEditorDialogs(request: EditorDialogsRequest, context: ToolContext): Promise<EditorDialogsResult> {
  if (!context.dataDir) throw new StructuredToolError("E_INSTANCE_NOT_FOUND", "Unity CLI data directory is required to resolve the selected Editor instance.", ["Pass --instance <instanceId|projectPath|alias> or run from a Unity project directory."]);
  const deadlineMs = Math.min(Date.now() + 10_000, context.absoluteDeadlineMs ?? Number.POSITIVE_INFINITY);
  let instance;
  try { instance = (await selectObservationInstanceForFlags(context.flags ?? {}, context.dataDir, Math.max(0, deadlineMs - Date.now()))).instance; }
  catch (error) { if (error instanceof InstanceRegistryError) throw new StructuredToolError(error.code, error.message, error.nextActions, error.details); throw error; }
  if (instance.status === "conflict") throw new StructuredToolError("E_INSTANCE_CONFLICT", "Multiple Unity Editor sessions claim the selected project identity.", ["Close duplicate Unity Editor processes before reading dialogs."]);
  const observed = await observeEditorDialogs({ instanceId: instance.instanceId, canonicalProjectPath: instance.canonicalProjectPath, displayProjectPath: instance.displayProjectPath }, Math.max(0, deadlineMs - Date.now()));
  if (observed.outcome === "unproven") throw new StructuredToolError("E_DIALOG_OBSERVATION_FAILED", "Windows dialog observation could not be proven for the selected Unity project.", ["Retry tools run editor.dialogs after the selected Unity process is stable."], { reason: observed.reason });
  if (observed.outcome === "conflict") throw new StructuredToolError("E_INSTANCE_CONFLICT", "Multiple Unity Editor processes claim the selected project identity.", ["Close duplicate Unity Editor processes before reading dialogs."]);
  if (observed.outcome === "offline") throw new StructuredToolError("E_INSTANCE_NOT_RUNNING", "The selected Unity project has no attributable Unity process.", ["Open the selected Unity project in Unity Editor."]);
  if (observed.outcome === "unsupported") throw new StructuredToolError("E_DIALOG_OBSERVATION_FAILED", "Windows dialog observation is unavailable on this platform.", ["Run tools run editor.dialogs on Windows."]);
  if (observed.outcome !== "observed") throw new StructuredToolError("E_DIALOG_OBSERVATION_FAILED", "Windows dialog observation could not be proven for the selected Unity project.", ["Run tools run editor.dialogs after the selected Unity process is stable."]);
  const dialogs = observed.dialogs.map(publicDialog);
  if (request.dialogId) { const dialog = dialogs.find((value) => value.dialogId === request.dialogId); if (!dialog) throw new StructuredToolError("E_DIALOG_NOT_FOUND", "The requested dialogId is not currently visible for the selected Unity project.", ["Run tools run editor.dialogs to obtain the current dialogId."]); return { dialogs: [dialog] }; }
  return { dialogs };
}
