import { createHash } from "node:crypto";
import { open } from "node:fs/promises";

/**
 * 从托管 PE 文件中读取程序集身份（name/version/culture/publicKeyToken）。
 *
 * 安装前冲突检查必须识别“文件名不同但 assembly identity 相同”的受保护依赖，
 * 因此这里只做定向读取：PE 头、节表、CLI 头、元数据根、#~ 表头与 Assembly 表行，
 * 不把整个 DLL 读入内存，也不依赖文件名字猜测身份。
 */

export interface AssemblyIdentity {
  name: string;
  version: string;
  culture: string;
  /** 无强名称时为 null。 */
  publicKeyToken: string | null;
}

/** 表列：固定宽度字节、堆索引或索引目标。 */
type Column =
  | { kind: "fixed"; size: number }
  | { kind: "string" }
  | { kind: "guid" }
  | { kind: "blob" }
  | { kind: "table"; table: number }
  | { kind: "coded"; code: string };

/** 编码索引的位宽与目标表布局（ECMA-335）。 */
const CODED_INDEXES: Record<string, { bits: number; tables: number[] }> = {
  ResolutionScope: { bits: 2, tables: [0x00, 0x1a, 0x23, 0x01] },
  TypeDefOrRef: { bits: 2, tables: [0x02, 0x01, 0x1b] },
  MemberRefParent: { bits: 3, tables: [0x02, 0x01, 0x1a, 0x06, 0x1b] },
  HasConstant: { bits: 2, tables: [0x04, 0x08, 0x17] },
  HasCustomAttribute: {
    bits: 5,
    tables: [0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0a, 0x00, 0x0e, 0x17, 0x14, 0x11, 0x1a, 0x1b, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2a, 0x2c, 0x2b],
  },
  CustomAttributeType: { bits: 3, tables: [0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x0a, 0x00] },
  HasFieldMarshal: { bits: 1, tables: [0x04, 0x08] },
  HasDeclSecurity: { bits: 2, tables: [0x02, 0x06, 0x20] },
  MethodDefOrRef: { bits: 1, tables: [0x06, 0x0a] },
  HasSemantics: { bits: 1, tables: [0x14, 0x17] },
  MemberForwarded: { bits: 1, tables: [0x04, 0x06] },
};

const fixed = (size: number): Column => ({ kind: "fixed", size });
const str: Column = { kind: "string" };
const guid: Column = { kind: "guid" };
const blob: Column = { kind: "blob" };
const tbl = (table: number): Column => ({ kind: "table", table });
const coded = (code: string): Column => ({ kind: "coded", code });

/** Assembly 表(0x20)之前的全部表列定义；用于跳过前置表行并定位 Assembly 行。 */
const TABLES: Record<number, Column[]> = {
  0x00: [fixed(2), str, guid, guid, guid],
  0x01: [coded("ResolutionScope"), str, str],
  0x02: [fixed(4), str, str, coded("TypeDefOrRef"), tbl(0x04), tbl(0x06)],
  0x03: [tbl(0x04)],
  0x04: [fixed(2), str, blob],
  0x05: [tbl(0x06)],
  0x06: [fixed(4), fixed(2), fixed(2), str, blob, tbl(0x08)],
  0x07: [tbl(0x08)],
  0x08: [fixed(2), fixed(2), str],
  0x09: [tbl(0x02), coded("TypeDefOrRef")],
  0x0a: [coded("MemberRefParent"), str, blob],
  0x0b: [fixed(1), fixed(1), coded("HasConstant"), blob],
  0x0c: [coded("HasCustomAttribute"), coded("CustomAttributeType"), blob],
  0x0d: [coded("HasFieldMarshal"), blob],
  0x0e: [fixed(2), coded("HasDeclSecurity"), blob],
  0x0f: [fixed(2), fixed(4), tbl(0x02)],
  0x10: [fixed(4), tbl(0x04)],
  0x11: [blob],
  0x12: [tbl(0x02), tbl(0x14)],
  0x13: [tbl(0x14)],
  0x14: [fixed(2), str, coded("TypeDefOrRef")],
  0x15: [tbl(0x02), tbl(0x17)],
  0x16: [tbl(0x17)],
  0x17: [fixed(2), str, blob],
  0x18: [fixed(2), tbl(0x06), coded("HasSemantics")],
  0x19: [tbl(0x02), coded("MethodDefOrRef"), coded("MethodDefOrRef")],
  0x1a: [str],
  0x1b: [blob],
  0x1c: [fixed(2), coded("MemberForwarded"), str, tbl(0x1a)],
  0x1d: [fixed(4), tbl(0x04)],
  0x20: [fixed(4), fixed(2), fixed(2), fixed(2), fixed(2), fixed(4), blob, str, str],
};

