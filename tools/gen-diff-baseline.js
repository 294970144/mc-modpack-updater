'use strict';
// logger 会往 stdout 写 ANSI 日志，污染基准 JSON —— 全部赶到 stderr
console.log = (...a) => process.stderr.write(a.join(' ') + String.fromCharCode(10));
/**
 * diff 基准生成器：造一个本地目录 + 一个整合包，跑 Node 原版 diffMods，
 * 把结果打印出来供 C# 对拍。
 *
 * 用法：node tools/gen-diff-baseline.js > tools/diff-baseline.json
 *
 * 【为什么用 JSZip 之外的手段造 jar】
 * mod 元数据解析依赖 jar 内的 fabric.mod.json，而 jar 本身是 zip。
 * 这里手写一个最小 STORED zip（只含一个条目），不依赖外部 zip 命令，
 * 保证在任何机器上都能跑出同样的字节 —— 字节确定，CRC 才确定，
 * 指纹预筛与 unchanged 判定才有稳定的对拍基准。
 */
const fs = require('fs');
const os = require('os');
const path = require('path');
const zlib = require('zlib');
const { diffMods, diffFiles, MetaCache } = require('../src/core/diff-engine');
const { openModpack } = require('../src/core/modpack-reader');

// ---------------------------------------------------------------
// 最小 STORED zip 构造器（只支持 STORED，够用且字节确定）
// ---------------------------------------------------------------
function crc32(buf) {
  let c, crc = 0xFFFFFFFF;
  if (!crc32.table) {
    crc32.table = [];
    for (let n = 0; n < 256; n++) {
      c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1;
      crc32.table[n] = c >>> 0;
    }
  }
  for (let i = 0; i < buf.length; i++) crc = (crc >>> 8) ^ crc32.table[(crc ^ buf[i]) & 0xFF];
  return (crc ^ 0xFFFFFFFF) >>> 0;
}

function makeStoredZip(entries) {
  // entries: [{ name, data:Buffer }]
  const locals = [];
  const centrals = [];
  let offset = 0;

  for (const e of entries) {
    const nameBuf = Buffer.from(e.name, 'utf8');
    const crc = crc32(e.data);
    const lh = Buffer.alloc(30);
    lh.writeUInt32LE(0x04034b50, 0);
    lh.writeUInt16LE(20, 4);          // version needed
    lh.writeUInt16LE(0x0800, 6);      // UTF-8 flag
    lh.writeUInt16LE(0, 8);           // method = STORED
    lh.writeUInt16LE(0, 10);          // time
    lh.writeUInt16LE(0x21, 12);       // date (1996-01-01, 固定值保证字节确定)
    lh.writeUInt32LE(crc, 14);
    lh.writeUInt32LE(e.data.length, 18);
    lh.writeUInt32LE(e.data.length, 22);
    lh.writeUInt16LE(nameBuf.length, 26);
    lh.writeUInt16LE(0, 28);
    locals.push(lh, nameBuf, e.data);

    const ch = Buffer.alloc(46);
    ch.writeUInt32LE(0x02014b50, 0);
    ch.writeUInt16LE(20, 4);          // version made by
    ch.writeUInt16LE(20, 6);          // version needed
    ch.writeUInt16LE(0x0800, 8);
    ch.writeUInt16LE(0, 10);
    ch.writeUInt16LE(0, 12);
    ch.writeUInt16LE(0x21, 14);
    ch.writeUInt32LE(crc, 16);
    ch.writeUInt32LE(e.data.length, 20);
    ch.writeUInt32LE(e.data.length, 24);
    ch.writeUInt16LE(nameBuf.length, 28);
    ch.writeUInt16LE(0, 30);          // extra
    ch.writeUInt16LE(0, 32);          // comment
    ch.writeUInt16LE(0, 34);          // disk
    ch.writeUInt16LE(0, 36);          // internal attrs
    ch.writeUInt32LE(0, 38);          // external attrs
    ch.writeUInt32LE(offset, 42);
    centrals.push(ch, nameBuf);

    offset += 30 + nameBuf.length + e.data.length;
  }

  const cdBuf = Buffer.concat(centrals);
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0);
  eocd.writeUInt16LE(0, 4);
  eocd.writeUInt16LE(0, 6);
  eocd.writeUInt16LE(entries.length, 8);
  eocd.writeUInt16LE(entries.length, 10);
  eocd.writeUInt32LE(cdBuf.length, 12);
  eocd.writeUInt32LE(offset, 16);
  eocd.writeUInt16LE(0, 20);

  return Buffer.concat([...locals, cdBuf, eocd]);
}

/** 造一个只含 fabric.mod.json 的最小 jar */
function makeJar(id, name, version) {
  const meta = Buffer.from(JSON.stringify({ schemaVersion: 1, id, version, name }), 'utf8');
  return makeStoredZip([{ name: 'fabric.mod.json', data: meta }]);
}

