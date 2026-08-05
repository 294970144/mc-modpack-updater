'use strict';

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

/** Node 22 内置 zlib.crc32，老版本回落到手写表 */
let crcTable = null;
function crc32Fallback(buf) {
  if (!crcTable) {
    crcTable = new Int32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      crcTable[n] = c;
    }
  }
  let crc = -1;
  for (let i = 0; i < buf.length; i++) crc = (crc >>> 8) ^ crcTable[(crc ^ buf[i]) & 0xff];
  return (crc ^ -1) >>> 0;
}
const crc32 = typeof zlib.crc32 === 'function'
  ? (buf) => zlib.crc32(buf) >>> 0
  : crc32Fallback;

/** 计算文件的 (size, crc32) 指纹 */
function fileFingerprint(filePath) {
  const st = fs.statSync(filePath);
  const buf = fs.readFileSync(filePath);
  return { size: st.size, crc32: crc32(buf), fingerprint: `${st.size}:${crc32(buf)}`, mtimeMs: st.mtimeMs, buffer: buf };
}

/** 只取 size，不读内容（用于快速预筛） */
function fileSize(filePath) {
  try { return fs.statSync(filePath).size; } catch (_) { return -1; }
}

function ensureDir(dir) {
  if (!fs.existsSync(dir)) fs.mkdirSync(dir, { recursive: true });
}

/** 递归列出目录下所有文件（返回相对路径，统一用 / 分隔） */
function listFilesRecursive(root, relBase = '') {
  const out = [];
  let items;
  try {
    items = fs.readdirSync(path.join(root, relBase), { withFileTypes: true });
  } catch (_) {
    return out;
  }
  for (const it of items) {
    const rel = relBase ? `${relBase}/${it.name}` : it.name;
    if (it.isDirectory()) {
      out.push(...listFilesRecursive(root, rel));
    } else if (it.isFile()) {
      out.push(rel);
    }
  }
  return out;
}

/** 递归复制目录 */
function copyDirSync(src, dest, onFile) {
  ensureDir(dest);
  for (const it of fs.readdirSync(src, { withFileTypes: true })) {
    const s = path.join(src, it.name);
    const d = path.join(dest, it.name);
    if (it.isDirectory()) copyDirSync(s, d, onFile);
    else if (it.isFile()) {
      fs.copyFileSync(s, d);
      if (onFile) onFile(d);
    }
  }
}

function rmrf(target) {
  try { fs.rmSync(target, { recursive: true, force: true }); } catch (_) { /* ignore */ }
}

/** 目录大小与文件数 */
function dirStats(dir) {
  let files = 0, bytes = 0;
  const walk = (d) => {
    let items;
    try { items = fs.readdirSync(d, { withFileTypes: true }); } catch (_) { return; }
    for (const it of items) {
      const p = path.join(d, it.name);
      if (it.isDirectory()) walk(p);
      else if (it.isFile()) {
        files++;
        try { bytes += fs.statSync(p).size; } catch (_) { /* ignore */ }
      }
    }
  };
  if (fs.existsSync(dir)) walk(dir);
  return { files, bytes };
}

/** 拒绝路径穿越 */
function isSafeChild(parent, child) {
  const rel = path.relative(path.resolve(parent), path.resolve(child));
  return rel !== '' && !rel.startsWith('..') && !path.isAbsolute(rel);
}

function hasTraversal(p) {
  return typeof p !== 'string' || p.includes('..');
}

/**
 * 极简 glob → 正则。只支持 `**`、`*`、`?`，够用了，不引三方库。
 * 路径统一用 `/` 分隔、统一转小写后再匹配。
 */
function globToRegExp(pattern) {
  let re = '';
  for (let i = 0; i < pattern.length; i++) {
    const c = pattern[i];
    if (c === '*') {
      if (pattern[i + 1] === '*') {
        // `**/` 要能匹配零层目录，所以把后面的斜杠一起吃掉
        i++;
        if (pattern[i + 1] === '/') { i++; re += '(?:.*/)?'; }
        else re += '.*';
      } else {
        re += '[^/]*';
      }
    } else if (c === '?') {
      re += '[^/]';
    } else {
      re += c.replace(/[.+^${}()|[\]\\]/g, '\\$&');
    }
  }
  return new RegExp('^' + re + '$', 'i');
}

/**
 * 把一组 glob 编译成一个判断函数
 * @param {string[]} patterns
 * @returns {(relPath:string)=>boolean}
 */
function makeMatcher(patterns) {
  if (!patterns || patterns.length === 0) return () => false;
  const res = patterns.map(globToRegExp);
  return (p) => {
    const s = String(p).replace(/\\/g, '/');
    return res.some(r => r.test(s));
  };
}

/** 时间戳目录名：20260805-001530 */
function timestampName(d = new Date()) {
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
}

function formatBytes(n) {
  if (n < 1024) return `${n} B`;
  if (n < 1048576) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1073741824) return `${(n / 1048576).toFixed(1)} MB`;
  return `${(n / 1073741824).toFixed(2)} GB`;
}

module.exports = {
  crc32, fileFingerprint, fileSize, ensureDir, listFilesRecursive,
  copyDirSync, rmrf, dirStats, isSafeChild, hasTraversal,
  timestampName, formatBytes, globToRegExp, makeMatcher,
};
