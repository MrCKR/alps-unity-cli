const fs = require('fs');
const path = 'contracts/command-contract.json';
const data = JSON.parse(fs.readFileSync(path, 'utf8'));
const existing = new Set(data.commands.map(c => c.bridgeCommand));
const additions = [
  ['script.hash','script','hash','bridge','present',true,false],
  ['script.apply-edits','script','apply-edits','bridge','present',false,true],
  ['editor.undo','editor','undo','bridge','present',false,false],
  ['editor.redo','editor','redo','bridge','present',false,false],
  ['editor.resolution','editor','resolution','bridge','present',false,false],
  ['project.tag','project','tag','bridge','present',false,false],
  ['project.layer','project','layer','bridge','present',false,false],
  ['go.duplicate','go','duplicate','bridge','present',false,false],
  ['go.move-relative','go','move-relative','bridge','present',false,false],
  ['go.look-at','go','look-at','bridge','present',false,false],
  ['scriptable-object.create','scriptable-object','create','bridge','present',false,false],
  ['scriptable-object.info','scriptable-object','info','bridge','present',true,false],
  ['scriptable-object.modify','scriptable-object','modify','bridge','present',false,false],
  ['physics.collision-matrix','physics','collision-matrix','bridge','present',false,false],
  ['physics.material','physics','material','bridge','present',false,false],
  ['physics.joint','physics','joint','bridge','present',false,false],
  ['physics.force','physics','force','bridge','present',false,false],
  ['physics.overlap','physics','overlap','bridge','present',true,false],
  ['physics.rigidbody','physics','rigidbody','bridge','present',false,false],
  ['cli.package.status','cli','package status','offline','none',true,false]
];
for (const [bridgeCommand, group, command, handler_type, csharp_handler, readOnly, destructive] of additions) {
  if (!existing.has(bridgeCommand)) {
    data.commands.push({ bridgeCommand, group, command, handler_type, csharp_handler, readOnly, destructive, timeout: 30000 });
  }
}
data.commands.sort((a,b) => a.bridgeCommand.localeCompare(b.bridgeCommand));
fs.writeFileSync(path, JSON.stringify(data, null, 2) + '\n');
