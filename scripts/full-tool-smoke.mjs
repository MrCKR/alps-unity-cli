import { spawnSync } from 'node:child_process';
import { mkdirSync, writeFileSync, readFileSync, existsSync, rmSync } from 'node:fs';
import { resolve, join } from 'node:path';

const distPrefix = existsSync(resolve('dist/src/cli.js')) ? '../dist/src' : '../dist';
await import(`${distPrefix}/extensions/index.js`);
const { bridgeCommandRegistry } = await import(`${distPrefix}/bridge/index.js`);
const { allRegisteredToolMetadata } = await import(`${distPrefix}/tools/metadata/index.js`);

const project = resolve(process.argv[2] || 'Dev/UnityProject');
const unity = process.argv[3] || 'C:/Program Files/Unity/Unity-2022.3.61f1/Editor/Unity.exe';
const outDir = resolve('.tmp/evidence/full-tool-smoke');
mkdirSync(outDir, { recursive: true });

const baseFlags = {
  name: 'UnityCliSmokeObject', target: 'UnityCliSmokeRoot/UnityCliSmokeObject', parent: 'UnityCliSmokeRoot', path: 'Assets/UnityCliSmoke/Smoke.txt',
  output: 'Assets/UnityCliSmoke/shot.png', assetPath: 'Assets/UnityCliSmoke/Smoke.txt', source: 'Assets/UnityCliSmoke/Smoke.txt', to: 'Assets/UnityCliSmoke/SmokeCopy.txt',
  type: 'Transform', component: 'Transform', componentType: 'Transform', property: 'tag', value: 'Untagged', action: 'list', tool: 'Move', format: 'png',
  query: 'UnityCliSmoke', keyword: 'UnityCliSmoke', text: 'hello', label: 'hello', content: 'smoke', code: 'return 1;',
  width: '64', height: '64', pageSize: '10', maxDepth: '2', distance: '10', durationMs: '10', frames: '1',
  originX: '0', originY: '1', originZ: '0', directionX: '0', directionY: '-1', directionZ: '0',
  startX: '0', startY: '0', startZ: '0', endX: '0', endY: '1', endZ: '0', radius: '0.5', shape: 'sphere',
  create: true, overwrite: true, dryRun: true, yes: true
};

