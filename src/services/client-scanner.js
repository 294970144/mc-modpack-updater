'use strict';

/**
 * 游戏版本扫描
 *
 * PCL2 的目录是两层：
 *   .minecraft/                ← 「游戏文件夹」
 *     versions/
 *       1.21.11/               ← 「版本」，开了版本隔离时 mods 在这里
 *         mods/ config/ ...
 *     mods/                    ← 没开隔离时 mods 在这里（所有版本共用）
 *
 * 隔离开没开，配置项说了不算——PCL2 那个 VersionArgumentIndie 的取值语义
 * 各版本之间还变过。所以这里以「实际哪个目录里有东西」为准，配置只当参考。
 */

const fs = require('fs');
const path = require('path');
const config = require('../config');
const { parseSetupIni } = require('./pcl2-detector');
const logger = require('../utils/logger');

/** 目录里的文件数量（不递归，够用了） */
function countFiles(dir, filter) {
  try {
    const list = fs.readdirSync(dir, { withFileTypes: true });
    return list.filter(e => e.isFile() && (!filter || filter(e.name))).length;
  } catch (_) {
    return 0;
  }
}

const isJar = (n) => /\.jar(\.disabled)?$/i.test(n);

/** 判断隔离状态：先看实际文件，再看配置 */
function resolveIsolation(mcRoot, versionDir, setup) {
  const isoMods = path.join(versionDir, 'mods');
  const sharedMods = path.join(mcRoot, 'mods');
  const isoCount = countFiles(isoMods, isJar);
  const sharedCount = countFiles(sharedMods, isJar);

  if (isoCount > 0 && sharedCount === 0) return { isolated: true, basis: 'files' };
  if (sharedCount > 0 && isoCount === 0) return { isolated: false, basis: 'files' };
  if (isoCount > 0 && sharedCount > 0) {
    // 两边都有 mod，这种情况看配置，配置也不明确就按隔离算（整合包场景占绝大多数）
    const flag = String(setup.VersionArgumentIndieV2 || setup.VersionArgumentIndie || '').toLowerCase();
    const off = flag === 'false' || flag === '2' || flag === '0';
    return { isolated: !off, basis: 'mixed' };
  }

  // 两边都空：新装的版本，看配置
  const v2 = String(setup.VersionArgumentIndieV2 || '').toLowerCase();
  if (v2 === 'true') return { isolated: true, basis: 'config' };
  if (v2 === 'false') return { isolated: false, basis: 'config' };
  const v1 = String(setup.VersionArgumentIndie || '').trim();
  if (v1 === '1') return { isolated: true, basis: 'config' };
  if (v1 === '2' || v1 === '0') return { isolated: false, basis: 'config' };
  return { isolated: true, basis: 'default' };
}

/** 从 version.json 里读加载器（Setup.ini 缺失时的兜底） */
function loaderFromVersionJson(jsonPath) {
  try {
    const j = JSON.parse(fs.readFileSync(jsonPath, 'utf-8'));
    const blob = [j.id, j.inheritsFrom, JSON.stringify(j.libraries || []).slice(0, 4000)].join(' ');
    if (/fabric[-_]?loader/i.test(blob)) return 'fabric';
    if (/quilt[-_]?loader/i.test(blob)) return 'quilt';
    if (/neoforge/i.test(blob)) return 'neoforge';
    if (/minecraftforge|net\.minecraftforge/i.test(blob)) return 'forge';
    return 'vanilla';
  } catch (_) {
    return null;
  }
}

/**
 * 扫描一个 .minecraft 文件夹下的所有版本
 * @param {string} mcRoot .minecraft 目录
 * @returns {Array} 版本列表
 */
function scanVersions(mcRoot) {
  const out = [];
  const versionsDir = path.join(mcRoot, config.MC_VERSIONS_DIR);
  if (!fs.existsSync(versionsDir)) return out;

  // 文件夹级 PCL.ini 记录了当前选中的版本
  let selectedName = null;
  try {
    const text = fs.readFileSync(path.join(mcRoot, config.MC_PCL_INI), 'utf-8');
    const m = text.match(/^\s*Version\s*:\s*(.+)$/m);
    if (m) selectedName = m[1].trim();
  } catch (_) { /* 没有就没有 */ }

  let entries = [];
  try {
    entries = fs.readdirSync(versionsDir, { withFileTypes: true });
  } catch (_) {
    return out;
  }

  for (const e of entries) {
    if (!e.isDirectory()) continue;
    const name = e.name;
    const versionDir = path.join(versionsDir, name);

    // 有同名 json 才算一个合法版本
    if (!fs.existsSync(path.join(versionDir, name + '.json'))) continue;

    const setup = parseSetupIni(path.join(versionDir, 'PCL', 'Setup.ini'));
    const iso = resolveIsolation(mcRoot, versionDir, setup);
    const contentDir = iso.isolated ? versionDir : mcRoot;

    // 加载器
    let loader = null;
    let loaderVersion = null;
    if (setup.VersionFabric) { loader = 'fabric'; loaderVersion = setup.VersionFabric; }
    else if (setup.VersionNeoForge) { loader = 'neoforge'; loaderVersion = setup.VersionNeoForge; }
    else if (setup.VersionForge) { loader = 'forge'; loaderVersion = setup.VersionForge; }
    else if (setup.VersionQuilt) { loader = 'quilt'; loaderVersion = setup.VersionQuilt; }
    else loader = loaderFromVersionJson(path.join(versionDir, name + '.json'));

    const counts = {};
    for (const cat of config.MANAGED_CATEGORIES) {
      const d = path.join(contentDir, cat.dir);
      counts[cat.key] = cat.key === 'mods'
        ? countFiles(d, isJar)
        : (fs.existsSync(d) ? 1 : 0);   // 非 mods 只记「有没有」，递归统计留给对比阶段
    }

    const hasManifest = fs.existsSync(path.join(contentDir, config.MANIFEST_NAME));

    out.push({
      name,
      versionDir,
      contentDir,
      isolated: iso.isolated,
      isolationBasis: iso.basis,
      gameVersion: setup.VersionVanillaName || null,
      loader,
      loaderVersion,
      info: setup.Info || null,
      isSelected: selectedName === name,
      modCount: counts.mods,
      counts,
      managed: hasManifest,   // 之前用本工具更新过
    });
  }

  out.sort((a, b) => {
    if (a.isSelected !== b.isSelected) return a.isSelected ? -1 : 1;
    if (a.managed !== b.managed) return a.managed ? -1 : 1;
    if (a.modCount !== b.modCount) return b.modCount - a.modCount;
    return a.name.localeCompare(b.name, 'zh');
  });

  return out;
}

