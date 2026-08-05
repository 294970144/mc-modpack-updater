'use strict';

/**
 * 对比引擎
 *
 * 核心策略（性能与正确性的关键）：
 *
 * 1. 指纹预筛。zip 中央目录白送每个条目的 CRC32，本地文件算一次 CRC32。
 *    (size + crc32) 相同 = 字节级一致 = 必然 unchanged，直接跳过元数据解析。
 *    实测同一个包重复对比时，83 个 mod 全部命中，零解析。
 *
 * 2. mods 必须按 jar 内解析出的 modId 匹配，绝不能按文件名。
 *    PCL2 会给 mod 加中文前缀（"[钠] sodium-fabric-0.8.13.jar"），
 *    按文件名比会把同一个 mod 误判成「移除旧的 + 新增新的」。
 *
 * 3. modId 不保证唯一。实测真实整合包里出现过同一个 modId 有两个 jar
 *    （sodium 0.8.12 与 0.8.13 并存），这会让 Fabric 直接崩游戏。
 *    引擎必须把这种情况识别成 conflict 并显式告警，而不是默默取一个。
 *
 * 4. 解析不出 id 的 mod（guessed=true）id 不可信，降级用文件名匹配。
 */

const fs = require('fs');
const path = require('path');
const config = require('../config');
const logger = require('../utils/logger');
const { parseJar } = require('./mod-metadata');
const { crc32, listFilesRecursive, makeMatcher, hasTraversal } = require('../utils/fs-helpers');

/** 保护规则编译一次就缓存，别每次对比都重新编译正则 */
const matcherCache = new Map();
function getProtectedMatcher(categoryKey) {
  if (!matcherCache.has(categoryKey)) {
    matcherCache.set(categoryKey, makeMatcher((config.CATEGORY_PROTECTED || {})[categoryKey] || []));
  }
  return matcherCache.get(categoryKey);
}

const TYPE = {
  ADDED: 'added',
  UPDATED: 'updated',
  REMOVED: 'removed',
  UNCHANGED: 'unchanged',
  CONFLICT: 'conflict',
};

/** 归一化版本串，用于「版本号相同但文件不同」的判断 */
function normVersion(v) {
  return String(v ?? '').trim().toLowerCase();
}

/** 语义化版本粗比较，返回 1 / 0 / -1 */
function compareVersion(a, b) {
  const pa = String(a).split(/[.\-+_]/).filter(Boolean);
  const pb = String(b).split(/[.\-+_]/).filter(Boolean);
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const na = parseInt(pa[i], 10), nb = parseInt(pb[i], 10);
    const va = Number.isNaN(na) ? -1 : na;
    const vb = Number.isNaN(nb) ? -1 : nb;
    if (va !== vb) return va > vb ? 1 : -1;
  }
  return 0;
}

/**
 * 元数据缓存：以指纹为 key，避免重复解析同一个 jar。
 * 落盘在目标 mods 目录旁边，跟着整合包走。
 */
class MetaCache {
  constructor(filePath) {
    this.filePath = filePath;
    /** 指纹 -> 元数据。跳过重复的 jar 解析 */
    this.map = new Map();
    /** 路径|大小|修改时间 -> crc32。跳过重复的本地文件读取，这是第二次对比能秒开的关键 */
    this.crcMap = new Map();
    this.dirty = false;
    try {
      if (filePath && fs.existsSync(filePath)) {
        const j = JSON.parse(fs.readFileSync(filePath, 'utf8'));
        for (const [k, v] of Object.entries(j.entries || {})) this.map.set(k, v);
        for (const [k, v] of Object.entries(j.crc || {})) this.crcMap.set(k, v);
      }
    } catch (_) { /* 缓存坏了就当没有 */ }
  }
  get(fp) { return this.map.get(fp) || null; }
  set(fp, info) {
    this.map.set(fp, { id: info.id, name: info.name, version: info.version, loader: info.loader, guessed: info.guessed });
    this.dirty = true;
  }
  save() {
    if (!this.filePath) return;
    try {
      // crcMap 只保留最近 2000 条，防止无限膨胀
      let crc = [...this.crcMap.entries()];
      if (crc.length > 2000) crc = crc.slice(crc.length - 2000);
      const obj = {
        version: 1,
        updatedAt: new Date().toISOString(),
        entries: Object.fromEntries(this.map),
        crc: Object.fromEntries(crc),
      };
      fs.writeFileSync(this.filePath, JSON.stringify(obj), 'utf8');
    } catch (e) { logger.warn(`元数据缓存写入失败: ${e.message}`); }
  }
}