function flagsFor(command) {
  const flags = { ...baseFlags };
  if (command.startsWith('scene.')) flags.path = command === 'scene.create' ? 'Assets/UnityCliSmoke/NewScene.unity' : 'Assets/UnityCliSmoke/SmokeScene.unity';
  if (command.startsWith('prefab.')) { flags.path = 'Assets/UnityCliSmoke/Smoke.prefab'; flags.output = 'Assets/UnityCliSmoke/SmokeOut.prefab'; flags.name = 'UnityCliSmokePrefab'; }
  if (command.startsWith('scriptable-object.')) { flags.path = command === 'scriptable-object.create' ? 'Assets/UnityCliSmoke/New.asset' : 'Assets/UnityCliSmoke/Smoke.asset'; flags.type = 'UnityCliSmokeSettings'; flags.property = 'text'; flags.value = 'updated'; }
  if (command.startsWith('material.')) { flags.path = command === 'material.create' ? 'Assets/UnityCliSmoke/New.mat' : 'Assets/UnityCliSmoke/Smoke.mat'; flags.material = 'Assets/UnityCliSmoke/Smoke.mat'; }
  if (command.startsWith('shader.')) { flags.path = command === 'shader.create' ? 'Assets/UnityCliSmoke/New.shader' : 'Assets/UnityCliSmoke/Smoke.shader'; flags.content = 'Shader "UnityCli/SmokeNew" { SubShader { Pass {} } }'; }
  if (command.startsWith('texture.')) { flags.path = command === 'texture.create' ? 'Assets/UnityCliSmoke/NewTexture.png' : 'Assets/UnityCliSmoke/SmokeTexture.png'; flags.source = 'Temp/UnityCliSmokeSource.png'; }
  if (command.startsWith('camera.')) { flags.target = 'UnityCliSmokeCamera'; flags.name = 'UnityCliSmokeCamera'; flags.output = 'Assets/UnityCliSmoke/camera.png'; }
  if (command.startsWith('screenshot.')) { flags.target = 'UnityCliSmokeRoot/UnityCliSmokeObject'; flags.path = 'Assets/UnityCliSmoke/screenshot.png'; flags.output = flags.path; }
  if (command.startsWith('asset.')) { flags.path = 'Assets/UnityCliSmoke/Smoke.txt'; flags.assetPath = flags.path; flags.source = command === 'asset.import' ? 'Temp/UnityCliSmokeImportSource.txt' : flags.path; flags.to = 'Assets/UnityCliSmoke/SmokeCopy.txt'; flags.destination = flags.to; }
  if (command.startsWith('ui.')) { flags.target = 'UnityCliSmokeRoot'; flags.name = 'UnityCliSmokeUI'; flags.path = command === 'ui.create-panel-settings' ? 'Assets/UnityCliSmoke/PanelSettings.asset' : 'Assets/UnityCliSmoke/Smoke.uxml'; flags.uxml = flags.path; }
  if (command.startsWith('component.')) { flags.target = 'UnityCliSmokeRoot/UnityCliSmokeObject'; flags.path = flags.target; flags.component = command === 'component.add' ? 'SphereCollider' : command === 'component.remove' ? 'BoxCollider' : 'Transform'; flags.componentType = 'Transform'; flags.type = flags.component; flags.property = 'tag'; flags.value = 'Untagged'; flags.__structuredInput = JSON.stringify({ path: flags.target, componentType: 'Transform', maxDepth: 1, pathPatches: [{ path: 'tag', value: 'Untagged' }] }); }
  if (command.startsWith('go.')) { flags['new-name'] = 'UnityCliSmokeRenamed'; flags.destination = 'UnityCliSmokeRoot'; flags['look-at'] = 'UnityCliSmokeCamera'; flags['offset-x'] = '0.1'; flags['offset-y'] = '0'; flags['offset-z'] = '0'; }
  if (command.startsWith('graphics.')) { flags.property = 'vSyncCount'; flags.value = '0'; flags.target = command === 'graphics.skybox-set-sun' ? 'UnityCliSmokeLight' : 'UnityCliSmokeRoot/UnityCliSmokeObject'; flags.material = 'Assets/UnityCliSmoke/Smoke.mat'; flags.create = true; }
  if (command.startsWith('physics.')) { flags.property = 'defaultContactOffset'; flags.value = '0.01'; flags.path = 'Assets/UnityCliSmoke/Smoke.physicMaterial'; flags.target = 'UnityCliSmokeRoot/UnityCliSmokeObject'; flags.type = 'FixedJoint'; flags.x = '0'; flags.y = '1'; flags.z = '0'; flags.mass = '1'; flags['dynamic-friction'] = '0.3'; flags['static-friction'] = '0.5'; }
  if (command.startsWith('profiler.')) { flags.path = 'Assets/UnityCliSmoke/profiler.json'; flags.output = flags.path; flags.format = 'json'; }
  if (command.startsWith('animation.')) { flags.path = 'Assets/UnityCliSmoke/Smoke.anim'; flags.target = 'UnityCliSmokeRoot/UnityCliSmokeObject'; flags.state = 'SmokeState'; }
  if (command.startsWith('animator.')) { flags.target = 'UnityCliSmokeRoot/UnityCliSmokeObject'; flags.name = 'SmokeFloat'; flags['parameter-name'] = 'SmokeFloat'; flags['parameter-type'] = 'float'; flags.value = '0.5'; flags.state = 'SmokeState'; flags.speed = '1'; }
  if (command.startsWith('controller.')) { flags.path = command === 'controller.create' ? 'Assets/UnityCliSmoke/New.controller' : 'Assets/UnityCliSmoke/Smoke.controller'; flags['controller-path'] = flags.path; flags.name = 'SmokeState2'; flags['state-name'] = command.includes('blend-tree') ? 'BlendSmoke' : 'SmokeState2'; flags['from-state'] = 'SmokeState'; flags['to-state'] = 'SmokeState2'; flags['parameter-name'] = 'SmokeFloat2'; flags['parameter-type'] = 'float'; flags['layer-name'] = 'SmokeLayer'; flags['layer-index'] = '1'; flags.weight = '0.5'; flags['blend-parameter'] = 'SmokeFloat'; flags['blend-parameter-x'] = 'SmokeFloat'; flags['blend-parameter-y'] = 'SmokeFloat2'; flags['clip-path'] = 'Assets/UnityCliSmoke/Smoke.anim'; }
  if (command.startsWith('script.')) { flags.path = command === 'script.create' ? 'Assets/UnityCliSmokeCreatedScript.cs' : 'Assets/UnityCliSmokeScriptFixture.cs'; flags.content = 'using UnityEngine; public class UnityCliSmokeScriptFixture : MonoBehaviour { public string text = "updated"; }\n'; flags.find = 'hello'; flags.replace = 'updated'; flags.query = 'UnityCliSmokeScriptFixture'; }
  if (command.startsWith('editor.')) { flags.path = command === 'editor.menu' ? 'File/Save' : flags.path; flags.tool = 'Move'; }
  if (command.startsWith('object.')) { delete flags.path; flags['asset-path'] = 'Assets/UnityCliSmoke/Smoke.asset'; flags.__structuredInput = JSON.stringify({ assetPath: 'Assets/UnityCliSmoke/Smoke.asset', maxDepth: 1, pathPatches: [{ path: 'text', value: 'updated' }] }); }
  if (command.startsWith('reflection.')) { flags.__structuredInput = JSON.stringify({ typeName: 'UnityEngine.Vector3', methodName: command === 'reflection.method.call' ? 'ToString' : undefined, includeInstance: true, includeStatic: true }); flags['type-name'] = 'UnityEngine.Vector3'; flags['method-name'] = command === 'reflection.method.call' ? 'ToString' : 'Normalize'; }
  if (command === 'type.schema') { flags.__structuredInput = JSON.stringify({ typeName: 'UnityEngine.Vector3', maxDepth: 1 }); flags['type-name'] = 'UnityEngine.Vector3'; }
  return flags;
}

