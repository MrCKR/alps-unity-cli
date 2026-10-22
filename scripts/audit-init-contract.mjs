import { readFile } from "node:fs/promises";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const contractPath = join(repoRoot, "contracts", "init-contract.json");
const schemaPath = join(repoRoot, "schemas", "init.schema.json");
const contract = JSON.parse(await readFile(contractPath, "utf8"));
const schema = JSON.parse(await readFile(schemaPath, "utf8"));
const cli = await readFile(join(repoRoot, "src", "cli.ts"), "utf8");
const handlers = (await Promise.all(["package.ts", "init-help.ts", "init-discovery.ts", "init-skill.ts"].map((file) => readFile(join(repoRoot, "src", "cli-commands", file), "utf8")))).join("\n");
const failures = [];
const expectedSubcommands = ["help", "discover", "package", "skill"];
const removedOptions = ["pkg-only", "skill-only", "unity"];
if (contract.version !== 2 || contract.topLevel !== "init" || contract.schema !== "schemas/init.schema.json") failures.push("init contract identity must be version 2 and reference schemas/init.schema.json");
if (JSON.stringify(Object.keys(contract.subcommands)) !== JSON.stringify(expectedSubcommands)) failures.push("init contract subcommands must be help, discover, package, skill in order");
if (JSON.stringify(contract.subcommands.package.options) !== JSON.stringify(["project", "close-editor", "hot", "check", "timeout-ms"])) failures.push("init package options mismatch");
if (JSON.stringify(contract.subcommands.skill.options) !== JSON.stringify(["agent", "scope", "workspace", "target", "check", "timeout-ms"])) failures.push("init skill options mismatch");
if (JSON.stringify(contract.subcommands.skill.requiredAnyOf) !== JSON.stringify([["target"], ["agent", "scope"]])) failures.push("init skill required input modes mismatch");
if (JSON.stringify(contract.subcommands.skill.agents) !== JSON.stringify([".agents", "claude"])) failures.push("init skill agent enum mismatch");
if (JSON.stringify(contract.subcommands.skill.scopes) !== JSON.stringify(["workspace", "user"])) failures.push("init skill scope enum mismatch");
if (JSON.stringify(contract.subcommands.package.checkStatuses) !== JSON.stringify(["installed", "missing", "damaged", "drifted", "conflict"])) failures.push("init package check status enum mismatch");
if (JSON.stringify(contract.subcommands.skill.checkStatuses) !== JSON.stringify(["installed", "missing", "drifted", "damaged"])) failures.push("init skill check status enum mismatch");
if (!Array.isArray(schema.oneOf) || schema.oneOf.length !== 2 || !schema.$defs?.helpData || !schema.$defs?.discoverData || !schema.$defs?.packageData || !schema.$defs?.skillData || !schema.$defs?.error) failures.push("init schema must define success/error envelopes and four data variants");
for (const action of ["init_help", "init_discover", "init_package", "init_package_check", "init_skill", "init_skill_check"]) if (!JSON.stringify(schema).includes(action)) failures.push(`init schema is missing action ${action}`);
for (const command of ["init help", "init discover", "init package --project <UnityProject>", "init skill --target <SkillDirectory>", "init skill --agent <.agents|claude> --scope workspace", "init skill --agent <.agents|claude> --scope user"]) if (!cli.includes(command)) failures.push(`CLI help is missing ${command}`);
for (const removed of removedOptions) {
  if (cli.includes(`${JSON.stringify(removed)}: `)) failures.push(`CLI arity still declares removed option ${removed}`);
  if (handlers.includes(`flags.${removed}`) || handlers.includes(`"${removed}"`)) failures.push(`init runtime still references removed option ${removed}`);
}
for (const symbol of ["handleInitHelp", "handleInitDiscover", "handleInitPackage", "handleInitSkill"]) if (!handlers.includes(symbol)) failures.push(`init dispatcher is missing ${symbol}`);
if (/"(?:pkg-only|skill-only|unity)"\s*:|--(?:pkg-only|skill-only|unity)\b/.test(JSON.stringify({ contract, schema }))) failures.push("public init contract/schema contains a removed option");
if (!handlers.includes("--close-editor") || !handlers.includes("--target")) failures.push("init handlers must expose the approved Editor-close and explicit Skill target controls");
const result = { ok: failures.length === 0, contract: relative(repoRoot, contractPath).replaceAll("\\", "/"), schema: relative(repoRoot, schemaPath).replaceAll("\\", "/"), subcommands: expectedSubcommands, forbiddenOptionsChecked: removedOptions.length, failures };
console.log(JSON.stringify(result, null, 2));
if (failures.length) process.exitCode = 1;