/**
 * 读取托管程序集身份；非托管文件、读取越界或结构不完整时返回 null，
 * 由调用方决定按“无法证明身份”处理，不做猜测。
 */
export async function readAssemblyIdentity(path: string): Promise<AssemblyIdentity | null> {
  let handle: Awaited<ReturnType<typeof open>>;
  try {
    handle = await open(path, "r");
  } catch {
    return null;
  }
  try {
    const stat = await handle.stat();
    const size = stat.size;
    if (size < 0x40) return null;

    const readAt = async (position: number, length: number): Promise<Buffer | null> => {
      if (position < 0 || length <= 0 || position + length > size) return null;
      const buffer = Buffer.alloc(length);
      const { bytesRead } = await handle.read(buffer, 0, length, position);
      return bytesRead === length ? buffer : null;
    };

    const dosHeader = await readAt(0, 0x40);
    if (!dosHeader || dosHeader.readUInt16LE(0) !== 0x5a4d) return null;
    const peOffset = dosHeader.readUInt32LE(0x3c);
    const coffHeader = await readAt(peOffset, 24);
    if (!coffHeader || coffHeader.readUInt32LE(0) !== 0x00004550) return null;
    const numberOfSections = coffHeader.readUInt16LE(6);
    const sizeOfOptionalHeader = coffHeader.readUInt16LE(20);
    if (numberOfSections <= 0 || numberOfSections > 96 || sizeOfOptionalHeader < 96) return null;

    const optionalHeader = await readAt(peOffset + 24, sizeOfOptionalHeader);
    if (!optionalHeader) return null;
    const magic = optionalHeader.readUInt16LE(0);
    const dataDirectoryOffset = magic === 0x10b ? 96 : magic === 0x20b ? 112 : -1;
    if (dataDirectoryOffset < 0 || dataDirectoryOffset + 16 * 8 > optionalHeader.length) return null;
    const numberOfRvaAndSizes = optionalHeader.readUInt32LE(dataDirectoryOffset - 4);
    if (numberOfRvaAndSizes <= 14) return null;
    const cliRva = optionalHeader.readUInt32LE(dataDirectoryOffset + 14 * 8);
    if (cliRva === 0) return null;

    const sectionHeaders = await readAt(peOffset + 24 + sizeOfOptionalHeader, numberOfSections * 40);
    if (!sectionHeaders) return null;
    const sections: Array<{ virtualSize: number; virtualAddress: number; rawPointer: number; rawSize: number }> = [];
    for (let index = 0; index < numberOfSections; index += 1) {
      const base = index * 40;
      sections.push({
        virtualSize: sectionHeaders.readUInt32LE(base + 8),
        virtualAddress: sectionHeaders.readUInt32LE(base + 12),
        rawSize: sectionHeaders.readUInt32LE(base + 16),
        rawPointer: sectionHeaders.readUInt32LE(base + 20),
      });
    }
    const rvaToOffset = (rva: number): number => {
      for (const section of sections) {
        const span = Math.max(section.virtualSize, section.rawSize);
        if (rva >= section.virtualAddress && rva < section.virtualAddress + span) {
          return section.rawPointer + (rva - section.virtualAddress);
        }
      }
      return -1;
    };

    const cliOffset = rvaToOffset(cliRva);
    if (cliOffset < 0) return null;
    const cliHeader = await readAt(cliOffset, 16);
    if (!cliHeader) return null;
    const metadataRva = cliHeader.readUInt32LE(8);
    const metadataSize = cliHeader.readUInt32LE(12);
    if (metadataRva === 0 || metadataSize < 32) return null;
    const metadataOffset = rvaToOffset(metadataRva);
    if (metadataOffset < 0) return null;

    const rootHeader = await readAt(metadataOffset, 20);
    if (!rootHeader || rootHeader.readUInt32LE(0) !== 0x424a5342) return null;
    const versionLength = rootHeader.readUInt32LE(12);
    if (versionLength <= 0 || versionLength > 1024) return null;
    const streamsOffset = metadataOffset + 16 + align4(versionLength) + 4;
    const streamCountHeader = await readAt(metadataOffset + 16 + align4(versionLength), 4);
    if (!streamCountHeader) return null;
    const streamCount = streamCountHeader.readUInt16LE(2);
    if (streamCount <= 0 || streamCount > 32) return null;

    const streams = new Map<string, { offset: number; size: number }>();
    let cursor = streamsOffset;
    for (let index = 0; index < streamCount; index += 1) {
      const header = await readAt(cursor, 8);
      if (!header) return null;
      const offset = header.readUInt32LE(0);
      const streamSize = header.readUInt32LE(4);
      const nameBuffer = await readAt(cursor + 8, 64);
      if (!nameBuffer) return null;
      const nameEnd = nameBuffer.indexOf(0);
      if (nameEnd < 0) return null;
      streams.set(nameBuffer.subarray(0, nameEnd).toString("ascii"), { offset, size: streamSize });
      cursor += 8 + align4(nameEnd + 1);
    }

    const tablesStream = streams.get("#~") ?? streams.get("#-");
    const stringsStream = streams.get("#Strings");
    const blobStream = streams.get("#Blob");
    if (!tablesStream || !stringsStream) return null;

    const tablesHeader = await readAt(metadataOffset + tablesStream.offset, 24);
    if (!tablesHeader) return null;
    const heapSizes = tablesHeader.readUInt8(6);
    const valid = tablesHeader.readBigUInt64LE(8);
    const stringIndexSize = (heapSizes & 0x01) !== 0 ? 4 : 2;
    const guidIndexSize = (heapSizes & 0x02) !== 0 ? 4 : 2;
    const blobIndexSize = (heapSizes & 0x04) !== 0 ? 4 : 2;

    const presentTables: number[] = [];
    for (let table = 0; table < 64; table += 1) {
      if ((valid & (1n << BigInt(table))) !== 0n) presentTables.push(table);
    }
    const rowCountsHeader = await readAt(metadataOffset + tablesStream.offset + 24, presentTables.length * 4);
    if (!rowCountsHeader) return null;
    const rowCounts = new Map<number, number>();
    for (let index = 0; index < presentTables.length; index += 1) {
      rowCounts.set(presentTables[index], rowCountsHeader.readUInt32LE(index * 4));
    }
    if (!rowCounts.has(0x20) || (rowCounts.get(0x20) ?? 0) < 1) return null;

    const rowSize = (table: number): number => {
      const columns = TABLES[table];
      if (!columns) return -1;
      let size = 0;
      for (const column of columns) {
        if (column.kind === "fixed") size += column.size;
        else if (column.kind === "string") size += stringIndexSize;
        else if (column.kind === "guid") size += guidIndexSize;
        else if (column.kind === "blob") size += blobIndexSize;
        else if (column.kind === "table") size += (rowCounts.get(column.table) ?? 0) > 0xffff ? 4 : 2;
        else {
          const coded = CODED_INDEXES[column.code];
          if (!coded) return -1;
          const wide = coded.tables.some((target) => (rowCounts.get(target) ?? 0) > (1 << (16 - coded.bits)) - 1);
          size += wide ? 4 : 2;
        }
      }
      return size;
    };

    const tablesDataOffset = metadataOffset + tablesStream.offset + 24 + presentTables.length * 4;
    let assemblyRowOffset = -1;
    let offset = tablesDataOffset;
    for (const table of presentTables) {
      if (table > 0x20) break;
      const size = rowSize(table);
      if (size < 0) return null;
      if (table === 0x20) { assemblyRowOffset = offset; break; }
      offset += size * (rowCounts.get(table) ?? 0);
    }
    if (assemblyRowOffset < 0) return null;

    const assemblyHeader = await readAt(assemblyRowOffset, 16 + blobIndexSize + stringIndexSize * 2);
    if (!assemblyHeader) return null;
    const version = `${assemblyHeader.readUInt16LE(4)}.${assemblyHeader.readUInt16LE(6)}.${assemblyHeader.readUInt16LE(8)}.${assemblyHeader.readUInt16LE(10)}`;
    const publicKeyIndex = readHeapIndex(assemblyHeader, 16, blobIndexSize);
    const nameIndex = readHeapIndex(assemblyHeader, 16 + blobIndexSize, stringIndexSize);
    const cultureIndex = readHeapIndex(assemblyHeader, 16 + blobIndexSize + stringIndexSize, stringIndexSize);

    const name = await readHeapString(handle, metadataOffset + stringsStream.offset, nameIndex, size);
    if (name === null || name.length === 0) return null;
    const culture = cultureIndex === 0 ? "" : (await readHeapString(handle, metadataOffset + stringsStream.offset, cultureIndex, size)) ?? "";
    const publicKeyToken = await readPublicKeyToken(handle, blobStream ? metadataOffset + blobStream.offset : -1, publicKeyIndex, size, blobStream ? blobStream.size : 0);
    return { name, version, culture, publicKeyToken };
  } catch {
    return null;
  } finally {
    await handle.close();
  }
}