function positionalFor(command, flags) {
  if (command === 'prefab.unpack') return 'UnityCliSmokePrefabInstance';
  if (command.startsWith('scene.') || command.startsWith('asset.') || command.startsWith('prefab.') || command.startsWith('scriptable-object.') || command.startsWith('material.') || command.startsWith('shader.') || command.startsWith('texture.') || command.startsWith('animation.clip') || command.startsWith('controller.') || command.startsWith('script.')) return flags.path;
  if (command.startsWith('camera.')) return flags.target;
  if (command === 'ui.create-panel-settings') return flags.path;
  if (command === 'screenshot.game-view' || command === 'screenshot.capture') return flags.path;
  if (command.startsWith('editor.active-tool')) return flags.tool;
  return flags.target;
}

const isolatedCompileCommands = new Set(['script.create', 'script.edit', 'script.apply-edits', 'script.update-or-create']);
const acceptedEnvironmentCommands = new Set(['scene.view-frame', 'screenshot.scene-view', 'editor.menu']);
const tools = new Map(allRegisteredToolMetadata().map(t => [t.name, t]));
const skipped = [];
const normalRequests = [];
const isolatedRequests = [];
let requestCounter = 0;

for (const spec of bridgeCommandRegistry.specs()) {
  const command = spec.bridgeCommand;
  const tool = tools.get(spec.toolName || command) || tools.get(command);
  if (tool?.requiresPackages?.length) { skipped.push({ command, reason: 'requiresPackage', packages: tool.requiresPackages }); continue; }
  const flags = flagsFor(command);
  const positionalTarget = positionalFor(command, flags);
  let args = {};
  try { args = await spec.buildArgs({ command, flags, positionalTarget }); } catch (error) { skipped.push({ command, reason: 'buildArgsFailed', error: String(error) }); continue; }
  if (spec.toolName) args = { name: spec.toolName, input: args };
  if (tool?.destructiveHint || spec.destructiveHint) args.dryRun = true;
  const request = { requestId: `full_smoke_${requestCounter++}`, command: spec.bridgeCommand, args, dryRun: Boolean(args.dryRun) };
  if (isolatedCompileCommands.has(command)) isolatedRequests.push(request);
  else normalRequests.push(request);
}

