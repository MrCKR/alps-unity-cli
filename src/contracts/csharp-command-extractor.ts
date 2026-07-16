export function extractCSharpRegisteredCommandsFromSource(source: string): string[] {
  const commands = new Set<string>();
  let index = 0;
  while (index < source.length) {
    index = skipCSharpTrivia(source, index);
    if (source[index] === '"' || source[index] === '\'') {
      index = skipCSharpQuotedLiteral(source, index);
      continue;
    }
    if (source.startsWith("RegisterCommand", index) && isIdentifierBoundary(source, index - 1) && isIdentifierBoundary(source, index + "RegisterCommand".length)) {
      const command = readRegisteredCommand(source, index + "RegisterCommand".length, false);
      if (command) commands.add(command.value);
      index = command?.nextIndex ?? index + "RegisterCommand".length;
      continue;
    }
    if (source.startsWith("Register<UnityCli.Protocol.BridgeRequest>", index) && isIdentifierBoundary(source, index - 1)) {
      const command = readRegisteredCommand(source, index + "Register<UnityCli.Protocol.BridgeRequest>".length, true);
      if (command) commands.add(command.value);
      index = command?.nextIndex ?? index + "Register<UnityCli.Protocol.BridgeRequest>".length;
      continue;
    }
    index += 1;
  }
  return [...commands].sort();
}

function readRegisteredCommand(source: string, index: number, generic: boolean): { value: string; nextIndex: number } | undefined {
  index = skipCSharpTrivia(source, index);
  if (source[index] !== "(") return undefined;
  index = skipCSharpTrivia(source, index + 1);
  if (generic) {
    const value = readCSharpString(source, index);
    return value ? { value: value.value, nextIndex: value.nextIndex } : undefined;
  }
  if (!source.startsWith("registry", index) || !isIdentifierBoundary(source, index + "registry".length)) return undefined;
  index = skipCSharpTrivia(source, index + "registry".length);
  if (source[index] !== ",") return undefined;
  const value = readCSharpString(source, skipCSharpTrivia(source, index + 1));
  return value ? { value: value.value, nextIndex: value.nextIndex } : undefined;
}

function readCSharpString(source: string, index: number): { value: string; nextIndex: number } | undefined {
  if (source[index] !== '"') return undefined;
  const end = source.indexOf('"', index + 1);
  return end < 0 ? undefined : { value: source.slice(index + 1, end), nextIndex: end + 1 };
}

function skipCSharpQuotedLiteral(source: string, index: number): number {
  const quote = source[index];
  index += 1;
  while (index < source.length) {
    if (source[index] === "\\") {
      index += 2;
    } else if (source[index] === quote) {
      return index + 1;
    } else {
      index += 1;
    }
  }
  return index;
}

function skipCSharpTrivia(source: string, index: number): number {
  while (index < source.length) {
    if (/\s/.test(source[index])) {
      index += 1;
    } else if (source.startsWith("//", index)) {
      const end = source.indexOf("\n", index + 2);
      index = end < 0 ? source.length : end + 1;
    } else if (source.startsWith("/*", index)) {
      const end = source.indexOf("*/", index + 2);
      index = end < 0 ? source.length : end + 2;
    } else {
      break;
    }
  }
  return index;
}

function isIdentifierBoundary(source: string, index: number): boolean {
  return index < 0 || index >= source.length || !/[A-Za-z0-9_]/.test(source[index]);
}
