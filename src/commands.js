import { CliError, ErrorCodes } from './errors.js';

export const domains = Object.freeze({
  connect: ['status'],
  query: ['list'],
  edit: ['apply'],
  job: ['run'],
  automation: ['run'],
});

export function resolveCommand(domain, verb) {
  if (!domain) {
    return { kind: 'root-help' };
  }

  if (!Object.hasOwn(domains, domain)) {
    throw new CliError(ErrorCodes.INVALID_ARGS, `Unknown domain: ${domain}`, { exitCode: 2 });
  }

  if (!verb) {
    return { kind: 'domain-help', domain };
  }

  if (!domains[domain].includes(verb)) {
    throw new CliError(ErrorCodes.INVALID_ARGS, `Unknown command: ${domain} ${verb}`, { exitCode: 2 });
  }

  return { kind: 'registered-command', domain, verb };
}

export function commandName(command) {
  if (command.kind === 'registered-command') {
    return `${command.domain} ${command.verb}`;
  }

  if (command.kind === 'domain-help') {
    return `${command.domain} help`;
  }

  return 'help';
}

export function executeCommand(command) {
  if (command.kind === 'registered-command') {
    throw new CliError(ErrorCodes.NOT_IMPLEMENTED, `${command.domain} ${command.verb} is registered but not implemented yet`, {
      exitCode: 1,
      details: { domain: command.domain, verb: command.verb },
    });
  }

  return {
    help: renderHelp(command.domain),
  };
}

export function renderHelp(domain) {
  if (domain) {
    const verbs = domains[domain].map((verb) => `  alps-unity ${domain} ${verb}`).join('\n');
    return `Usage: alps-unity ${domain} <verb> [options]\n\nCommands:\n${verbs}\n\nGlobal options:\n  --project <path>     Unity project path\n  --instance <id>      Unity instance id\n  --format <text|json> Output format\n  --timeout <ms>       Timeout in milliseconds\n  --verbose            Enable diagnostic output\n  -h, --help           Show help`;
  }

  const domainLines = Object.keys(domains).map((name) => `  alps-unity ${name} <verb>`).join('\n');
  return `Usage: alps-unity <domain> <verb> [options]\n\nDomains:\n${domainLines}\n\nGlobal options:\n  --project <path>     Unity project path\n  --instance <id>      Unity instance id\n  --format <text|json> Output format\n  --timeout <ms>       Timeout in milliseconds\n  --verbose            Enable diagnostic output\n  -h, --help           Show help`;
}
