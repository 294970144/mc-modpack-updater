'use strict';

/**
 * 零依赖 ZIP 读取器
 *
 * 设计要点：
 * 1. 只读中央目录（文件尾部几十 KB）就能拿到全部条目信息，不需要读整个文件。
 *    实测 216MB 的整合包，拿到全部目录只花 0.6ms。
 * 2. 中央目录里自带每个条目的 CRC32 —— 这是天然的文件指纹，
 *    用 (size + crc32) 做差异预筛可以跳过绝大多数解压工作。
 * 3. 两种数据源统一接口：
 *    - fromFile()   基于文件句柄随机读，内存占用极低，适合大整合包
 *    - fromBuffer() 基于内存 Buffer，适合嵌套场景（jar 在 zip 里）
 * 4. 支持 ZIP64、STORED/DEFLATE、UTF-8 与 GBK 文件名。
 */

const fs = require('fs');
const zlib = require('zlib');
const { pipeline } = require('stream/promises');

const SIG_LFH = 0x04034b50;       // Local File Header
const SIG_CDH = 0x02014b50;       // Central Directory Header
const SIG_EOCD = 0x06054b50;      // End of Central Directory
const SIG_EOCD64 = 0x06064b50;    // ZIP64 EOCD
const SIG_EOCD64_LOC = 0x07064b50;// ZIP64 EOCD Locator

const MAX_COMMENT = 0xffff;
const EOCD_MIN = 22;

/** GBK 解码器（Node 内置 full-icu 提供，缺失时优雅降级） */
let gbkDecoder = null;
try {
  gbkDecoder = new TextDecoder('gbk');
} catch (_) {
  gbkDecoder = null;
}

function decodeName(buf, utf8Flag) {
  if (utf8Flag) return buf.toString('utf8');
  // 未标 UTF-8：先按 UTF-8 严格试，失败再用 GBK
  const asUtf8 = buf.toString('utf8');
  if (!asUtf8.includes('\ufffd')) return asUtf8;
  if (gbkDecoder) {
    try { return gbkDecoder.decode(buf); } catch (_) { /* fall through */ }
  }
  return buf.toString('latin1');
}

/** DOS 时间戳 → JS Date */
function dosToDate(dosTime, dosDate) {
  try {
    return new Date(
      ((dosDate >> 9) & 0x7f) + 1980,
      ((dosDate >> 5) & 0x0f) - 1,
      dosDate & 0x1f,
      (dosTime >> 11) & 0x1f,
      (dosTime >> 5) & 0x3f,
      (dosTime & 0x1f) * 2
    );
  } catch (_) {
    return null;
  }
}

class ZipReader {
  constructor(source) {
    this._fd = source.fd ?? null;
    this._buf = source.buf ?? null;
    this._size = source.size;
    this._path = source.path ?? null;
    /** 子区间读基址：用于 STORED 嵌套条目（内层 zip 在外层文件里是连续裸字节） */
    this._base = source.base ?? 0;
    this._entries = null;
    this._byName = null;
  }

  /** 基于文件句柄打开（低内存，推荐用于大文件） */
  static fromFile(filePath) {
    const size = fs.statSync(filePath).size;
    const fd = fs.openSync(filePath, 'r');
    return new ZipReader({ fd, size, path: filePath });
  }

  /** 基于内存 Buffer 打开（用于嵌套 zip / jar） */
  static fromBuffer(buf) {
    return new ZipReader({ buf, size: buf.length });
  }

  /**
   * 基于文件的某个字节区间打开——用于「外层 zip 里以 STORED 方式存放的内层 zip」。
   * 内层是连续裸字节，无需解压即可直接读它的中央目录与条目，零解压、零临时文件。
   */
  static fromFileRange(filePath, start, length) {
    const fd = fs.openSync(filePath, 'r');
    return new ZipReader({ fd, size: length, path: filePath, base: start });
  }

