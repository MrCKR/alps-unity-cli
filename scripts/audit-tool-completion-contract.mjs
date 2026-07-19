import fs from "node:fs";
import path from "node:path";
import { collectPublicToolInventory } from "./lib/public-tool-inventory.mjs";

const root = process.cwd();
const strict = process.argv.includes("--strict");
const contractPath = path.join(root, "contracts", "tool-completion-contract.json");
const allowedExecutionClasses = new Set(["atomic", "observed-state", "resumable", "external"]);
const allowedDeliveryModes = new Set(["direct-terminal", "job-deferred"]);
const allowedDirectInvocationPolicies = new Set(["allowed", "deferred-only", "prohibited-unprovable"]);
const allowedFrozenDeliveryPolicies = new Set(["ordinary", "preserved"]);
const requiredJobDeferred = new Set(["build.run", "editor.refresh", "test.run"]);
const failures = [];

// 仅允许明确列出的 descriptor 误判项跳过 commit 要求。每项必须是无业务 effect 的 fail-closed
// Tool；不得按目录、owner、execution class 或其他泛化条件豁免。
const NO_EFFECT_FAIL_CLOSED = new Map([
]);

/** 记录失败并继续收集完整差异。 */
function fail(message) {
  failures.push(message);
}

if (!fs.existsSync(contractPath)) {
  fail("contracts/tool-completion-contract.json 不存在");
}

const inventory = collectPublicToolInventory(root);
const contract = fs.existsSync(contractPath)
  ? JSON.parse(fs.readFileSync(contractPath, "utf8"))
  : { tools: [] };
const entries = Array.isArray(contract.tools) ? contract.tools : [];
const byName = new Map();

for (const entry of entries) {
  if (!entry?.toolName) {
    fail("completion contract 存在缺少 toolName 的记录");
    continue;
  }
  if (byName.has(entry.toolName)) fail(`duplicate completion contract: ${entry.toolName}`);
  byName.set(entry.toolName, entry);
  if (!allowedExecutionClasses.has(entry.executionClass)) fail(`${entry.toolName}: executionClass 无效`);
  if (!allowedDeliveryModes.has(entry.deliveryMode)) fail(`${entry.toolName}: deliveryMode 无效`);
  if (!entry.completionPredicate?.kind || !entry.completionPredicate?.description) fail(`${entry.toolName}: completionPredicate 不完整`);
  if (!entry.proofSource?.kind || !entry.proofSource?.description) fail(`${entry.toolName}: proofSource 不完整`);
  if (!entry.reloadPolicy?.kind || !entry.reloadPolicy?.description) fail(`${entry.toolName}: reloadPolicy 不完整`);
  if (!/^E_[A-Z0-9_]+$/.test(entry.timeoutError ?? "")) fail(`${entry.toolName}: timeoutError 无效`);
  if (!entry.outcomeUnknownPolicy?.kind || !entry.outcomeUnknownPolicy?.description) fail(`${entry.toolName}: outcomeUnknownPolicy 不完整`);
  const directInvocationPolicy = entry.directInvocationPolicy?.kind ?? "allowed";
  const frozenDeliveryPolicy = entry.frozenDeliveryPolicy?.kind ?? "ordinary";
  if (!allowedDirectInvocationPolicies.has(directInvocationPolicy)) fail(`${entry.toolName}: directInvocationPolicy 无效`);
  if (!allowedFrozenDeliveryPolicies.has(frozenDeliveryPolicy)) fail(`${entry.toolName}: frozenDeliveryPolicy 无效`);
  if (directInvocationPolicy !== "allowed" && frozenDeliveryPolicy !== "preserved") fail(`${entry.toolName}: restricted direct invocation 必须绑定 preserved frozen delivery`);
  const publicText = JSON.stringify({ completionPredicate: entry.completionPredicate, proofSource: entry.proofSource });
  if (/accepted|requested|queued|handler[- ]returned|ExecuteAsync returned/i.test(publicText)) {
    fail(`${entry.toolName}: completion proof 使用了禁止的中间态或 handler-returned 语义`);
  }
}

const inventoryByName = new Map(inventory.map((tool) => [tool.name, tool]));
for (const tool of inventory) {
  const entry = byName.get(tool.name);
  if (!entry) {
    fail(`missing completion contract: ${tool.name}`);
    continue;
  }
  if (entry.descriptorOwner !== tool.descriptorOwner) fail(`${tool.name}: descriptorOwner=${entry.descriptorOwner}, expected=${tool.descriptorOwner}`);
  if (entry.source !== tool.source) fail(`${tool.name}: source=${entry.source}, expected=${tool.source}`);
}
for (const entry of entries) {
  if (!inventoryByName.has(entry.toolName)) fail(`orphan completion contract: ${entry.toolName}`);
}

