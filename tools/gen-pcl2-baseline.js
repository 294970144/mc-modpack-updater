'use strict';
/**
 * M4 基准生成器：PCL2 探测 + 版本扫描 + 目标匹配。
 *
 * 【铁律】期望值必须由 Node 原版实跑得出，不许手算、不许手抄。
 * M2 的教训：106 条手抄基准错了 2 处，测试变成「验证错误期望」，
 * 比没有测试更糟。所以这里直接 require Node 原版的两个 service，
 * 在本机真实目录上跑一遍，把结果落成 JSON。
 *
 * 用法：node tools/gen-pcl2-baseline.js > tools/pcl2-baseline.json
 *        （诊断日志走 stderr，不会污染 JSON）
 */
const fs = require('fs');
const path = require('path');

// logger 往 stdout 写 ANSI 日志会污染 JSON → 重定向到 stderr
const realLog = console.log;
console.log = (...a) => process.stderr.write(a.join(' ') + '\n');

const pcl2 = require('../src/services/pcl2-detector');
const scanner = require('../src/services/client-scanner');
const { openModpack } = require('../src/core/modpack-reader');

const REAL_PACK = 'D:/Users/zhang/Downloads/1.21.11 1.0.6.zip';
/** 只探测这一个根目录 —— 把测试钉死在已知样本上，避免扫到别的盘产生漂移 */
const REAL_ROOT = 'E:/mc/pcl2';

function norm(p) {
  return String(p || '').replace(/\\/g, '/');
}

async function main() {
  const out = {
    generatedAt: new Date().toISOString(),
    pack: REAL_PACK,
    root: REAL_ROOT,
    packMeta: null,
    detect: null,
    folders: [],
    versions: [],
    ranked: [],
    best: null,
  };

  // ---- 整合包元信息（matchTarget 的输入）----
  if (fs.existsSync(REAL_PACK)) {
    const handle = await openModpack(REAL_PACK, () => {});
    out.packMeta = {
      packName: handle.packName,
      packVersion: handle.packVersion,
      versionName: handle.versionName,
      gameVersion: handle.gameVersion,
      loader: handle.loader,
      loaderVersion: handle.loaderVersion,
    };
    handle.close();

    // ---- 探测 ----
    // customRoot 走 'custom' 源；不传进程探测结果 —— running/exePath 取决于
    // 那一刻 PCL2 有没有在跑，属于非确定性，不进基准。
    const d = pcl2.detect(REAL_ROOT);
    out.detect = { found: d.found, rootCount: d.roots.length, folderCount: d.folders.length };
    out.folders = d.folders.map((f) => ({
      name: f.name,
      path: norm(f.path),
      source: f.source,
      isSelected: f.isSelected,
      pcl2Root: norm(f.pcl2Root),
    }));

    // ---- 扫描 ----
    const all = scanner.scanAll(d.folders);
    out.versions = all.map((v, i) => ({
      index: i,
      name: v.name,
      folderName: v.folderName,
      folderPath: norm(v.folderPath),
      versionDir: norm(v.versionDir),
      contentDir: norm(v.contentDir),
      isolated: v.isolated,
      isolationBasis: v.isolationBasis,
      gameVersion: v.gameVersion,
      loader: v.loader,
      loaderVersion: v.loaderVersion,
      info: v.info,
      isSelected: v.isSelected,
      folderSelected: v.folderSelected,
      modCount: v.modCount,
      counts: v.counts,
      managed: v.managed,
    }));

    // ---- 匹配 ----
    const { best, ranked } = scanner.matchTarget(all, handle);
    out.ranked = ranked.map((v) => ({
      name: v.name,
      folderName: v.folderName,
      contentDir: norm(v.contentDir),
      score: v.score,
      why: v.why || [],
    }));
    out.best = best
      ? { name: best.name, folderName: best.folderName, contentDir: norm(best.contentDir), score: best.score, why: best.why || [] }
      : null;
  } else {
    process.stderr.write(`[warn] 样本不存在：${REAL_PACK}\n`);
  }

  console.log = realLog;
  process.stdout.write(JSON.stringify(out, null, 2) + '\n');
}

main().catch((e) => {
  process.stderr.write(`[error] ${e && e.stack}\n`);
  process.exit(1);
});
