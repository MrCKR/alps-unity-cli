import assert from "node:assert/strict";
import { access, readFile } from "node:fs/promises";
import test from "node:test";

const agentPath = ".pi/agents/unity-cli-gate.md";
const validationPath = ".pi/skills/unity-cli-validation/SKILL.md";
const issueLifecyclePath = ".pi/skills/unity-cli-issue-lifecycle/SKILL.md";
const releasePath = ".pi/skills/unity-cli-release/SKILL.md";
const agentsPath = "AGENTS.md";
const publicSkillPath = "skills/unitycli/SKILL.md";
const readmePath = "README.md";

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

test("repository governance separates implementation from affected-scope validation", async () => {
  const [agent, agents, validation, issueLifecycle, release] = await Promise.all([
    readFile(agentPath, "utf8"),
    readFile(agentsPath, "utf8"),
    readFile(validationPath, "utf8"),
    readFile(issueLifecyclePath, "utf8"),
    readFile(releasePath, "utf8"),
  ]);
  for (const source of [agents, validation, issueLifecycle]) assert.doesNotMatch(source, /unity-cli-plan/);
  await assert.rejects(access(".pi/skills/unity-cli-plan/SKILL.md"));
  assert.match(agents, /skills\/unitycli\/SKILL\.md[^\n]*安装后/);
  assert.match(agents, /实现未收敛前不得运行正式自动化测试[^\n]*unity-cli-gate/);
  assert.match(validation, /未完成前不得运行正式自动化测试[^\n]*陌生 AI 门禁/);
  for (const source of [agents, validation]) assert.match(source, /只覆盖本次增删改/);
  assert.match(validation, /AffectedValidationScope/);
  assert.match(validation, /禁止运行无筛选的 `npm test`[^\n]*`npm run build:test`[^\n]*全 Tool Live 矩阵/);
  assert.match(validation, /缺少所需选择器[^\n]*先为 harness 增加并验证选择器/);
  assert.match(agent, /全量 smoke\/回归\/矩阵[^\n]*立即报告 `INVALID`/);
  assert.match(agent, /最小 discovery、业务调用、业务验证和 cleanup 链/);
  assert.doesNotMatch(release, /npm test[^\n]*全量/);
  assert.match(release, /增删改及其直接影响面/);
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