function align4(value: number): number {
  return value % 4 === 0 ? value : value + (4 - (value % 4));
}

function readHeapIndex(buffer: Buffer, offset: number, width: number): number {
  return width === 4 ? buffer.readUInt32LE(offset) : buffer.readUInt16LE(offset);
}

/** 读取 #Strings 堆中以 NUL 结尾的 UTF-8 字符串。 */
async function readHeapString(handle: Awaited<ReturnType<typeof open>>, heapOffset: number, index: number, fileSize: number): Promise<string | null> {
  if (index <= 0) return null;
  const position = heapOffset + index;
  if (position < 0 || position >= fileSize) return null;
  const buffer = Buffer.alloc(Math.min(512, fileSize - position));
  const { bytesRead } = await handle.read(buffer, 0, buffer.length, position);
  const end = buffer.subarray(0, bytesRead).indexOf(0);
  if (end < 0) return null;
  return buffer.subarray(0, end).toString("utf8");
}

/** 读取 #Blob 堆中的公钥并计算 ECMA 公钥令牌；空公钥表示无强名称。 */
async function readPublicKeyToken(
  handle: Awaited<ReturnType<typeof open>>,
  blobHeapOffset: number,
  index: number,
  fileSize: number,
  heapSize: number,
): Promise<string | null> {
  if (index <= 0 || blobHeapOffset < 0) return null;
  const position = blobHeapOffset + index;
  if (position < 0 || position >= fileSize) return null;
  const lengthBuffer = Buffer.alloc(4);
  const { bytesRead } = await handle.read(lengthBuffer, 0, 4, position);
  if (bytesRead < 1) return null;
  let length = lengthBuffer.readUInt8(0);
  let headerSize = 1;
  if ((length & 0x80) === 0) {
    length = length & 0x7f;
  } else if ((length & 0xc0) === 0x80) {
    if (bytesRead < 2) return null;
    length = ((length & 0x3f) << 8) | lengthBuffer.readUInt8(1);
    headerSize = 2;
  } else {
    if (bytesRead < 4) return null;
    if ((length & 0xe0) !== 0xc0) return null;
    length = ((length & 0x1f) << 24) | (lengthBuffer.readUInt8(1) << 16) | (lengthBuffer.readUInt8(2) << 8) | lengthBuffer.readUInt8(3);
    headerSize = 4;
  }
  if (length <= 0) return null;
  if (heapSize > 0 && headerSize + length > heapSize) return null;
  const data = Buffer.alloc(length);
  const read = await handle.read(data, 0, length, position + headerSize);
  if (read.bytesRead !== length) return null;
  const digest = createHash("sha1").update(data).digest();
  const token = digest.subarray(digest.length - 8).reverse();
  return token.toString("hex");
}