/** 扫描本地 mods 目录，产出带指纹的条目列表 */
function scanLocalMods(modsDir) {
  const out = [];
  if (!fs.existsSync(modsDir)) return out;
  for (const it of fs.readdirSync(modsDir, { withFileTypes: true })) {
    if (!it.isFile()) continue;
    const lower = it.name.toLowerCase();
    // .disabled 是玩家手动禁用的，视为存在但标记出来
    if (!lower.endsWith('.jar') && !lower.endsWith('.jar.disabled')) continue;
    const full = path.join(modsDir, it.name);
    let st;
    try { st = fs.statSync(full); } catch (_) { continue; }
    out.push({
      fileName: it.name,
      relPath: it.name,
      fullPath: full,
      size: st.size,
      mtimeMs: st.mtimeMs,
      disabled: lower.endsWith('.disabled'),
      crc32: null,
      fingerprint: null,
    });
  }
  return out;
}

/**
 * 按需补算本地文件 CRC32。
 *
 * crcCache 以 (路径|大小|修改时间) 为键，避免每次都重读整个 mods 目录。
 *
 * P1-7: 读出的 Buffer 会暂存到 item._buf，供紧随其后的 resolveLocal 复用
 * （否则 parseJar 又要 readFileSync 一次，83 个 jar 双倍 I/O）。
 * resolveLocal 用完会置空 _buf，不会长期占内存。
 */
function ensureLocalFingerprint(item, crcCache = null) {
  if (item.fingerprint) return item.fingerprint;
  const ck = `${item.fullPath.toLowerCase()}|${item.size}|${Math.round(item.mtimeMs)}`;
  if (crcCache) {
    const hit = crcCache.get(ck);
    if (hit !== undefined) {
      item.crc32 = hit;
      item.fingerprint = `${item.size}:${hit}`;
      return item.fingerprint;
    }
  }
  try {
    const buf = fs.readFileSync(item.fullPath);
    item.crc32 = crc32(buf);
    item.fingerprint = `${item.size}:${item.crc32}`;
    item._buf = buf;   // 暂存给 resolveLocal 复用
    if (crcCache) crcCache.set(ck, item.crc32);
  } catch (_) {
    item.fingerprint = `${item.size}:err`;
  }
  return item.fingerprint;
}

/**
 * 对比 mods
 * @param {Array} zipMods    来自 pack.listCategory('mods')
 * @param {string} localModsDir
 * @param {object} pack      ModpackHandle，用于读取 jar 内容
 * @param {object} manifest  上次安装记录（可为 null）
 * @param {MetaCache} cache
 * @param {(msg,pct)=>void} onProgress
 */
