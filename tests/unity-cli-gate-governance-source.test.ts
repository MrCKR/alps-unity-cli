import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const agentPath = ".pi/agents/unity-cli-gate.md";
const validationPath = ".pi/skills/unity-cli-validation/SKILL.md";
const agentsPath = "AGENTS.md";
const publicSkillPath = "skills/unity_cli/SKILL.md";
const readmePath = "README.md";
const projectFixturePath = "Dev/UnityProject/Library/UnityCli/dir022-validation-project/Assets/Editor/Dir022ProjectCommand.cs";
const packageFixturePath = "Dev/UnityProject/Library/UnityCli/dir022-validation-project/Packages/com.example.dir022/Editor/Dir022PackageCommand.cs";

test("fresh gate keeps task values above defaults and forbids probe calls", async () => {
  const [agent, validation, agents] = await Promise.all([
    readFile(agentPath, "utf8"),
    readFile(validationPath, "utf8"),
    readFile(agentsPath, "utf8"),
  ]);
  for (const source of [agent, validation, agents]) {
    assert.match(source, /任务明确[^\n]*业务值优先|任务值与example\/default不同时/);
    assert.match(source, /default\/example[^\n]*(连通性|smoke|probe)/);
    assert.match(source, /业务调用必须严格串行/);
    assert.match(source, /同一assistant turn|同一个 assistant turn/);
  }
  assert.match(agent, /任务要求的每个literal如何映射到schema业务字段/);
  assert.match(validation, /不得用预先并行提交的后续业务调用绕过停止边界/);
});

test("public guidance says examples do not override caller intent", async () => {
  const [skill, readme] = await Promise.all([
    readFile(publicSkillPath, "utf8"),
    readFile(readmePath, "utf8"),
  ]);
  assert.match(skill, /任务明确要求的业务值优先/);
  assert.match(skill, /不能把default\/example当作连通性或probe调用/);
  assert.match(readme, /调用方明确要求的业务值优先于schema default和example值/);
});

test("DIR-022 echo fixtures describe caller payload and fixed source prefixes", async () => {
  const [projectFixture, packageFixture] = await Promise.all([
    readFile(projectFixturePath, "utf8"),
    readFile(packageFixturePath, "utf8"),
  ]);
  assert.match(projectFixture, /Echo a caller-provided payload with the project source prefix/);
  assert.match(projectFixture, /fixed project: source prefix[\s\S]*caller-requested payload/);
  assert.match(packageFixture, /Echo a caller-provided payload with the third-party package source prefix/);
  assert.match(packageFixture, /fixed package: source prefix[\s\S]*caller-requested payload/);
});