const actualJobDeferred = new Set(entries.filter((entry) => entry.deliveryMode === "job-deferred").map((entry) => entry.toolName));
for (const name of requiredJobDeferred) if (!actualJobDeferred.has(name)) fail(`job-deferred contract missing: ${name}`);
for (const name of actualJobDeferred) if (!requiredJobDeferred.has(name)) fail(`unexpected job-deferred contract: ${name}`);

const csharpCommitAudit = auditCSharpCommitEffects(inventory, byName);
for (const failure of csharpCommitAudit.failures) fail(failure);
for (const failure of auditRestrictedDirectGate(entries)) fail(failure);

const result = {
  ok: failures.length === 0,
  version: contract.version ?? null,
  publicTools: inventory.length,
  completionContracts: entries.length,
  directTerminal: entries.filter((entry) => entry.deliveryMode === "direct-terminal").length,
  jobDeferred: actualJobDeferred.size,
  directInvocationPolicies: Object.fromEntries(
    [...allowedDirectInvocationPolicies].map((kind) => [kind, entries.filter((entry) => (entry.directInvocationPolicy?.kind ?? "allowed") === kind).length]),
  ),
  frozenDeliveryPolicies: Object.fromEntries(
    [...allowedFrozenDeliveryPolicies].map((kind) => [kind, entries.filter((entry) => (entry.frozenDeliveryPolicy?.kind ?? "ordinary") === kind).length]),
  ),
  executionClasses: Object.fromEntries(
    [...allowedExecutionClasses].map((kind) => [kind, entries.filter((entry) => entry.executionClass === kind).length]),
  ),
  csharpCommitEffect: csharpCommitAudit.summary,
  failures,
};

console.log(JSON.stringify(result, null, 2));
if (strict && failures.length > 0) process.exitCode = 1;

/** restricted direct policy 必须在 HTTP Bridge journal/handler 前执行；frozen batch 仅由独立进程内入口持有。 */
function auditRestrictedDirectGate(contractEntries) {
  if (!contractEntries.some((entry) => (entry.directInvocationPolicy?.kind ?? "allowed") !== "allowed")) return [];
  const coordinator = fs.readFileSync(path.join(root, "unitypkg", "Editor", "Infrastructure", "UnityCliOperationCoordinator.cs"), "utf8");
  const workQueue = fs.readFileSync(path.join(root, "unitypkg", "Editor", "Bridge", "BridgeWorkQueue.cs"), "utf8");
  const http = fs.readFileSync(path.join(root, "unitypkg", "Editor", "Bridge", "BridgeHttpServer.cs"), "utf8");
  const supervisor = fs.readFileSync(path.join(root, "src", "bridge", "session-supervisor.ts"), "utf8");
  const bridge = fs.readFileSync(path.join(root, "unitypkg", "Editor", "UnityCliBridge.cs"), "utf8");
  const policyIndex = coordinator.indexOf("contract.DirectInvocationPolicy");
  const journalIndex = coordinator.indexOf("AcceptOrRecover(request");
  const failures = [];
  if (policyIndex < 0 || journalIndex < 0 || policyIndex > journalIndex)
    failures.push("restricted direct policy must be enforced before Bridge journal acceptance");
  if (/preservedFrozenInvocation|FrozenDeliveryPolicy|Application\.isBatchMode/.test(coordinator.slice(policyIndex, journalIndex)))
    failures.push("HTTP direct policy must not grant frozen delivery from process mode or caller metadata");
  if (/PreservedFrozenInvocation|preservedFrozenInvocation/.test(workQueue)
    || /x-unity-cli-invocation-origin|invocationOrigin/.test(supervisor)
    || /x-unity-cli-invocation-origin|internalOrigin|invocationOrigin/.test(http))
    failures.push("HTTP Bridge must not treat caller-controlled invocation origin as a privilege");
  if (!/ExecuteBatchCommandAsync[\s\S]*response = await Execute\(request, timeout\.Token\)/.test(bridge))
    failures.push("frozen batch delivery must remain on the independent in-process batch entrypoint");
  return failures;
}

/**
 * 从 public inventory 与声明 descriptor 的同一 C# source 分类 mutation，避免由路径、Tool 名称
 * 或 handler 返回值推断。仅 direct-terminal mutation 可进入本次 commit-effect 审计。
 */
