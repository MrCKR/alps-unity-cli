import { mkdir, readdir, readFile } from "node:fs/promises";
import path from "node:path";
import { runProcess } from "./instance-validation-process.mjs";

const repo = process.cwd();
const cli = path.join(repo, "dist", "cli.js");
const dataRoot = process.env.UNITYCLI_DATA_DIR;
const instanceId = process.env.UNITYCLI_INSTANCE_ID;
if (!dataRoot || !path.isAbsolute(dataRoot)) throw new Error("UNITYCLI_DATA_DIR must be an absolute validation root.");
if (!/^unity-[0-9a-f]{64}$/.test(instanceId ?? "")) throw new Error("UNITYCLI_INSTANCE_ID must be a full instance ID.");
const validationRoot = path.join(dataRoot, ".temp", "instance-validation");
await mkdir(validationRoot, { recursive: true });
const registryPath = path.join(dataRoot, "instances", `${instanceId}.json`);
const initialRecord = JSON.parse(await readFile(registryPath, "utf8"));
const project = initialRecord.identity.displayProjectPath;
const counterKey = "UnityCLI.InstanceValidation.ConcurrentCounter";
const env = { ...process.env, UNITYCLI_DATA_DIR: dataRoot };

const run = (args) => runProcess(process.execPath, [cli, ...args], { cwd: repo, env, timeoutMs: 200_000 });

async function tool(name, code, timeout = 60_000) {
  return run(["tools", "run", name, "--instance", instanceId, "--input", JSON.stringify({ code }), "--timeout-ms", String(timeout)]);
}

function envelope(result) {
  try { return JSON.parse(result.stdout); } catch { throw new Error(`Invalid CLI JSON (${result.code}): ${result.stdout}\n${result.stderr}`); }
}

const beforeValidationFiles = (await readdir(validationRoot)).sort();
let result = await tool("code.execute", `UnityEditor.SessionState.SetInt(${JSON.stringify(counterKey)}, 0); return 0;`);
if (result.code !== 0 || envelope(result).ok !== true) throw new Error(`Counter setup failed: ${result.stdout}`);
result = await tool("code.execute", `var p=System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("UNITYCLI_DATA_DIR"),"instances",${JSON.stringify(instanceId + ".json")});System.IO.File.Delete(p);return !System.IO.File.Exists(p);`);
if (result.code !== 0 || envelope(result).ok !== true) throw new Error(`Registry removal fixture failed: ${result.stdout}`);

const shortWait = run(["tools", "run", "project-settings.info", "--instance", instanceId, "--timeout-ms", "500"]);
const longWaits = Array.from({ length: 6 }, () => run(["tools", "run", "project-settings.info", "--instance", instanceId, "--timeout-ms", "90000"]));
const [shortResult, longResults] = await Promise.all([shortWait, Promise.all(longWaits)]);
if (shortResult.code === 0 || envelope(shortResult).ok !== false) throw new Error(`Short waiter should fail without affecting peers: ${shortResult.stdout}`);
for (const [index, item] of longResults.entries()) {
  const parsed = envelope(item);
  if (item.code !== 0 || parsed.ok !== true || parsed.data.projectName !== "UnityProject") throw new Error(`Long waiter ${index} failed: ${item.stdout}`);
}

const increments = Array.from({ length: 4 }, () => tool("code.execute", `var v=UnityEditor.SessionState.GetInt(${JSON.stringify(counterKey)},0);System.Threading.Thread.Sleep(100);UnityEditor.SessionState.SetInt(${JSON.stringify(counterKey)},v+1);return v+1;`, 60_000));
const reads = Array.from({ length: 4 }, () => run(["tools", "run", "project-settings.info", "--instance", instanceId, "--timeout-ms", "60000"]));
const lists = Array.from({ length: 4 }, () => run(["instances", "list", "--all"]));
const [incrementResults, readResults, listResults] = await Promise.all([Promise.all(increments), Promise.all(reads), Promise.all(lists)]);
for (const [index, item] of incrementResults.entries()) if (item.code !== 0 || envelope(item).ok !== true) throw new Error(`Mutation ${index} failed: ${item.stdout}`);
for (const [index, item] of readResults.entries()) if (item.code !== 0 || envelope(item).ok !== true) throw new Error(`Read ${index} failed: ${item.stdout}`);
for (const [index, item] of listResults.entries()) {
  const parsed = envelope(item);
  if (item.code !== 0 || parsed.ok !== true || parsed.data.invalidRecordCount !== undefined || parsed.data.instances.length !== 1) throw new Error(`List ${index} failed: ${item.stdout}`);
}

const counter = await tool("code.execute", `return UnityEditor.SessionState.GetInt(${JSON.stringify(counterKey)},-1);`);
const counterEnvelope = envelope(counter);
if (counter.code !== 0 || counterEnvelope.ok !== true || counterEnvelope.data.runtime.returnValue !== 4) throw new Error(`Counter expected 4: ${counter.stdout}`);
const cleanup = await tool("code.execute", `UnityEditor.SessionState.EraseInt(${JSON.stringify(counterKey)});return true;`);
if (cleanup.code !== 0 || envelope(cleanup).ok !== true) throw new Error(`Counter cleanup failed: ${cleanup.stdout}`);

const record = JSON.parse(await readFile(registryPath, "utf8"));
const codeTemp = path.join(project, "Library", "UnityCli", "code-exec-temp");
let codeTempFiles = [];
try { codeTempFiles = await readdir(codeTemp); } catch {}
const afterValidationFiles = (await readdir(validationRoot)).sort();
if (await readFile(registryPath, "utf8").then(() => false, () => true)) throw new Error("Registry disappeared after concurrent calls.");
console.log(JSON.stringify({ ok: true, shortError: envelope(shortResult).error.code, longWaiters: longResults.length, mutations: incrementResults.length, finalCounter: counterEnvelope.data.runtime.returnValue, concurrentReads: readResults.length, concurrentLists: listResults.length, finalRevision: record.revision, finalSessions: record.sessions.length, registryTempExists: await readFile(`${registryPath}.tmp`).then(() => true, () => false), codeTempEntries: codeTempFiles.length, validationFileSetUnchanged: JSON.stringify(beforeValidationFiles) === JSON.stringify(afterValidationFiles) }));
