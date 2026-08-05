'use strict';

/**
 * 整合包读取器
 *
 * 已知真实样本（PCL2「导出整合包」的标准产物）：
 *   1.21.11 1.0.3.zip
 *     ├── modpack.mrpack            ← 真身，Modrinth 整合包格式
 *     │     ├── modrinth.index.json ← 游戏版本 + 加载器版本直接白给
 *     │     └── overrides/          ← 内容根，1:1 对应 versions/<版本>/
 *     │           ├── mods/  config/  resourcepacks/  shaderpacks/
 *     │           └── PCL/Setup.ini ← 版本名、隔离状态
 *     └── Plain Craft Launcher.exe  ← 启动器本体，忽略
 *
 * 识别到这个格式就走快速路径：overrides/ 直接就是内容根，零歧义。
 * 认不出来再回落到通用内容根探测。
 */

const fs = require('fs');
const os = require('os');
const path = require('path');
const crypto = require('crypto');
const config = require('../config');
const logger = require('../utils/logger');
const { ZipReader, openNestedViaTempFile } = require('./zip-reader');
const { looseJsonParse } = require('./mod-metadata');

const FORMAT = {
  PCL2_MRPACK: 'pcl2-mrpack',
  MRPACK: 'mrpack',
  GENERIC: 'generic',
};

/** 解析 PCL2 的 Setup.ini（Key:Value 格式） */
function parseSetupIni(text) {
  const out = {};
  for (const line of text.replace(/\r\n/g, '\n').split('\n')) {
    const t = line.trim();
    if (!t || t.startsWith(';') || t.startsWith('#')) continue;
    const i = t.indexOf(':');
    if (i === -1) continue;
    out[t.slice(0, i).trim()] = t.slice(i + 1).trim();
  }
  return out;
}

/**
 * 通用内容根探测：找出哪个目录下面挂着 mods/config/resourcepacks/shaderpacks
 * 返回按可信度排序的候选列表
 */
function detectContentRoots(entries) {
  const managedDirs = new Set(config.MANAGED_CATEGORIES.map(c => c.dir));
  /** parentPath -> Set(命中的分类名) */
  const hits = new Map();

  for (const e of entries) {
    const parts = e.name.split('/').filter(Boolean);
    if (parts.length < 2) continue;
    for (let i = 0; i < parts.length - 1; i++) {
      if (!managedDirs.has(parts[i])) continue;
      const parent = parts.slice(0, i).join('/');
      if (!hits.has(parent)) hits.set(parent, new Set());
      hits.get(parent).add(parts[i]);
      break;
    }
  }

  const scored = [];
  for (const [root, cats] of hits) {
    let score = cats.size * 10;
    if (cats.has('mods')) score += 50;               // 有 mods 才是真内容根
    if (root === 'overrides') score += 40;
    if (root === '') score += 30;                    // 直接在 zip 根
    if (root === '.minecraft') score += 25;
    if (/^versions\/[^/]+$/.test(root)) score += 20;
    if (/^\.minecraft\/versions\/[^/]+$/.test(root)) score += 20;
    score -= root.split('/').length;                 // 越浅越可信
    scored.push({ root, categories: [...cats], score });
  }
  scored.sort((a, b) => b.score - a.score);
  return scored;
}

/** 从 modrinth.index.json 的 dependencies 提取加载器 */
function loaderFromDependencies(deps) {
  if (!deps || typeof deps !== 'object') return { loader: null, loaderVersion: null };
  const map = {
    'fabric-loader': 'Fabric',
    'quilt-loader': 'Quilt',
    'forge': 'Forge',
    'neoforge': 'NeoForge',
  };
  for (const [k, label] of Object.entries(map)) {
    if (deps[k]) return { loader: label, loaderVersion: String(deps[k]) };
  }
  return { loader: null, loaderVersion: null };
}

class ModpackHandle {
  constructor(opts) {
    Object.assign(this, opts);
  }

  /** 列出某个分类下的全部文件条目（相对内容根的路径） */
  listCategory(categoryKey) {
    const cat = config.MANAGED_CATEGORIES.find(c => c.key === categoryKey);
    if (!cat) return [];
    const prefix = this.contentRoot ? `${this.contentRoot}/${cat.dir}/` : `${cat.dir}/`;
    return this.reader.entries()
      .filter(e => !e.isDir && e.name.startsWith(prefix) && e.size > 0)
      .map(e => ({
        entry: e,
        /** 相对分类目录的路径，如 "sodium.jar" 或 "yosbr/options.txt" */
        relPath: e.name.slice(prefix.length),
        size: e.size,
        crc32: e.crc32,
        fingerprint: `${e.size}:${e.crc32}`,
      }));
  }

  read(entry) {
    return this.reader.read(entry);
  }

  async extractTo(entry, destPath) {
    return this.reader.extractToFile(entry, destPath);
  }

