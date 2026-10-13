'use strict';
/**
 * M5 基准生成器：完整对比链路（四个分类全开）。
 *
 * 【铁律】期望值必须由 Node 原版实跑得出，不许手算、不许手抄。
 * 这里按 src/router.js 的 runDiff 逐行复刻编排（分类顺序、TYPE_ORDER 排序、
 * summary 计算、skipped 累加），跑在真实整合包 × 真实目标目录上。
 *
 * 【只读】全程只读取，绝不写入目标目录（连 CRC 缓存也不落盘）。
 *
 * 用法：node tools/gen-diffplan-baseline.js > tools/diffplan-baseline.json
 *        （诊断日志走 stderr）
 */
const fs = require('fs');
const path = require('path');

console.log = (...a) => process.stderr.write(a.join(' ') + '\n');

const config = require('../src/config');
const { openModpack } = require('../src/core/modpack-reader');
const { diffMods, diffFiles, MetaCache } = require('../src/core/diff-engine');
const manifestMod = require('../src/core/manifest');
const { formatBytes } = require('../src/utils/fs-helpers');

const PACK = 'D:/Users/zhang/Downloads/1.21.11 1.0.6.zip';
/** M4 自动匹配到的那个目标（1.21.11 @ Client1.21.11，195 分） */
const TARGET_DIR = 'E:/mc/mc26/Client1.21.11/.minecraft/versions/1.21.11';

/** Node router.js: TYPE_ORDER —— 需要注意的排前面 */
const TYPE_ORDER = { updated: 0, added: 1, removed: 2, unchanged: 3 };

function norm(p) {
  return String(p || '').replace(/\\/g, '/');
}

async function main() {
  const out = {
    generatedAt: new Date().toISOString(),
    pack: PACK,
    targetDir: norm(TARGET_DIR),
    summary: null,
    /** 基准时的本地 jar 数 —— 玩家装/删 mod 会让对比结果整体漂移，
     *  C# 侧用它判断"环境没变才跑严格断言"。 */
    targetJarCount: 0,
    categories: [],
    conflicts: 0,
    total: 0,
    byType: {},
    head: [],
  };

  if (!fs.existsSync(PACK) || !fs.existsSync(TARGET_DIR)) {
    process.stderr.write('[warn] 样本不在本机，基准会是空的\n');
    console.log = process.stdout.write.bind(process.stdout);
    process.stdout.write(JSON.stringify(out, null, 2) + '\n');
    return;
  }

  out.targetJarCount = fs.readdirSync(path.join(TARGET_DIR, 'mods'))
    .filter((f) => f.toLowerCase().endsWith('.jar')).length;

  const handle = await openModpack(PACK, () => {});
  const manifest = manifestMod.load(TARGET_DIR);
  const cache = new MetaCache();          // 内存态，不落盘

  const all = [];
  const conflicts = [];
  const skipped = { protected: 0, oversize: 0, samples: [] };

  // 【保真】分类顺序取 config.MANAGED_CATEGORIES 的顺序，不是别的什么顺序
  for (const cat of config.MANAGED_CATEGORIES) {
    const zipItems = handle.listCategory(cat.key);
    const localDir = path.join(TARGET_DIR, cat.dir);
    if (cat.byModId) {
      const r = diffMods(zipItems, localDir, handle, manifest, cache, () => {});
      all.push(...r.changes);
      conflicts.push(...r.conflicts);
    } else {
      const r = diffFiles(cat.key, zipItems, localDir, manifest);
      all.push(...r.changes);
      skipped.protected += r.skipped.protected;
      skipped.oversize += r.skipped.oversize;
      for (const s of r.skipped.samples) {
        if (skipped.samples.length < 8) skipped.samples.push(`${cat.dir}/${s}`);
      }
    }
  }
  out.categories = config.MANAGED_CATEGORIES.map((c) => c.key);

  // ---- 排序（Node runDiff）----
  all.sort((a, b) => {
    const t = (TYPE_ORDER[a.type] ?? 9) - (TYPE_ORDER[b.type] ?? 9);
    if (t !== 0) return t;
    if (a.category !== b.category) return a.category.localeCompare(b.category);
    return String(a.displayName).localeCompare(String(b.displayName), 'zh');
  });

  // ---- summary（Node runDiff）----
  const summary = { added: 0, updated: 0, removed: 0, unchanged: 0, bytes: 0, byCategory: {} };
  for (const c of all) {
    summary[c.type] = (summary[c.type] || 0) + 1;
    if (c.type !== 'unchanged' && c.checked) summary.bytes += c.size || 0;
  }
  summary.changed = summary.added + summary.updated + summary.removed;
  summary.bytesText = formatBytes(summary.bytes);
  summary.playerInstalledRemovals = all.filter(
    (c) => c.type === 'removed' && c.playerInstalled === true).length;
  summary.firstRun = !manifest || !Array.isArray(manifest.mods) || manifest.mods.length === 0;
  summary.skipped = skipped;

  out.summary = {
    added: summary.added,
    updated: summary.updated,
    removed: summary.removed,
    unchanged: summary.unchanged,
    changed: summary.changed,
    bytes: summary.bytes,
    bytesText: summary.bytesText,
    playerInstalledRemovals: summary.playerInstalledRemovals,
    firstRun: summary.firstRun,
    skippedProtected: skipped.protected,
    skippedOversize: skipped.oversize,
    skippedSamples: skipped.samples,
  };

  out.conflicts = conflicts.length;
  out.total = all.length;
  out.byType = {
    added: summary.added, updated: summary.updated,
    removed: summary.removed, unchanged: summary.unchanged,
  };

  // 只留前 40 条的顺序基准（完整列表会让 JSON 到几百 KB）
  out.head = all.slice(0, 40).map(
    (c) => `${c.type}|${c.category}|${c.key}|${c.fileName}|${c.checked ? 1 : 0}`);

  handle.close();

  console.log = process.stdout.write.bind(process.stdout);
  process.stdout.write(JSON.stringify(out, null, 2) + '\n');
}

main().catch((e) => {
  process.stderr.write(`[error] ${e && e.stack}\n`);
  process.exit(1);
});