function diffMods(zipMods, localModsDir, pack, manifest, cache, onProgress = () => {}) {
  const jarFilter = (p) => /\.jar(\.disabled)?$/i.test(p);
  const zipItems = zipMods.filter(m => jarFilter(m.relPath));
  const localItems = scanLocalMods(localModsDir);

  logger.info(`对比 mods：整合包 ${zipItems.length} 个 / 本地 ${localItems.length} 个`);

  // ---------- 阶段 1：指纹预筛 ----------
  onProgress('正在比对文件指纹…', 45);
  const zipByFp = new Map();
  for (const z of zipItems) {
    if (!zipByFp.has(z.fingerprint)) zipByFp.set(z.fingerprint, []);
    zipByFp.get(z.fingerprint).push(z);
  }

  // 只对「大小能在 zip 侧找到对应」的本地文件算 CRC，其余必然是差异项
  const zipSizes = new Set(zipItems.map(z => z.size));
  const identicalPairs = [];
  const localRest = [];
  const zipMatchedFp = new Set();
  const crcCache = cache && cache.crcMap ? cache.crcMap : null;

  for (const l of localItems) {
    if (!zipSizes.has(l.size)) { localRest.push(l); continue; }
    const fp = ensureLocalFingerprint(l, crcCache);
    const cand = zipByFp.get(fp);
    if (cand && cand.length > 0) {
      const z = cand.find(c => !c._paired) || cand[0];
      z._paired = true;
      zipMatchedFp.add(z);
      identicalPairs.push({ zip: z, local: l });
    } else {
      localRest.push(l);
    }
  }
  const zipRest = zipItems.filter(z => !zipMatchedFp.has(z));

  logger.info(`指纹预筛：完全一致 ${identicalPairs.length} 对，待解析 zip ${zipRest.length} / 本地 ${localRest.length}`);

  // ---------- 阶段 2：解析元数据（只解析必要的） ----------
  onProgress('正在解析模组元数据…', 55);

  const resolveZip = (z) => {
    const cached = cache.get(z.fingerprint);
    if (cached) return { ...cached, fileName: path.basename(z.relPath) };
    const info = parseJar(pack.read(z.entry), path.basename(z.relPath));
    cache.set(z.fingerprint, info);
    return info;
  };
  const resolveLocal = (l) => {
    ensureLocalFingerprint(l, crcCache);
    const cached = cache.get(l.fingerprint);
    if (cached) { l._buf = null; return { ...cached, fileName: l.fileName }; }
    // P1-7: 复用 ensureLocalFingerprint 暂存的 buf，避免二次读盘
    const buf = l._buf || fs.readFileSync(l.fullPath);
    l._buf = null;   // 用完释放，不长期占内存
    const info = parseJar(buf, l.fileName);
    cache.set(l.fingerprint, info);
    return info;
  };

  // 完全一致的那批也要拿 id（为了重复检测和 manifest），走缓存命中率极高
  const zipInfo = new Map();   // zipItem -> info
  const localInfo = new Map(); // localItem -> info
  for (const { zip, local } of identicalPairs) {
    const info = resolveZip(zip);
    zipInfo.set(zip, info);
    localInfo.set(local, { ...info, fileName: local.fileName });
  }
  for (const z of zipRest) zipInfo.set(z, resolveZip(z));
  for (const l of localRest) localInfo.set(l, resolveLocal(l));

  // ---------- 阶段 3：按 modId 建索引 ----------
  onProgress('正在计算差异…', 70);

  /** 解析成功的用 modId，兜底猜的用 "file:文件名" 以免误匹配 */
  const keyOf = (info, fileName) => (info.guessed ? `file:${fileName.toLowerCase()}` : `id:${info.id}`);

  const zipByKey = new Map();
  for (const z of zipItems) {
    const info = zipInfo.get(z);
    const k = keyOf(info, path.basename(z.relPath));
    if (!zipByKey.has(k)) zipByKey.set(k, []);
    zipByKey.get(k).push({ item: z, info });
  }
  const localByKey = new Map();
  for (const l of localItems) {
    const info = localInfo.get(l);
    const k = keyOf(info, l.fileName);
    if (!localByKey.has(k)) localByKey.set(k, []);
    localByKey.get(k).push({ item: l, info });
  }

  // ---------- 阶段 4：产出变更 ----------
  const changes = [];
  const conflicts = [];

  const manifestFiles = new Set(
    manifest && manifest.mods ? manifest.mods.map(m => (m.fileName || '').toLowerCase()) : []
  );
  const manifestIds = new Set(
    manifest && manifest.mods ? manifest.mods.map(m => m.id).filter(Boolean) : []
  );
  const hasManifest = !!(manifest && Array.isArray(manifest.mods) && manifest.mods.length > 0);

  /** 统一取出条目的展示文件名（zip 侧是 relPath，本地侧是 fileName） */
  const nameOf = (item) => (item.relPath ? path.basename(item.relPath) : item.fileName);

  // 同一 key 在同一侧出现多次 = 重复安装，Fabric/Forge 会直接拒绝启动
  const recordConflict = (side, key, group) => {
    conflicts.push({
      side, key,
      displayName: group[0].info.name || key,
      files: group.map(g => ({
        fileName: nameOf(g.item),
        version: g.info.version,
      })),
    });
  };
  for (const [k, g] of zipByKey) if (g.length > 1) recordConflict('zip', k, g);
  for (const [k, g] of localByKey) if (g.length > 1) recordConflict('local', k, g);

  // 文件名里的「复制」标记：Windows 中文是「 - 副本」，英文是「 - Copy」，还有「(1)」
  const COPY_MARKER = /副本|copy|\(\d+\)/i;
  const isLikelyCopy = (item) => COPY_MARKER.test(nameOf(item));

  /**
   * 多份时取一份作为「代表」参与主变更判断，其余进移除列表。
   * 排序：版本高的优先；版本相同则非副本优先（避免把玩家复制的「xxx - 副本」
   * 当原版保留、反而把真原版标成多余）；都非副本时文件名短的优先。
   */
  const pick = (group) => group.slice().sort((a, b) => {
    const cv = compareVersion(b.info.version, a.info.version);
    if (cv !== 0) return cv;
    const ac = isLikelyCopy(a.item) ? 1 : 0;
    const bc = isLikelyCopy(b.item) ? 1 : 0;
    if (ac !== bc) return ac - bc;
    return nameOf(a.item).length - nameOf(b.item).length;
  })[0];

  const allKeys = new Set([...zipByKey.keys(), ...localByKey.keys()]);
  for (const key of allKeys) {
    const zg = zipByKey.get(key);
    const lg = localByKey.get(key);

    // --- 新增 ---
    if (zg && !lg) {
      const z = pick(zg);
      changes.push({
        type: TYPE.ADDED,
        category: 'mods',
        key,
        modId: z.info.id,
        displayName: z.info.name || z.info.id,
        fileName: path.basename(z.item.relPath),
        newVersion: z.info.version,
        oldVersion: null,
        size: z.item.size,
        loader: z.info.loader,
        checked: true,
        zipRelPath: z.item.relPath,
        duplicated: zg.length > 1,
      });
      continue;
    }

    // --- 移除 ---
    if (!zg && lg) {
      const l = pick(lg);
      // 智能判断：能在上次安装记录里找到 → 整合包装的，默认勾选删除
      //           找不到 → 玩家自己装的，默认不勾并打标签
      let playerInstalled;
      if (!hasManifest) {
        playerInstalled = null;   // 首次运行，无从判断
      } else {
        playerInstalled = !(manifestIds.has(l.info.id) || manifestFiles.has(nameOf(l.item).toLowerCase()));
      }
      changes.push({
        type: TYPE.REMOVED,
        category: 'mods',
        key,
        modId: l.info.id,
        displayName: l.info.name || l.info.id,
        fileName: nameOf(l.item),
        oldVersion: l.info.version,
        newVersion: null,
        size: l.item.size,
        loader: l.info.loader,
        checked: hasManifest ? !playerInstalled : false,
        playerInstalled,
        localPath: l.item.fullPath,
        disabled: l.item.disabled,
        duplicated: lg.length > 1,
      });
      continue;
    }

    // --- 两边都有：更新 or 无变化 ---
    const z = pick(zg);
    const l = pick(lg);
    const sameVersion = normVersion(z.info.version) === normVersion(l.info.version);
    const sameBytes = z.item.fingerprint === l.item.fingerprint;

    if (sameVersion && sameBytes) {
      changes.push({
        type: TYPE.UNCHANGED,
        category: 'mods',
        key,
        modId: z.info.id,
        displayName: z.info.name || z.info.id,
        fileName: path.basename(z.item.relPath),
        oldVersion: l.info.version,
        newVersion: z.info.version,
        size: z.item.size,
        checked: false,
        // 内容一致但文件名不同（PCL2 给 mod 加了中文前缀）—— 不算变更，但值得提示
        renamed: nameOf(z.item) !== nameOf(l.item),
        oldFileName: nameOf(l.item),
      });
      continue;
    }

    const cmp = compareVersion(z.info.version, l.info.version);
    changes.push({
      type: TYPE.UPDATED,
      category: 'mods',
      key,
      modId: z.info.id,
      displayName: z.info.name || z.info.id,
      fileName: nameOf(z.item),
      oldFileName: nameOf(l.item),
      oldVersion: l.info.version,
      newVersion: z.info.version,
      size: z.item.size,
      loader: z.info.loader,
      checked: true,
      // 版本号一样但内容不同：多半是服主重新打包/热修
      reason: sameVersion ? 'content' : (cmp < 0 ? 'downgrade' : 'version'),
      downgrade: cmp < 0,
      zipRelPath: z.item.relPath,
      localPath: l.item.fullPath,
      duplicated: zg.length > 1 || lg.length > 1,
    });
  }

  // ---------- 阶段 5：本地重复 mod 进移除列表 ----------
  // 同一 modId 在本地装了多份（Fabric/Forge 会拒绝启动），保留版本最高的，
  // 其余作为「移除」项列出，标 duplicate=true，默认不勾，让玩家自己决定删哪份。
  // 注意：只处理 local 侧。zip 侧重复是服主打包事故，删了本地下次更新还会装回来，
  // 所以 zip 侧重复只走 conflicts 告警，不产出移除项。
  for (const [key, lg] of localByKey) {
    if (lg.length <= 1) continue;
    const rep = pick(lg);
    for (const g of lg) {
      if (g === rep) continue;
      const dupFileName = nameOf(g.item);
      changes.push({
        type: TYPE.REMOVED,
        category: 'mods',
        // key 加 dup:: 前缀和文件名，避免和主变更撞 key
        key: `dup::${key}::${dupFileName.toLowerCase()}`,
        modId: g.info.id,
        displayName: (g.info.name || g.info.id) + '（重复）',
        fileName: dupFileName,
        oldVersion: g.info.version,
        newVersion: null,
        size: g.item.size,
        loader: g.info.loader,
        checked: false,            // 不自动勾，玩家自己决定
        playerInstalled: null,     // 重复项无法判断归属
        localPath: g.item.fullPath,
        disabled: g.item.disabled,
        duplicate: true,
        duplicateOf: nameOf(rep.item),
        duplicateOfVersion: rep.info.version,
        duplicated: true,
      });
    }
  }

  return { changes, conflicts };
}