/** 造整合包内层的 mods 目录映射 */
function packModsToEntries(mods, contentRoot) {
  const entries = [];
  for (const [fileName, buf] of Object.entries(mods)) {
    entries.push({ name: `${contentRoot}/mods/${fileName}`, data: buf });
  }
  return entries;
}

const out = {};

(async () => {
  // ============================================================
  // 场景 A：新增 + 无变化 + 更新 + 玩家自装
  // ============================================================
  const modsA = {
    'alpha.jar': makeJar('alpha', 'Alpha Mod', '1.0.0'),
    'bravo.jar': makeJar('bravo', 'Bravo Mod', '2.0.0'),
    'delta.jar': makeJar('delta', 'Delta Mod', '1.0.0'),
  };
  const zipA = makeStoredZip([
    { name: 'modrinth.index.json', data: Buffer.from(JSON.stringify({
      name: '基准包', versionId: '1.0.0',
      dependencies: { minecraft: '1.21.11', 'fabric-loader': '0.16.10' },
      files: [],
    }), 'utf8') },
    ...packModsToEntries(modsA, 'overrides'),
  ]);
  const zipAPath = path.join(os.tmpdir(), `mcdiff-a-${process.pid}.zip`);
  fs.writeFileSync(zipAPath, zipA);

  const localA = path.join(os.tmpdir(), `mcdiff-localA-${process.pid}`, 'mods');
  fs.mkdirSync(localA, { recursive: true });
  fs.writeFileSync(path.join(localA, 'alpha.jar'), modsA['alpha.jar']);
  fs.writeFileSync(path.join(localA, 'bravo.jar'), makeJar('bravo', 'Bravo Mod', '1.0.0'));
  fs.writeFileSync(path.join(localA, 'charlie.jar'), makeJar('charlie', 'Charlie Mod', '1.0.0'));

  const fakeManifest = {
    mods: [
      { id: 'alpha', fileName: 'alpha.jar', version: '1.0.0' },
      { id: 'bravo', fileName: 'bravo.jar', version: '1.0.0' },
    ],
    files: {},
  };

  for (const [label, manifest] of [['A_有manifest', fakeManifest], ['A_无manifest', null]]) {
    const handle = await openModpack(zipAPath);
    const r = diffMods(handle.listCategory('mods'), localA, handle, manifest, new MetaCache());
    out[label] = {
      // 顺序本身就是被测行为，完整保留
      order: r.changes.map(c => `${c.type}|${c.key}|${c.fileName}`),
      changes: r.changes.map(c => ({
        type: c.type, key: c.key, modId: c.modId, displayName: c.displayName,
        fileName: c.fileName, oldVersion: c.oldVersion ?? null, newVersion: c.newVersion ?? null,
        checked: c.checked,
        playerInstalled: c.playerInstalled === undefined ? 'undef' : c.playerInstalled,
        reason: c.reason ?? null,
        downgrade: c.downgrade === undefined ? null : c.downgrade,
        renamed: c.renamed === undefined ? null : c.renamed,
        oldFileName: c.oldFileName ?? null,
        duplicate: c.duplicate === undefined ? null : c.duplicate,
        duplicated: c.duplicated === undefined ? null : c.duplicated,
      })),
      conflicts: r.conflicts.map(c => ({ side: c.side, key: c.key, displayName: c.displayName, files: c.files })),
    };
    handle.close();
  }

  // ============================================================
  // 场景 B：本地重复 modId（三份，其中一份是"副本"）
  // ============================================================
  const zipB = makeStoredZip([
    { name: 'modrinth.index.json', data: Buffer.from(JSON.stringify({
      name: '重复包', versionId: '1.0.0',
      dependencies: { minecraft: '1.21.11', 'fabric-loader': '0.16.10' },
      files: [],
    }), 'utf8') },
    ...packModsToEntries({ 'sodium-0.8.13.jar': makeJar('sodium', 'Sodium', '0.8.13') }, 'overrides'),
  ]);
  const zipBPath = path.join(os.tmpdir(), `mcdiff-b-${process.pid}.zip`);
  fs.writeFileSync(zipBPath, zipB);

  const localB = path.join(os.tmpdir(), `mcdiff-localB-${process.pid}`, 'mods');
  fs.mkdirSync(localB, { recursive: true });
  fs.writeFileSync(path.join(localB, 'sodium-0.8.12.jar'), makeJar('sodium', 'Sodium', '0.8.12'));
  fs.writeFileSync(path.join(localB, 'sodium-0.8.13.jar'), makeJar('sodium', 'Sodium', '0.8.13'));
  fs.writeFileSync(path.join(localB, 'zzz-sodium.jar - 副本.jar'), makeJar('sodium', 'Sodium', '0.9.0'));

  {
    const handle = await openModpack(zipBPath);
    const r = diffMods(handle.listCategory('mods'), localB, handle, null, new MetaCache());
    out['B_本地重复'] = {
      order: r.changes.map(c => `${c.type}|${c.key}|${c.fileName}`),
      changes: r.changes.map(c => ({
        type: c.type, key: c.key, fileName: c.fileName,
        oldVersion: c.oldVersion ?? null, newVersion: c.newVersion ?? null,
        checked: c.checked,
        duplicate: c.duplicate === undefined ? null : c.duplicate,
        duplicateOf: c.duplicateOf ?? null,
        duplicateOfVersion: c.duplicateOfVersion ?? null,
        displayName: c.displayName,
      })),
      conflicts: r.conflicts.map(c => ({ side: c.side, key: c.key, displayName: c.displayName, files: c.files })),
    };
    handle.close();
  }

  // ============================================================
  // 场景 C：PCL2 中文前缀导致的"内容一致但改名"
  // ============================================================
  const modsC = { '[钠] sodium-fabric-0.8.13.jar': makeJar('sodium', 'Sodium', '0.8.13') };
  const zipC = makeStoredZip([
    { name: 'modrinth.index.json', data: Buffer.from(JSON.stringify({
      name: '改名包', versionId: '1.0.0',
      dependencies: { minecraft: '1.21.11', 'fabric-loader': '0.16.10' },
      files: [],
    }), 'utf8') },
    ...packModsToEntries(modsC, 'overrides'),
  ]);
  const zipCPath = path.join(os.tmpdir(), `mcdiff-c-${process.pid}.zip`);
  fs.writeFileSync(zipCPath, zipC);

  const localC = path.join(os.tmpdir(), `mcdiff-localC-${process.pid}`, 'mods');
  fs.mkdirSync(localC, { recursive: true });
  // 字节完全相同，只有文件名不同
  fs.writeFileSync(path.join(localC, 'sodium-fabric-0.8.13.jar'), modsC['[钠] sodium-fabric-0.8.13.jar']);

  {
    const handle = await openModpack(zipCPath);
    const r = diffMods(handle.listCategory('mods'), localC, handle, null, new MetaCache());
    out['C_改名但内容一致'] = {
      order: r.changes.map(c => `${c.type}|${c.key}|${c.fileName}`),
      changes: r.changes.map(c => ({
        type: c.type, fileName: c.fileName, oldFileName: c.oldFileName ?? null,
        renamed: c.renamed === undefined ? null : c.renamed,
        oldVersion: c.oldVersion ?? null, newVersion: c.newVersion ?? null,
        checked: c.checked,
      })),
    };
    handle.close();
  }

  // ============================================================
  // 场景 D：降级（同 modId，zip 版本比本地旧）
  // ============================================================
  const zipD = makeStoredZip([
    { name: 'modrinth.index.json', data: Buffer.from(JSON.stringify({
      name: '降级包', versionId: '1.0.0',
      dependencies: { minecraft: '1.21.11', 'fabric-loader': '0.16.10' },
      files: [],
    }), 'utf8') },
    ...packModsToEntries({ 'echo.jar': makeJar('echo', 'Echo Mod', '1.0.0') }, 'overrides'),
  ]);
  const zipDPath = path.join(os.tmpdir(), `mcdiff-d-${process.pid}.zip`);
  fs.writeFileSync(zipDPath, zipD);

  const localD = path.join(os.tmpdir(), `mcdiff-localD-${process.pid}`, 'mods');
  fs.mkdirSync(localD, { recursive: true });
  // 同版本号但内容不同（改了 name）→ 应判 reason=content
  fs.writeFileSync(path.join(localD, 'echo.jar'), makeJar('echo', 'Echo Mod Patched', '1.0.0'));

  {
    const handle = await openModpack(zipDPath);
    const r = diffMods(handle.listCategory('mods'), localD, handle, null, new MetaCache());
    out['D_同版本内容不同'] = {
      changes: r.changes.map(c => ({
        type: c.type, fileName: c.fileName, oldVersion: c.oldVersion ?? null,
        newVersion: c.newVersion ?? null, reason: c.reason ?? null,
        downgrade: c.downgrade ?? null, checked: c.checked,
      })),
    };
    handle.close();
  }

  process.stdout.write(JSON.stringify(out, null, 2));

  // 清理
  for (const p of [zipAPath, zipBPath, zipCPath, zipDPath]) { try { fs.unlinkSync(p); } catch (_) {} }
  for (const d of [localA, localB, localC, localD]) {
    try { fs.rmSync(path.dirname(d), { recursive: true, force: true }); } catch (_) {}
  }
})();