function cleanupScriptCreateTarget() {
  for (const suffix of ['', '.meta']) rmSync(join(project, `Assets/UnityCliSmokeCreatedScript.cs${suffix}`), { force: true });
}

function runUnityBatch(batchName, requests) {
  cleanupScriptCreateTarget();
  const requestPath = join(outDir, `bridge-requests-${batchName}.jsonl`);
  const resultPath = join(outDir, `bridge-results-${batchName}.jsonl`);
  writeFileSync(requestPath, requests.map(r => JSON.stringify(r)).join('\n') + '\n');
  const unityArgs = ['-batchmode','-nographics','-quit','-projectPath',project,'-logFile','-','-executeMethod','UnityCliFullToolSmoke.Run','-unityCliSmokeRequests',requestPath,'-unityCliSmokeResults',resultPath];
  const run = spawnSync(unity, unityArgs, { encoding: 'utf8', maxBuffer: 50 * 1024 * 1024 });
  writeFileSync(join(outDir, `unity-stdout-${batchName}.log`), run.stdout || '');
  writeFileSync(join(outDir, `unity-stderr-${batchName}.log`), run.stderr || '');
  if (run.status !== 0) {
    throw new Error(JSON.stringify({ phase: 'unity', batchName, status: run.status, requestCount: requests.length }));
  }
  if (!existsSync(resultPath)) {
    throw new Error(JSON.stringify({ phase: 'resultMissing', batchName, requestCount: requests.length }));
  }
  return readFileSync(resultPath, 'utf8').trim().split(/\r?\n/).filter(Boolean).map(line => ({ ...JSON.parse(line), batchName }));
}

const batches = [['main', normalRequests], ...isolatedRequests.map(request => [`isolated-${request.command}`, [request]])];
const rows = [];
try {
  for (const [batchName, batchRequests] of batches) rows.push(...runUnityBatch(batchName, batchRequests));
} catch (error) {
  console.error(JSON.stringify({ ok: false, error: String(error), outDir }, null, 2));
  process.exit(1);
}

writeFileSync(join(outDir, 'bridge-requests.jsonl'), [...normalRequests, ...isolatedRequests].map(r => JSON.stringify(r)).join('\n') + '\n');
writeFileSync(join(outDir, 'bridge-results.jsonl'), rows.map(r => JSON.stringify(r)).join('\n') + '\n');
writeFileSync(join(outDir, 'bridge-skipped.json'), JSON.stringify(skipped, null, 2));

const failed = rows.filter(r => !r.ok);
const acceptedEnvironment = failed.filter(r => acceptedEnvironmentCommands.has(r.command));
const hardFailed = failed.filter(r => !acceptedEnvironmentCommands.has(r.command));
writeFileSync(join(outDir, 'bridge-summary.json'), JSON.stringify({
  total: rows.length,
  failed: hardFailed.length,
  acceptedEnvironment: acceptedEnvironment.length,
  skipped: skipped.length,
  failedCommands: hardFailed.map(f => f.command),
  acceptedEnvironmentCommands: acceptedEnvironment.map(f => f.command)
}, null, 2));
console.log(JSON.stringify({ ok: hardFailed.length === 0, total: rows.length, failed: hardFailed.length, acceptedEnvironment: acceptedEnvironment.length, skipped: skipped.length, outDir }, null, 2));
if (hardFailed.length) process.exit(2);