  /** 从底层数据源读取任意区间 */
  _readRange(offset, length) {
    if (length <= 0) return Buffer.alloc(0);
    if (this._buf) return this._buf.subarray(offset, offset + length);
    const out = Buffer.alloc(length);
    fs.readSync(this._fd, out, 0, length, this._base + offset);
    return out;
  }

  /** 解析中央目录（惰性，只做一次） */
  entries() {
    if (this._entries) return this._entries;

    const size = this._size;
    if (size < EOCD_MIN) throw new Error('文件太小，不是有效的 ZIP');

    // --- 1. 反向搜索 EOCD ---
    const tailLen = Math.min(size, MAX_COMMENT + EOCD_MIN);
    const tail = this._readRange(size - tailLen, tailLen);
    let eocd = -1;
    for (let i = tail.length - EOCD_MIN; i >= 0; i--) {
      if (tail.readUInt32LE(i) === SIG_EOCD) { eocd = i; break; }
    }
    if (eocd < 0) throw new Error('未找到 ZIP 结束标记，文件可能已损坏或不是 ZIP');

    let entryCount = tail.readUInt16LE(eocd + 10);
    let cdSize = tail.readUInt32LE(eocd + 12);
    let cdOffset = tail.readUInt32LE(eocd + 16);

    // --- 2. ZIP64 ---
    if (entryCount === 0xffff || cdSize === 0xffffffff || cdOffset === 0xffffffff) {
      const locRel = eocd - 20;
      if (locRel >= 0 && tail.readUInt32LE(locRel) === SIG_EOCD64_LOC) {
        const z64Off = Number(tail.readBigUInt64LE(locRel + 8));
        const z64 = this._readRange(z64Off, 56);
        if (z64.readUInt32LE(0) === SIG_EOCD64) {
          entryCount = Number(z64.readBigUInt64LE(32));
          cdSize = Number(z64.readBigUInt64LE(40));
          cdOffset = Number(z64.readBigUInt64LE(48));
        }
      }
    }

    // --- 3. 遍历中央目录 ---
    const cd = this._readRange(cdOffset, cdSize);
    const list = [];
    let p = 0;
    for (let n = 0; n < entryCount; n++) {
      if (p + 46 > cd.length) break;
      if (cd.readUInt32LE(p) !== SIG_CDH) break;

      const flags = cd.readUInt16LE(p + 8);
      const method = cd.readUInt16LE(p + 10);
      const dosTime = cd.readUInt16LE(p + 12);
      const dosDate = cd.readUInt16LE(p + 14);
      const crc32 = cd.readUInt32LE(p + 16) >>> 0;
      let compSize = cd.readUInt32LE(p + 20);
      let size_ = cd.readUInt32LE(p + 24);
      const nameLen = cd.readUInt16LE(p + 28);
      const extraLen = cd.readUInt16LE(p + 30);
      const commentLen = cd.readUInt16LE(p + 32);
      const externalAttr = cd.readUInt32LE(p + 38);
      let localOffset = cd.readUInt32LE(p + 42);

      const nameBuf = cd.subarray(p + 46, p + 46 + nameLen);
      const name = decodeName(nameBuf, (flags & 0x800) !== 0);

      // ZIP64 extra field
      if (size_ === 0xffffffff || compSize === 0xffffffff || localOffset === 0xffffffff) {
        let e = p + 46 + nameLen;
        const end = e + extraLen;
        while (e + 4 <= end) {
          const hid = cd.readUInt16LE(e);
          const hsz = cd.readUInt16LE(e + 2);
          if (hid === 0x0001) {
            let q = e + 4;
            if (size_ === 0xffffffff) { size_ = Number(cd.readBigUInt64LE(q)); q += 8; }
            if (compSize === 0xffffffff) { compSize = Number(cd.readBigUInt64LE(q)); q += 8; }
            if (localOffset === 0xffffffff) { localOffset = Number(cd.readBigUInt64LE(q)); q += 8; }
            break;
          }
          e += 4 + hsz;
        }
      }

      const isDir = name.endsWith('/') || ((externalAttr & 0x10) !== 0 && size_ === 0);

      list.push({
        name,
        isDir,
        method,
        crc32,
        compSize,
        size: size_,
        localOffset,
        mtime: dosToDate(dosTime, dosDate),
        /** 指纹：大小 + CRC32，用于快速判断文件是否字节级一致 */
        get fingerprint() { return `${this.size}:${this.crc32}`; },
      });

      p += 46 + nameLen + extraLen + commentLen;
    }

    this._entries = list;
    this._byName = new Map(list.map(e => [e.name, e]));
    return list;
  }