  close() {
    try { this.reader.close(); } catch (_) { /* ignore */ }
    if (this.outerReader && this.outerReader !== this.reader) {
      try { this.outerReader.close(); } catch (_) { /* ignore */ }
    }
    if (this.tmpFile && !this.tmpFilePersistent) {
      // 非缓存的一次性临时文件才删；命中缓存的留着复用
      try { fs.unlinkSync(this.tmpFile); } catch (_) { /* ignore */ }
      this.tmpFile = null;
    }
  }
}

/** 缓存目录：展开过的内层 mrpack 落盘缓存，重复打开同一整合包秒回 */
function modpackCacheDir() {
  const dir = path.join(os.tmpdir(), 'mcupdater-modpack-cache');
  try { fs.mkdirSync(dir, { recursive: true }); } catch (_) { /* ignore */ }
  return dir;
}

/** LRU 清理缓存，只保留最近 N 份 */
function pruneCache(dir, keep = 4) {
  let files;
  try {
    files = fs.readdirSync(dir)
      .map(f => { const p = path.join(dir, f); let st; try { st = fs.statSync(p); } catch (_) { return null; } return { p, mtime: st.mtimeMs }; })
      .filter(Boolean);
  } catch (_) { return; }
  files.sort((a, b) => b.mtime - a.mtime);
  for (const f of files.slice(keep)) { try { fs.unlinkSync(f.p); } catch (_) { /* ignore */ } }
}

/**
 * 把（压缩的）内层 mrpack 展开到缓存临时文件。
 * 同一整合包（按 来源路径 + 条目指纹）重复打开时命中缓存秒回，
 * 避免每次都全量解压 + 被 Defender 重新扫描。
 * 返回 { reader, tmpFile, cached }。cached=true 时 tmpFile 是持久缓存，close 不删。
 */
async function openNestedCached(outer, ent, sourcePath) {
  const key = crypto.createHash('sha1')
    .update(`${sourcePath}|${ent.size}|${ent.crc32}|${ent.name}`)
    .digest('hex').slice(0, 16);
  const dir = modpackCacheDir();
  const tmpFile = path.join(dir, `${key}.mrpack`);

  let cached = false;
  if (fs.existsSync(tmpFile)) {
    try {
      const st = fs.statSync(tmpFile);
      if (st.size === ent.size) {
        const probe = ZipReader.fromFile(tmpFile);
        probe.entries(); probe.close();
        cached = true;
        try { fs.utimesSync(tmpFile, new Date(), new Date()); } catch (_) { /* LRU 更新 mtime */ }
      }
    } catch (_) { /* 缓存损坏，下面覆盖 */ }
  }

  if (!cached) {
    // 先写 .part 再 rename，防止中断留下半截缓存
    const part = tmpFile + '.part';
    await outer.extractToFile(ent, part);
    fs.renameSync(part, tmpFile);
    pruneCache(dir, 4);
  }

  return { reader: ZipReader.fromFile(tmpFile), tmpFile, cached };
}

/**
 * 打开整合包
 * @param {string} zipPath
 * @param {(msg:string, pct?:number)=>void} onProgress
 * @returns {Promise<ModpackHandle>}
 */