/**
 * 对比普通文件类目录（config / resourcepacks / shaderpacks）
 * 无元数据可用，按相对路径 + 指纹对比。
 *
 * resourcepacks / shaderpacks 只认 .zip（Iris/OptiFine 光影包和资源包的标准格式），
 * 避免把说明文档（.txt）、下载残留、.disabled 等当成资源包。
 */
const ALLOWED_EXTS = {
  resourcepacks: ['.zip'],
  shaderpacks: ['.zip'],
};

function diffFiles(categoryKey, zipItems, localDir, manifest, opts = {}) {
  const changes = [];
  const localFiles = fs.existsSync(localDir) ? listFilesRecursive(localDir) : [];

  // 分类内部的保护规则：mod 运行时生成的玩家数据（存档备份、查询历史、缓存…）
  // 混在 config 里，这些一律不参与对比，也不显示
  const isProtected = getProtectedMatcher(categoryKey);
  const maxSize = categoryKey === 'config' ? config.CONFIG_MAX_FILE : Infinity;
  const allowedExt = ALLOWED_EXTS[categoryKey];   // undefined 表示不限制扩展名
  const skipped = { protected: 0, oversize: 0, ext: 0, samples: [] };

  const noteSkip = (why, p) => {
    skipped[why] = (skipped[why] || 0) + 1;
    if (skipped.samples.length < 6) skipped.samples.push(p);
  };

  const keepZip = zipItems.filter(z => {
    // P0-2: 拒绝含 .. 的路径，防整合包构造穿越攻击覆盖分类目录之外的文件
    if (hasTraversal(z.relPath)) { noteSkip('protected', z.relPath); return false; }
    if (isProtected(z.relPath)) { noteSkip('protected', z.relPath); return false; }
    if (z.size > maxSize) { noteSkip('oversize', z.relPath); return false; }
    if (allowedExt) {
      const ext = path.extname(z.relPath).toLowerCase();
      if (!allowedExt.includes(ext)) { noteSkip('ext', z.relPath); return false; }
    }
    return true;
  });
  const keepLocal = localFiles.filter(f => {
    if (hasTraversal(f)) { noteSkip('protected', f); return false; }
    if (isProtected(f)) { noteSkip('protected', f); return false; }
    if (allowedExt) {
      const ext = path.extname(f).toLowerCase();
      if (!allowedExt.includes(ext)) { noteSkip('ext', f); return false; }
    }
    return true;
  });

  if (skipped.protected || skipped.oversize || skipped.ext) {
    logger.info(
      `${categoryKey}: 跳过 ${skipped.protected} 个受保护文件` +
      (skipped.oversize ? `、${skipped.oversize} 个超大文件` : '') +
      (skipped.ext ? `、${skipped.ext} 个非资源包文件` : '') +
      `（例如 ${skipped.samples.slice(0, 3).join('、')}）`
    );
  }

  const zipMap = new Map(keepZip.map(z => [z.relPath.toLowerCase(), z]));
  const localMap = new Map(keepLocal.map(f => [f.toLowerCase(), f]));

  const manifestSet = new Set(
    manifest && manifest.files && manifest.files[categoryKey]
      ? manifest.files[categoryKey].map(f => String(f.path || '').toLowerCase())
      : []
  );
  const hasManifest = manifestSet.size > 0;

  const allKeys = new Set([...zipMap.keys(), ...localMap.keys()]);
  for (const k of allKeys) {
    const z = zipMap.get(k);
    const lRel = localMap.get(k);

    if (z && !lRel) {
      changes.push({
        type: TYPE.ADDED, category: categoryKey, key: k,
        displayName: z.relPath, fileName: z.relPath,
        size: z.size, checked: true, zipRelPath: z.relPath,
      });
      continue;
    }

    if (!z && lRel) {
      const full = path.join(localDir, lRel);
      let lSize = 0;
      try { lSize = fs.statSync(full).size; } catch (_) { /* 读不到就当 0 */ }
      // 本地这边也挡一道：config 里的超大文件多半是数据不是配置，不给删
      if (lSize > maxSize) { noteSkip('oversize', lRel); continue; }

      const playerInstalled = hasManifest ? !manifestSet.has(k) : null;
      changes.push({
        type: TYPE.REMOVED, category: categoryKey, key: k,
        displayName: lRel, fileName: lRel,
        size: lSize, checked: false, playerInstalled,
        localPath: full,
      });
      continue;
    }

    // 两边都有：比指纹
    const full = path.join(localDir, lRel);
    let lSize = -1, lCrc = null;
    try {
      const st = fs.statSync(full);
      lSize = st.size;
      if (lSize === z.size) lCrc = crc32(fs.readFileSync(full));
    } catch (_) { /* ignore */ }

    const same = lSize === z.size && lCrc === z.crc32;
    changes.push({
      type: same ? TYPE.UNCHANGED : TYPE.UPDATED,
      category: categoryKey, key: k,
      displayName: z.relPath, fileName: z.relPath,
      size: z.size, reason: 'content',
      // 配置文件玩家经常自己调参，覆盖会丢设置 —— 默认不勾
      checked: same ? false : (categoryKey === 'config' ? false : true),
      zipRelPath: z.relPath,
      localPath: full,
    });
  }

  return { changes, skipped };
}

module.exports = {
  diffMods, diffFiles, MetaCache, TYPE, compareVersion, scanLocalMods, getProtectedMatcher,
};
