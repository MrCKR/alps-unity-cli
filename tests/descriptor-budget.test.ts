import assert from "node:assert/strict";
import test from "node:test";
import { join } from "node:path";
import { pathToFileURL } from "node:url";

interface BudgetReport {
  failures: string[];
  warnings: string[];
  tools: { name: string; notesTokens: number; descriptionTokens: number }[];
  summary: { toolCount: number; notesTokens: number };
}

interface DescriptorParse {
  name: string;
  notes: string[];
  unresolvedNotesElements: string[];
  unresolvedNotesCalls: string[];
}

interface BudgetModule {
  auditDescriptorBudget(): Promise<BudgetReport>;
  readCSharpDescriptor(source: string, noteIndex: Map<string, string[]>, methods: Map<string, string[]>): DescriptorParse | undefined;
}

const moduleUrl = pathToFileURL(join(process.cwd(), "scripts", "audit-descriptor-budget.mjs")).href;

async function budget(): Promise<BudgetModule> {
  return await import(moduleUrl) as BudgetModule;
}

/** 真实 catalog 必须零 failure：预算超限或 notes 无法离线核对都算产品缺陷。 */
test("every Tool descriptor stays inside its text budget and is offline measurable", async () => {
  const report = await (await budget()).auditDescriptorBudget();
  assert.ok(report.tools.length > 0, "the catalog must contain descriptors");
  assert.deepEqual(report.failures, [], "descriptor budget failures");
});

/** notes 元素与零参数文本方法引用都必须可核对，未解析的内容不能静默少算。 */
test("notes elements and zero-argument text references are measured or reported", async () => {
  const module = await budget();
  const source = [
    "public sealed class ProbeTool",
    "{",
    "    public ToolDescriptor Describe()",
    "    {",
    "        return new ToolDescriptor",
    "        {",
    '            Name = "probe.tool",',
    '            Group = "probe",',
    '            Description = "Probe descriptor.",',
    '            Notes = new[] { "inline text", Opaque(), DebugLimits.SessionSummary() },',
    "        };",
    "    }",
    "}",
  ].join("\n");

  const withoutResolution = module.readCSharpDescriptor(source, new Map(), new Map());
  assert.ok(withoutResolution);
  assert.deepEqual(withoutResolution!.notes, ["inline text"], "only literal text is measurable without a method index");
  assert.deepEqual(withoutResolution!.unresolvedNotesCalls, ["Opaque()", "DebugLimits.SessionSummary()"], "unresolvable references must be reported");

  const resolved = module.readCSharpDescriptor(source, new Map(), new Map([["SessionSummary", ["Limits: one session."]]]));
  assert.ok(resolved);
  assert.deepEqual(resolved!.notes, ["inline text", "Limits: one session."], "a single-source text reference is expanded");
  assert.deepEqual(resolved!.unresolvedNotesCalls, ["Opaque()"], "only the remaining unresolved reference is reported");
  assert.deepEqual(resolved!.unresolvedNotesElements, [], "an element with measurable text is not reported as empty");

  const emptyElement = module.readCSharpDescriptor(
    source.replace('Notes = new[] { "inline text", Opaque(), DebugLimits.SessionSummary() }', "Notes = new[] { Held }"),
    new Map(),
    new Map()
  );
  assert.ok(emptyElement);
  assert.deepEqual(emptyElement!.unresolvedNotesElements, ["Held"], "an element without any measurable text must be reported");
});