async function openModpack(zipPath, onProgress = () => {}) {
  if (!fs.existsSync(zipPath)) throw new Error(`整合包文件不存在: ${zipPath}`);

  onProgress('正在读取整合包目录…', 5);
  const outer = ZipReader.fromFile(zipPath);
  const outerEntries = outer.entries();
  logger.info(`外层 zip 条目数: ${outerEntries.length}`);

  let reader = outer;
  let outerReader = outer;
  let tmpFile = null;
  let tmpFilePersistent = false;   // 命中缓存时 true，close() 不删，留给下次复用
  let format = FORMAT.GENERIC;

  // ---- 快速路径：外层套了一个 .mrpack ----
  const mrEnt = outerEntries.find(e => !e.isDir && e.name.toLowerCase().endsWith('.mrpack'));
  if (mrEnt) {
    format = FORMAT.PCL2_MRPACK;
    const sizeMb = (mrEnt.size / 1048576).toFixed(1);

    if (mrEnt.method === 0) {
      // STORED：内层 mrpack 在外层文件里是连续裸字节，子区间直读，
      // 零解压、零临时文件、零 Defender 扫描——秒开
      onProgress(`发现 PCL2 整合包（内层 ${sizeMb} MB，直读）…`, 15);
      const dataOff = outer._dataOffset(mrEnt);
      reader = ZipReader.fromFileRange(zipPath, dataOff, mrEnt.size);
      logger.info(`内层包 ${sizeMb}MB 为 STORED，子区间直读（零解压）`);
    } else if (mrEnt.size >= config.INMEMORY_THRESHOLD) {
      // 大包 + 压缩：展开到缓存临时文件，同一整合包重复打开秒回
      onProgress(`发现 PCL2 整合包（内层 ${sizeMb} MB），正在展开…`, 15);
      const r = await openNestedCached(outer, mrEnt, zipPath);
      reader = r.reader;
      tmpFile = r.tmpFile;
      tmpFilePersistent = r.cached;
      logger.info(`内层包 ${sizeMb}MB，${r.cached ? '命中缓存复用' : '展开到'}临时文件: ${tmpFile}`);
    } else {
      onProgress(`发现 PCL2 整合包（内层 ${sizeMb} MB），正在读取…`, 15);
      logger.info(`内层包 ${sizeMb}MB，直接读入内存`);
      reader = outer.openNested(mrEnt);
    }
    reader.entries();
  } else if (outer.has(config.MRPACK_INDEX)) {
    // 用户直接拿到了 .mrpack 本体（后缀被改成 zip）
    format = FORMAT.MRPACK;
  }

  // ---- 读取整合包元信息 ----
  onProgress('正在识别游戏版本与加载器…', 25);
  let packName = path.basename(zipPath).replace(/\.zip$/i, '');
  let packVersion = null;
  let gameVersion = null;
  let loader = null;
  let loaderVersion = null;
  let contentRoot = null;
  let isolationHint = null;
  let versionNameHint = null;

  if (format !== FORMAT.GENERIC && reader.has(config.MRPACK_INDEX)) {
    try {
      const idx = looseJsonParse(reader.readText(config.MRPACK_INDEX));
      packName = idx.name || packName;
      packVersion = idx.versionId || null;
      gameVersion = (idx.dependencies && idx.dependencies.minecraft) || null;
      const l = loaderFromDependencies(idx.dependencies);
      loader = l.loader;
      loaderVersion = l.loaderVersion;
      // mrpack 的 files 数组：非空表示有需要联网下载的部分
      if (Array.isArray(idx.files) && idx.files.length > 0) {
        logger.warn(`整合包含 ${idx.files.length} 个需联网下载的条目，本工具只处理离线打包内容`);
      }
      contentRoot = config.MRPACK_OVERRIDES;
      logger.success(`索引解析成功: ${packName} ${packVersion || ''} | MC ${gameVersion} | ${loader} ${loaderVersion}`);
    } catch (e) {
      logger.warn(`modrinth.index.json 解析失败: ${e.message}`);
    }
  }

  // ---- PCL/Setup.ini 补充信息（版本名 + 隔离状态） ----
  const setupPath = contentRoot ? `${contentRoot}/PCL/Setup.ini` : 'PCL/Setup.ini';
  if (reader.has(setupPath)) {
    try {
      const setup = parseSetupIni(reader.readText(setupPath));
      versionNameHint = setup.VersionVanillaName || null;
      if (!gameVersion && setup.VersionVanillaName) gameVersion = setup.VersionVanillaName;
      if (!loader) {
        if (setup.VersionFabric) { loader = 'Fabric'; loaderVersion = setup.VersionFabric; }
        else if (setup.VersionForge) { loader = 'Forge'; loaderVersion = setup.VersionForge; }
        else if (setup.VersionNeoForge) { loader = 'NeoForge'; loaderVersion = setup.VersionNeoForge; }
        else if (setup.VersionQuilt) { loader = 'Quilt'; loaderVersion = setup.VersionQuilt; }
      }
      // VersionArgumentIndie: 1=开启隔离 2=关闭 0=跟随全局
      if (setup.VersionArgumentIndie !== undefined) {
        isolationHint = setup.VersionArgumentIndie === '1';
      }
      logger.info(`Setup.ini: 版本名=${versionNameHint} 隔离=${isolationHint}`);
    } catch (e) {
      logger.warn(`Setup.ini 解析失败: ${e.message}`);
    }
  }

  // ---- 回落：通用内容根探测 ----
  let rootCandidates = [];
  if (contentRoot === null) {
    onProgress('正在探测整合包结构…', 30);
    rootCandidates = detectContentRoots(reader.entries());
    if (rootCandidates.length === 0) {
      reader.close();
      if (outerReader !== reader) outerReader.close();
      if (tmpFile && !tmpFilePersistent) { try { fs.unlinkSync(tmpFile); } catch (_) {} }
      throw new Error('在整合包里没有找到 mods 文件夹，这可能不是一个有效的整合包');
    }
    contentRoot = rootCandidates[0].root;
    logger.info(`探测到内容根: "${contentRoot}"（候选 ${rootCandidates.length} 个）`);
  }

  // ---- 统计各分类文件数 ----
  const handle = new ModpackHandle({
    zipPath, reader, outerReader, tmpFile, tmpFilePersistent, format, contentRoot,
    packName, packVersion, gameVersion, loader, loaderVersion,
    versionNameHint, isolationHint,
    /** 整合包期望的版本文件夹名，用来自动匹配本地目标目录 */
    versionName: versionNameHint,
    rootCandidates,
  });

  handle.categoryCounts = {};
  for (const c of config.MANAGED_CATEGORIES) {
    handle.categoryCounts[c.key] = handle.listCategory(c.key).length;
  }
  logger.info(`分类统计: ${JSON.stringify(handle.categoryCounts)}`);

  onProgress('整合包读取完成', 35);
  return handle;
}

module.exports = { openModpack, detectContentRoots, parseSetupIni, FORMAT };
