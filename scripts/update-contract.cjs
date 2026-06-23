const fs = require('fs');
const c = JSON.parse(fs.readFileSync('contracts/command-contract.json', 'utf8'));
const before = c.commands.length;
c.commands = c.commands.filter(e => !e.bridgeCommand.startsWith('cli.'));
console.log('removed cli.*:', before - c.commands.length, 'remaining:', c.commands.length);
const newEntries = [
  {bridgeCommand:'package.list',group:'package',command:'list',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'package.status',group:'package',command:'status',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'package.info',group:'package',command:'info',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'package.search',group:'package',command:'search',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'package.install',group:'package',command:'install',handler_type:'hybrid',csharp_handler:'none',readOnly:false,destructive:false,timeout:120000},
  {bridgeCommand:'package.remove',group:'package',command:'remove',handler_type:'hybrid',csharp_handler:'none',readOnly:false,destructive:true,timeout:120000},
  {bridgeCommand:'package.resolve',group:'package',command:'resolve',handler_type:'hybrid',csharp_handler:'none',readOnly:false,destructive:false,timeout:300000},
  {bridgeCommand:'package.registry',group:'package',command:'registry',handler_type:'offline',csharp_handler:'none',readOnly:false,destructive:false,timeout:30000},
  {bridgeCommand:'package.scoped-registry',group:'package',command:'scoped-registry',handler_type:'offline',csharp_handler:'none',readOnly:false,destructive:false,timeout:30000},
  {bridgeCommand:'batch.schema',group:'batch',command:'schema',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'batch.run',group:'batch',command:'run',handler_type:'offline',csharp_handler:'none',readOnly:false,destructive:true,timeout:300000},
  {bridgeCommand:'docs.list',group:'docs',command:'list',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'docs.topic',group:'docs',command:'topic',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'docs.get',group:'docs',command:'get',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'docs.search',group:'docs',command:'search',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'reflect.type',group:'reflect',command:'type',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'reflect.member',group:'reflect',command:'member',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'reflect.search',group:'reflect',command:'search',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'job.status',group:'job',command:'status',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'job.logs',group:'job',command:'logs',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'job.describe',group:'job',command:'describe',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'job.cancel',group:'job',command:'cancel',handler_type:'offline',csharp_handler:'none',readOnly:false,destructive:true,timeout:30000},
  {bridgeCommand:'instances.prune',group:'instances',command:'prune',handler_type:'offline',csharp_handler:'none',readOnly:false,destructive:true,timeout:30000},
  {bridgeCommand:'target.set',group:'target',command:'set',handler_type:'offline',csharp_handler:'none',readOnly:false,destructive:true,timeout:30000},
  {bridgeCommand:'target.clear',group:'target',command:'clear',handler_type:'offline',csharp_handler:'none',readOnly:false,destructive:true,timeout:30000},
  {bridgeCommand:'target.alias-set',group:'target',command:'alias-set',handler_type:'offline',csharp_handler:'none',readOnly:false,destructive:false,timeout:30000},
  {bridgeCommand:'target.alias-unset',group:'target',command:'alias-unset',handler_type:'offline',csharp_handler:'none',readOnly:false,destructive:false,timeout:30000},
  {bridgeCommand:'target.alias-list',group:'target',command:'alias-list',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000},
  {bridgeCommand:'diagnose.doctor',group:'diagnose',command:'doctor',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:60000},
  {bridgeCommand:'diagnose.capabilities',group:'diagnose',command:'capabilities',handler_type:'offline',csharp_handler:'none',readOnly:true,destructive:false,timeout:30000}
];
for (const e of newEntries) {
  const idx = c.commands.findIndex(x => x.bridgeCommand === e.bridgeCommand);
  if (idx >= 0) c.commands[idx] = e; else c.commands.push(e);
}
c.commands.sort((a, b) => a.bridgeCommand.localeCompare(b.bridgeCommand));
fs.writeFileSync('contracts/command-contract.json', JSON.stringify(c, null, 2) + '\n');
const verify = JSON.parse(fs.readFileSync('contracts/command-contract.json', 'utf8'));
console.log('written total:', verify.commands.length, 'has package.list:', verify.commands.some(x => x.bridgeCommand === 'package.list'));