/**
 * 扫描全部文件夹下的全部版本，摊平成一个列表
 * @param {Array} folders detect() 返回的 folders
 */
function scanAll(folders) {
  const all = [];
  for (const f of folders) {
    const versions = scanVersions(f.path);
    for (const v of versions) {
      all.push({ ...v, folderName: f.name, folderPath: f.path, folderSelected: f.isSelected });
    }
  }
  logger.info(`共扫描到 ${all.length} 个游戏版本`);
  return all;
}

/**
 * 给整合包挑一个最合适的目标版本。
 *
 * 打分规则（能叠加）：
 *   版本名完全一致       +100
 *   版本名互相包含       +40
 *   原版版本号一致       +30
 *   加载器一致           +15
 *   之前用本工具更新过   +25
 *   PCL2 当前选中        +10
 *   装了 mod             +5
 *
 * @param {Array}  versions scanAll 的结果
 * @param {object} pack     ModpackHandle（要 versionName / gameVersion / loader）
 */
function matchTarget(versions, pack) {
  if (!versions.length) return { best: null, ranked: [] };

  const packName = String(pack.versionName || '').trim().toLowerCase();
  const packGame = String(pack.gameVersion || '').trim().toLowerCase();
  const packLoader = String(pack.loader || '').trim().toLowerCase();

  const ranked = versions.map(v => {
    const name = v.name.toLowerCase();
    const game = String(v.gameVersion || '').toLowerCase();
    let score = 0;
    const why = [];

    if (packName && name === packName) { score += 100; why.push('版本名完全一致'); }
    else if (packName && (name.includes(packName) || packName.includes(name))) { score += 40; why.push('版本名相近'); }

    if (packGame && (game === packGame || name.includes(packGame))) { score += 30; why.push(`同为 ${pack.gameVersion}`); }
    if (packLoader && v.loader === packLoader) { score += 15; why.push(`同为 ${v.loader}`); }
    if (v.managed) { score += 25; why.push('之前用本工具更新过'); }
    // 每个游戏文件夹各有各的「选中版本」，所以文件夹本身选中才更有说服力
    if (v.isSelected && v.folderSelected) { score += 20; why.push('PCL2 当前正在用'); }
    else if (v.isSelected) { score += 8; }
    if (v.modCount > 0) { score += 5; }

    return { ...v, score, why };
  }).sort((a, b) => b.score - a.score);

  const best = ranked[0].score >= 30 ? ranked[0] : null;
  if (best) logger.success(`自动匹配到目标版本：${best.name}（${best.why.join('、')}）`);
  else logger.warn('没能自动匹配到目标版本，需要手动选择');

  return { best, ranked };
}

/**
 * 用户手动指定一个目录时，把它规整成版本对象。
 * 支持直接丢 versions/xxx 目录，也支持丢 .minecraft 目录。
 */
function describeDir(dir) {
  const abs = path.resolve(dir);
  if (!fs.existsSync(abs)) throw new Error(`目录不存在：${abs}`);
  if (!fs.statSync(abs).isDirectory()) throw new Error(`这不是一个目录：${abs}`);

  const name = path.basename(abs);
  const parent = path.dirname(abs);

  // 像是 versions/<name>
  if (path.basename(parent).toLowerCase() === config.MC_VERSIONS_DIR) {
    const mcRoot = path.dirname(parent);
    const list = scanVersions(mcRoot);
    const hit = list.find(v => v.name === name);
    if (hit) return { ...hit, folderName: path.basename(mcRoot), folderPath: mcRoot, manual: true };
  }

  // 像是 .minecraft
  if (fs.existsSync(path.join(abs, config.MC_VERSIONS_DIR))) {
    const list = scanVersions(abs);
    if (list.length) return { ...list[0], folderName: name, folderPath: abs, manual: true };
  }

  // 什么都不像，就当成一个裸内容目录（里面直接有 mods/）
  const counts = {};
  for (const cat of config.MANAGED_CATEGORIES) {
    const d = path.join(abs, cat.dir);
    counts[cat.key] = cat.key === 'mods' ? countFiles(d, isJar) : (fs.existsSync(d) ? 1 : 0);
  }
  if (counts.mods === 0 && !fs.existsSync(path.join(abs, 'mods'))) {
    throw new Error(`这个目录里没有 mods 文件夹，看着不像游戏版本目录：${abs}`);
  }
  return {
    name, versionDir: abs, contentDir: abs, isolated: true, isolationBasis: 'manual',
    gameVersion: null, loader: null, loaderVersion: null, info: null,
    isSelected: false, modCount: counts.mods, counts,
    managed: fs.existsSync(path.join(abs, config.MANIFEST_NAME)),
    folderName: path.basename(path.dirname(abs)), folderPath: path.dirname(abs), manual: true,
  };
}

module.exports = { scanVersions, scanAll, matchTarget, describeDir };
