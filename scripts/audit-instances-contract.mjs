import { readFile } from "node:fs/promises";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const contractPath = join(repoRoot, "contracts", "instances-contract.json");
const schemaPath = join(repoRoot, "schemas", "instances.schema.json");
const contract = JSON.parse(await readFile(contractPath, "utf8"));
const schema = JSON.parse(await readFile(schemaPath, "utf8"));
const [cli, command, instances, supervisor, cleanup, registry, bridgeRegistry, operation, commandContract] = await Promise.all([
  readFile(join(repoRoot, "src", "cli.ts"), "utf8"),
  readFile(join(repoRoot, "src", "cli-commands", "instances.ts"), "utf8"),
  readFile(join(repoRoot, "src", "instances.ts"), "utf8"),
  readFile(join(repoRoot, "src", "bridge", "session-supervisor.ts"), "utf8"),
  readFile(join(repoRoot, "unitypkg", "Editor", "Infrastructure", "UnityCliInstanceCleanup.cs"), "utf8"),
  readFile(join(repoRoot, "unitypkg", "Editor", "Infrastructure", "UnityCliInstanceRegistry.cs"), "utf8"),
  readFile(join(repoRoot, "unitypkg", "Editor", "Bridge", "BridgeCommandRegistry.cs"), "utf8"),
  readFile(join(repoRoot, "unitypkg", "Editor", "Infrastructure", "UnityCliOperationCoordinator.cs"), "utf8"),
  readFile(join(repoRoot, "src", "contracts", "command-contract.ts"), "utf8"),
]);
const failures = [];
const statuses = ["ready", "transitioning", "blocked", "unresponsive", "unverifiable", "offline", "conflict"];
const retention = { offlineMax: 100, damagedMax: 20, maxAgeDays: 30, scanLimit: 2000, deleteLimit: 20 };
if (contract.version !== 1 || contract.topLevel !== "instances" || contract.schema !== "schemas/instances.schema.json") failures.push("instances contract identity mismatch");
if (JSON.stringify(contract.registry.statuses) !== JSON.stringify(statuses)) failures.push("instance status enum mismatch");
if (JSON.stringify(contract.subcommands.list.options) !== JSON.stringify(["all"]) || contract.subcommands.list.readOnly !== true) failures.push("instances list options/read-only mismatch");
if (JSON.stringify(contract.subcommands.prune.options) !== JSON.stringify(["instance", "timeout-ms"]) || contract.subcommands.prune.privateCommand !== "internal.instances.prune" || contract.subcommands.prune.mutation !== true) failures.push("instances prune command contract mismatch");
if (JSON.stringify(contract.subcommands.prune.retention) !== JSON.stringify(retention)) failures.push("instances prune retention mismatch");
if (!schema.$defs?.listSuccess || !schema.$defs?.pruneSuccess || !schema.$defs?.errorEnvelope || !schema.$defs?.pruneData || !schema.$defs?.retention) failures.push("instances schema definitions are incomplete");
for (const status of statuses) if (!JSON.stringify(schema.$defs.instanceSummary).includes(status)) failures.push(`instances schema missing status ${status}`);
for (const [key, value] of Object.entries(retention)) {
  if (schema.$defs.retention?.properties?.[key]?.const !== value) failures.push(`instances schema retention ${key} mismatch`);
  const csharpName = { offlineMax: "InstanceCleanupRetainedOffline", damagedMax: "InstanceCleanupRetainedDamaged", maxAgeDays: "InstanceCleanupRetentionDays", scanLimit: "InstanceCleanupMaxScanned", deleteLimit: "InstanceCleanupMaxDeleted" }[key];
  if (!cleanup.includes(`${csharpName} = ${value}`)) failures.push(`C# cleanup retention ${key} mismatch`);
  if (!command.includes(`${key}: ${value}`)) failures.push(`TypeScript prune retention ${key} mismatch`);
}
if (!cli.includes('command === "prune" ? [...global, "instance", "timeout-ms"] : [...global, "all"]')) failures.push("CLI instances option routing mismatch");
if (!command.includes('executeBridgePayload(requestId, "internal.instances.prune"') || !command.includes('completionContractIdentity: "instances-prune-v1"')) failures.push("CLI writer-owned prune route mismatch");
if (!command.includes('exactObject(value, ["removed", "remaining", "deferred", "scanned", "truncated", "retention"])')) failures.push("CLI prune result must be closed");
if (!cleanup.includes('"internal.instances.prune"') || !bridgeRegistry.includes("RegisterInstanceCleanupCommand(registry)")) failures.push("Unity private prune command is not registered");
if (!cleanup.includes("BeginDirectEffect(request.RequestId)") || !cleanup.includes("RecordDirectCompletionEvidence") || !operation.includes("IsInstanceCleanupCommand(toolName)")) failures.push("Unity private prune completion proof mismatch");
if (!commandContract.includes('"internal.instances.prune"')) failures.push("private command exclusion is missing internal.instances.prune");
if (!registry.includes("ReadCurrentRegistryForPublication") || !registry.includes("RegistryPublicationMaxAttempts = 4")) failures.push("Unity readiness recovery/retry contract is missing");
for (const code of ["E_INSTANCE_NOT_FOUND", "E_INSTANCE_NOT_RUNNING", "E_INSTANCE_IDENTITY_MISMATCH", "E_INSTANCE_UNVERIFIABLE"]) if (!supervisor.includes(code) && !instances.includes(code)) failures.push(`readiness wait code missing ${code}`);
if (!supervisor.includes("waitForStartingSelection") || !instances.includes("observation有截止时间时可等待启动中的writer首次发布")) failures.push("initial readiness wait implementation is missing");
if (/removed:\s*0[\s\S]*writer:\s*["']unity-bridge/.test(instances) || instances.includes("Only the C# registry writer may remove")) failures.push("old fixed prune placeholder remains");
if (!Array.isArray(schema.oneOf) || schema.oneOf.length !== 3) failures.push("instances schema must expose list/prune/error envelopes only");
const result = { ok: failures.length === 0, contract: relative(repoRoot, contractPath).replaceAll("\\", "/"), schema: relative(repoRoot, schemaPath).replaceAll("\\", "/"), statuses: statuses.length, retention, failures };
console.log(JSON.stringify(result, null, 2));
if (failures.length) process.exitCode = 1;
