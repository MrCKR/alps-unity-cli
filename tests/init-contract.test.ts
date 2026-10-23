import assert from "node:assert/strict";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { execute } from "../src/cli.js";
import { validateSchema } from "../src/schema/validator.js";

process.env.UNITY_CLI_ALLOW_NON_WINDOWS = "1";
const contract = JSON.parse(await readFile("contracts/init-contract.json", "utf8")) as any;
const schema = JSON.parse(await readFile("schemas/init.schema.json", "utf8")) as any;
function assertValid(value: unknown): void { assert.deepEqual(validateSchema(schema, value), [], JSON.stringify(value)); }

test("init contract and schema enumerate the final command surface only", () => {
  assert.equal(contract.version, 3);
  assert.equal(contract.schema, "schemas/init.schema.json");
  assert.deepEqual(Object.keys(contract.subcommands), ["help", "discover", "package", "skill"]);
  assert.deepEqual(contract.subcommands.skill.agents, [".agents", "claude"]);
  assert.deepEqual(contract.subcommands.skill.scopes, ["workspace", "user"]);
  assert.deepEqual(contract.subcommands.package.options, ["project", "close-editor", "check", "timeout-ms"]);
  assert.deepEqual(contract.subcommands.skill.requiredAnyOf, [["target"], ["agent", "scope"]]);
  assert.doesNotMatch(JSON.stringify({ contract, schema }), /pkg-only|skill-only|<pi\|/);
  assert.doesNotMatch(JSON.stringify(schema.$defs.discoverData), /closeEditor/);
  assert.equal(schema.oneOf.length, 2);
  assert.ok(schema.$defs.helpData && schema.$defs.discoverData && schema.$defs.packageData && schema.$defs.skillData && schema.$defs.error);
  assert.ok(schema.$defs.helpData.properties.version && schema.$defs.packageData.properties.cli);
});

test("actual init success/check envelopes pass full JSON Schema validation", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-init-contract-"));
  try {
    const project = join(root, "UnityProject");
    await mkdir(join(project, "Assets"), { recursive: true });
    await mkdir(join(project, "Packages"), { recursive: true });
    await mkdir(join(project, "ProjectSettings"), { recursive: true });
    await writeFile(join(project, "Packages", "manifest.json"), "{\"dependencies\":{}}\n", "utf8");
    await writeFile(join(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.61f1\n", "utf8");
    const runtime = { dataDir: join(root, "data"), userHome: root } as never;
    const results = [
      await execute(["init", "help"], runtime),
      await execute(["init", "discover"], runtime),
      await execute(["init", "package", "--project", project, "--check"], runtime),
      await execute(["init", "skill", "--agent", ".agents", "--scope", "user", "--check"], runtime),
      await execute(["init", "skill", "--target", join(root, ".pi", "skills", "unitycli"), "--check"], runtime),
    ];
    for (const result of results) { assert.equal(result.ok, true, JSON.stringify(result)); assertValid(result); }
    const helpWithUnknown = { ...results[0], unknown: true };
    assert.ok(validateSchema(schema, helpWithUnknown).length > 0);
    const helpWithoutVersion = JSON.parse(JSON.stringify(results[0])); delete helpWithoutVersion.data.version;
    assert.ok(validateSchema(schema, helpWithoutVersion).length > 0);
    const invalidAgent = JSON.parse(JSON.stringify(results[3])); invalidAgent.data.agent = "pi";
    assert.ok(validateSchema(schema, invalidAgent).length > 0);
    const invalidNextActions = JSON.parse(JSON.stringify(results[2])); invalidNextActions.data.nextActions = "retry";
    assert.ok(validateSchema(schema, invalidNextActions).length > 0);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("actual init stable error envelopes pass full JSON Schema validation", async () => {
  const root = await mkdtemp(join(tmpdir(), "unity-init-error-contract-"));
  try {
    const runtime = { dataDir: join(root, "data"), userHome: root } as never;
    const errors = [
      await execute(["init"], runtime),
      await execute(["init", "package"], runtime),
      await execute(["init", "skill", "--agent", "pi", "--scope", "user"], runtime),
      await execute(["init", "skill", "--agent", ".agents", "--scope", "invalid"], runtime),
      await execute(["init", "help", "--check"], runtime),
      await execute(["init", "discover", "extra"], runtime),
    ];
    for (const result of errors) { assert.equal(result.ok, false, JSON.stringify(result)); assertValid(result); }
    const unknownErrorField = JSON.parse(JSON.stringify(errors[0])); unknownErrorField.error.log = "internal";
    assert.ok(validateSchema(schema, unknownErrorField).length > 0);
    const missingTransient = JSON.parse(JSON.stringify(errors[1])); delete missingTransient.error.transient;
    assert.ok(validateSchema(schema, missingTransient).length > 0);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