  /** 按名字取条目 */
  entry(name) {
    if (!this._byName) this.entries();
    return this._byName.get(name) || null;
  }

  has(name) {
    return this.entry(name) !== null;
  }

  /** 计算压缩数据的真实起始偏移（需读 Local File Header） */
  _dataOffset(ent) {
    const h = this._readRange(ent.localOffset, 30);
    if (h.readUInt32LE(0) !== SIG_LFH) {
      throw new Error(`条目 "${ent.name}" 的本地头损坏`);
    }
    return ent.localOffset + 30 + h.readUInt16LE(26) + h.readUInt16LE(28);
  }

  /** 解压单个条目为 Buffer */
  read(nameOrEntry) {
    const ent = typeof nameOrEntry === 'string' ? this.entry(nameOrEntry) : nameOrEntry;
    if (!ent) throw new Error(`条目不存在: ${nameOrEntry}`);
    if (ent.isDir) return Buffer.alloc(0);

    const off = this._dataOffset(ent);
    const raw = this._readRange(off, ent.compSize);

    if (ent.method === 0) return Buffer.from(raw);
    if (ent.method === 8) return zlib.inflateRawSync(raw);
    throw new Error(`不支持的压缩方式 ${ent.method}（条目 ${ent.name}）`);
  }

  readText(nameOrEntry, encoding = 'utf8') {
    return this.read(nameOrEntry).toString(encoding);
  }

  /**
   * 流式把一个条目写到磁盘 —— 用于大文件，避免整块进内存。
   * 只有基于文件句柄的 ZipReader 支持（Buffer 模式直接同步写更快）。
   */
  async extractToFile(nameOrEntry, destPath) {
    const ent = typeof nameOrEntry === 'string' ? this.entry(nameOrEntry) : nameOrEntry;
    if (!ent) throw new Error(`条目不存在: ${nameOrEntry}`);

    const off = this._dataOffset(ent);

    // Buffer 模式或小文件：直接同步写
    if (this._buf || ent.compSize < 8 * 1024 * 1024) {
      fs.writeFileSync(destPath, this.read(ent));
      return;
    }

    const src = fs.createReadStream(this._path, {
      start: off,
      end: off + ent.compSize - 1,
      highWaterMark: 1 << 20,
    });
    const dst = fs.createWriteStream(destPath);

    if (ent.method === 0) {
      await pipeline(src, dst);
    } else if (ent.method === 8) {
      await pipeline(src, zlib.createInflateRaw(), dst);
    } else {
      src.destroy(); dst.destroy();
      throw new Error(`不支持的压缩方式 ${ent.method}`);
    }
  }

  /** 把整个条目解压成一个新的 ZipReader（用于嵌套 zip，如 jar / mrpack） */
  openNested(nameOrEntry) {
    return ZipReader.fromBuffer(this.read(nameOrEntry));
  }

  close() {
    if (this._fd !== null) {
      try { fs.closeSync(this._fd); } catch (_) { /* ignore */ }
      this._fd = null;
    }
    this._buf = null;
    this._entries = null;
    this._byName = null;
  }
}

/**
 * 流式把 ZipReader 里的某个大条目 inflate 到临时文件，
 * 返回一个针对该临时文件的新 ZipReader（低内存嵌套打开）。
 */
async function openNestedViaTempFile(reader, nameOrEntry, tmpPath) {
  await reader.extractToFile(nameOrEntry, tmpPath);
  return ZipReader.fromFile(tmpPath);
}

module.exports = { ZipReader, openNestedViaTempFile };
