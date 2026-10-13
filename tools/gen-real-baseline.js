'use strict';
/**
 * 真实整合包回归基准：拿 Master 提供的真实 PCL2 导出包与本地 PCL2 目录，
 * 跑 Node 原版完整 diff 链路，输出黄金数据供 C# 侧逐项核对。
 *
 * 【只读】全程只读取，绝不写入这两个路径。
 *
 * 用法：node tools/gen-real-baseline.js
 */
console.log = (...a) => process.stderr.write(a.join(' ') + String.fromCharCode(10));
process.stdout.write = process.stdout.write.bind(process.stdout);

const fs = require('fs');
const os = require('os');
const path = require('path');
const { openModpack } = require('../src/core/modpack-reader');
const { diffMods, diffFiles, MetaCache } = require('../src/core/diff-engine');
const manifestMod = require('../src/core/manifest');

const PACK = 'D:/Users/zhang/Downloads/1.21.11 1.0.6.zip';
const PCL2 = 'E:/mc/pcl2';

// ---- 找到 PCL2 里的目标版本目录（有 mods 的那个）----
//【注意】PCL2 的版本库在 <根>/.minecraft/versions/，不是 <根>/versions/。
// 漏掉 .minecraft 这层会整个找不到目标 —— 这正是 Node版 pcl2-detector
// 要单独处理PCL2 目录结构的原因。
function findVersionDir(root) {
  const bases = [
    path.join(root, '.minecraft', 'versions'),
    path.join(root, 'versions'),
  ];
  for (const versions of bases) {
    if (!fs.existsSync(versions)) continue;
    const out = [];
    for (const v of fs.readdirSync(versions)) {
      const dir = path.join(versions, v);
      const mods = path.join(dir, 'mods');
      if (fs.existsSync(mods)) {
        let n = 0;
        try { n = fs.readdirSync(mods).filter(f => f.toLowerCase().endsWith('.jar')).length; } catch (_) {}
        out.push({ version: v, dir, mods, jarCount: n });
      }
    }
    if (out.length) {
      // 取 jar 最多的那个，最接近玩家实际在用的版本
      out.sort((a, b) => b.jarCount - a.jarCount);
      return out[0];
    }
  }
  return null;
}

(async () => {
  const out = {};
  const target = findVersionDir(PCL2);
  if (!target) {
    process.stderr.write('未在 PCL2 里找到含 mods 的版本目录\n');
  }

  const handle = await openModpack(PACK);
  out.pack = {
    format: handle.format,
    packName: handle.packName,
    packVersion: handle.packVersion,
    gameVersion: handle.gameVersion,
    loader: handle.loader,
    loaderVersion: handle.loaderVersion,
    contentRoot: handle.contentRoot,
    versionName: handle.versionName,
    isolationHint: handle.isolationHint,
    categoryCounts: handle.categoryCounts,
    rootCandidates: handle.rootCandidates,
  };

  if (target) {
    out.target = { version: target.version, jarCount: target.jarCount };

    // ---- manifest 只读 ----
    const mf = manifestMod.load(target.dir);
    out.manifest = mf
      ? {
          packName: mf.packName || null,
          modsCount: Array.isArray(mf.mods) ? mf.mods.length : -1,
          fileCats: Object.keys(mf.files || {}),
        }
      : null;

    // ---- mods 对比 ----
    const zipMods = handle.listCategory('mods');
    const rMods = diffMods(zipMods, target.mods, handle, mf, new MetaCache());
    out.mods = {
      zipCount: zipMods.length,
      localCount: fs.readdirSync(target.mods).filter(f => f.toLowerCase().endsWith('.jar')).length,
      changeCount: rMods.changes.length,
      // 逐项记录 type|key|fileName|checked 的三元组，顺序即展示顺序
      order: rMods.changes.map(c => `${c.type}|${c.key}|${c.fileName}|${c.checked ? 1 : 0}`),
      byType: rMods.changes.reduce((acc, c) => {
        acc[c.type] = (acc[c.type] || 0) + 1;
        return acc;
      }, {}),
      conflicts: rMods.conflicts.map(c => ({ side: c.side, key: c.key, n: c.files.length })),
    };

    // ---- 其余三个分类 ----
    out.files = {};
    for (const cat of ['config', 'resourcepacks', 'shaderpacks']) {
      const localDir = path.join(target.dir, cat);
      const r = diffFiles(cat, handle.listCategory(cat), localDir, mf);
      out.files[cat] = {
        changeCount: r.changes.length,
        byType: r.changes.reduce((acc, c) => {
          acc[c.type] = (acc[c.type] || 0) + 1;
          return acc;
        }, {}),
        skipped: r.skipped,
        // 抽样前 15 条，避免 JSON 过大
        sample: r.changes.slice(0, 15).map(c => `${c.type}|${c.key}|${c.checked ? 1 : 0}`),
      };
    }
  }

  handle.close();
  process.stderr.write('基准生成完成\n');
  fs.writeFileSync('tools/real-baseline.json', JSON.stringify(out, null, 2));
})().catch(e => {
  process.stderr.write('失败: ' + (e && e.stack || e) + String.fromCharCode(10));
  process.exit(1);
});