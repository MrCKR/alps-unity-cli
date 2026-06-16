/**
 * Compatibility CLI command: docs-topic <topic>
 *
 * `docs-topic` remains as a legacy alias for built-in documentation, while
 * the canonical S4.3 surface is now `docs` / `docs topic <topic>`. It
 * intentionally does not accept runtime/custom tools so existing callers keep
 * the same offline-only compatibility behavior.
 */

import { getBuiltinDocTopic, listBuiltinDocs } from "./builtin-docs.js";
import type { CliResult } from "./types.js";

export function handleDocsTopicCommand(
  topic: string | undefined,
  flags: Record<string, string | boolean>,
  requestId: string
): CliResult {
  const result = topic ? getBuiltinDocTopic(topic, flags, {}, requestId) : listBuiltinDocs(flags, {}, requestId);
  if (result.ok && result.data && typeof result.data === "object") {
    const data = result.data as Record<string, unknown>;
    return { ...result, data: { ...data, command: "docs-topic" } };
  }
  return result;
}