function auditCSharpCommitEffects(publicInventory, contracts) {
  const summary = {
    csharpDirectTools: 0,
    directMutations: 0,
    directReadOnly: 0,
    delegatedCommitEffect: [],
    noEffectFailClosed: [],
    missingCommitEffect: [],
    readOnlyCommitEffect: [],
  };
  const auditFailures = [];
  const commitHelperSources = collectCommitHelperSources();

  for (const tool of publicInventory) {
    if (tool.descriptorOwner === "ts-local") continue;
    const contract = contracts.get(tool.name);
    if (!contract || contract.deliveryMode !== "direct-terminal") continue;
    summary.csharpDirectTools += 1;
    const sourceText = fs.readFileSync(path.join(root, tool.source), "utf8");
    const mutation = csharpDescriptorDeclaresMutation(sourceText);
    const directCommits = (sourceText.match(/\bctx\s*\.\s*CommitEffect\s*\(\s*\)/g) ?? []).length;
    const delegatedHelpers = referencedCommitHelpers(sourceText, commitHelperSources);
    const reachesCommit = directCommits > 0 || delegatedHelpers.length > 0;

    if (!mutation) {
      summary.directReadOnly += 1;
      if (reachesCommit) {
        summary.readOnlyCommitEffect.push(tool.name);
        auditFailures.push(`${tool.name}: read-only direct Tool must not call ctx.CommitEffect() (${tool.source})`);
      }
      continue;
    }

    summary.directMutations += 1;
    const noEffectReason = NO_EFFECT_FAIL_CLOSED.get(tool.name);
    if (noEffectReason) {
      if (reachesCommit) auditFailures.push(`${tool.name}: no-effect-fail-closed Tool must not reach ctx.CommitEffect() (${tool.source})`);
      else summary.noEffectFailClosed.push({ toolName: tool.name, reason: noEffectReason });
      continue;
    }
    if (!reachesCommit) {
      summary.missingCommitEffect.push(tool.name);
      auditFailures.push(`${tool.name}: direct mutation Tool must reach ctx.CommitEffect() after prevalidation and before its first effect (${tool.source})`);
    } else if (directCommits === 0) {
      summary.delegatedCommitEffect.push({ toolName: tool.name, helpers: delegatedHelpers });
    }
  }

  for (const [toolName, reason] of NO_EFFECT_FAIL_CLOSED) {
    const tool = publicInventory.find((candidate) => candidate.name === toolName);
    const contract = contracts.get(toolName);
    if (!tool || tool.descriptorOwner === "ts-local" || contract?.deliveryMode !== "direct-terminal")
      auditFailures.push(`${toolName}: no-effect-fail-closed entry must name one public C# direct-terminal Tool`);
    if (typeof reason !== "string" || reason.trim().length === 0)
      auditFailures.push(`${toolName}: no-effect-fail-closed entry requires a concrete reason`);
  }

  return { failures: auditFailures, summary };
}

/** 建立共享 helper 类到源码的索引，允许 Tool 将 commit 委托给同一调用链中的强类型 helper。 */
function collectCommitHelperSources() {
  const byClass = new Map();
  for (const base of [path.join(root, "unitypkg", "Editor"), path.join(root, "extensions")]) {
    for (const filePath of walkCSharp(base)) {
      const source = fs.readFileSync(filePath, "utf8");
      if (!/\bctx\s*\.\s*CommitEffect\s*\(\s*\)/.test(source)) continue;
      for (const match of source.matchAll(/\b(?:class|struct)\s+([A-Z][A-Za-z0-9_]*)\b/g)) byClass.set(match[1], source);
    }
  }
  return byClass;
}

/** 仅跟随源码中显式引用的 helper 类型；不按目录或 Tool 名称豁免。 */
function referencedCommitHelpers(sourceText, helperSources) {
  const helpers = new Set();
  for (const match of sourceText.matchAll(/\b([A-Z][A-Za-z0-9_]*(?:Support|StateTool))\s*\.\s*[A-Z][A-Za-z0-9_]*\s*\(/g)) {
    if (helperSources.has(match[1])) helpers.add(match[1]);
  }
  return [...helpers].sort();
}

/** 递归枚举产品 C#，用于解析共享 commit helper。 */
function walkCSharp(base) {
  if (!fs.existsSync(base)) return [];
  const files = [];
  for (const entry of fs.readdirSync(base, { withFileTypes: true })) {
    const fullPath = path.join(base, entry.name);
    if (entry.isDirectory()) files.push(...walkCSharp(fullPath));
    else if (entry.isFile() && entry.name.endsWith(".cs")) files.push(fullPath);
  }
  return files;
}

/** 只接受 descriptor 中公开声明的 mutation 风险字段；缺失或动态声明按 read-only fail closed。 */
function csharpDescriptorDeclaresMutation(sourceText) {
  return /\bDestructiveHint\s*=\s*true\b/.test(sourceText)
    || /\bSupportsDryRun\s*=\s*true\b/.test(sourceText)
    || /\bSideEffects\s*=\s*new\[\]\s*\{\s*[^}\s]/.test(sourceText)
    || /\bPlayModeSupport\s*=\s*"session-affecting"/.test(sourceText);
}
