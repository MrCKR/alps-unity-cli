import { observeEditorDialogs, publicDialog, type EditorDialog } from "../../editor-dialogs.js";
import { InstanceRegistryError, selectObservationInstanceForFlags } from "../../instances.js";
import { defineContext, StructuredContextError, type ContextReadContext } from "../define.js";

export interface EditorDialogsRequest { dialogId?: string; }
export interface EditorDialogsResult { observedAt: string; dialogs: EditorDialog[]; }

export default defineContext<EditorDialogsRequest, EditorDialogsResult>({
  name: "editor.dialogs",
  summary: "Read current modal desktop dialogs for the selected Unity Editor project.",
  description: "Reads Windows modal dialogs for the uniquely attributable selected Unity project without contacting Unity Bridge or changing desktop state. Optionally filters the current observation by dialogId.",
  category: "editor",
  source: "built-in",
  target: "editor",
  availabilityScope: "always",
  playModeSupport: "supported",
  examples: [{ description: "Read every current modal dialog.", argv: ["context", "read", "editor.dialogs", "--input", "{}"], input: {} }],
  async read(request, context) { return readEditorDialogs(request, context); },
});

export async function readEditorDialogs(request: EditorDialogsRequest, context: ContextReadContext): Promise<EditorDialogsResult> {
  if (!context.dataDir) throw new StructuredContextError("E_INSTANCE_NOT_FOUND", "Unity CLI data directory is required to resolve the selected Editor instance.", ["Pass --instance <instanceId|projectPath|alias> or run from a Unity project directory."]);
  const deadlineMs = Date.now() + 10_000;
  let instance;
  try { instance = (await selectObservationInstanceForFlags(context.flags ?? {}, context.dataDir, Math.max(0, deadlineMs - Date.now()))).instance; }
  catch (error) { if (error instanceof InstanceRegistryError) throw new StructuredContextError(error.code, error.message, error.nextActions, error.details); throw error; }
  if (instance.status === "conflict") throw new StructuredContextError("E_INSTANCE_CONFLICT", "Multiple Unity Editor sessions claim the selected project identity.", ["Close duplicate Unity Editor processes before reading dialogs."]);
  const observed = await observeEditorDialogs({ instanceId: instance.instanceId, canonicalProjectPath: instance.canonicalProjectPath, displayProjectPath: instance.displayProjectPath }, Math.max(0, deadlineMs - Date.now()));
  if (observed.outcome === "unproven") throw new StructuredContextError("E_DIALOG_OBSERVATION_FAILED", "Windows dialog observation could not be proven for the selected Unity project.", ["Retry context read editor.dialogs after the selected Unity process is stable."], { reason: observed.reason });
  if (observed.outcome === "conflict") throw new StructuredContextError("E_INSTANCE_CONFLICT", "Multiple Unity Editor processes claim the selected project identity.", ["Close duplicate Unity Editor processes before reading dialogs."]);
  if (observed.outcome === "offline") throw new StructuredContextError("E_INSTANCE_NOT_RUNNING", "The selected Unity project has no attributable Unity process.", ["Open the selected Unity project in Unity Editor."]);
  if (observed.outcome === "unsupported") throw new StructuredContextError("E_DIALOG_OBSERVATION_FAILED", "Windows dialog observation is unavailable on this platform.", ["Run context read editor.dialogs on Windows."]);
  if (observed.outcome !== "observed") throw new StructuredContextError("E_DIALOG_OBSERVATION_FAILED", "Windows dialog observation could not be proven for the selected Unity project.", ["Run context read editor.dialogs after the selected Unity process is stable."]);
  const dialogs = observed.dialogs.map(publicDialog);
  if (request.dialogId) { const dialog = dialogs.find((value) => value.dialogId === request.dialogId); if (!dialog) throw new StructuredContextError("E_DIALOG_NOT_FOUND", "The requested dialogId is not currently visible for the selected Unity project.", ["Run context read editor.dialogs to obtain the current dialogId."]); return { observedAt: new Date().toISOString(), dialogs: [dialog] }; }
  return { observedAt: new Date().toISOString(), dialogs };
}
