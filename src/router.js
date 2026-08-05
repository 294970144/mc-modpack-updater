'use strict';

/**
 * API 路由
 *
 * 这是个单用户本地工具，所以直接用模块级变量存会话状态，不搞 session。
 * 流程是有状态的：打开包 → 选目标 → 对比 → 应用，每一步都依赖上一步的结果。
 */

const fs = require('fs');
const path = require('path');

const config = require('./config');
const logger = require('./utils/logger');
const { success, error, sendJson, sendSseHeaders, sendSseData } = require('./utils/response');
const { formatBytes } = require('./utils/fs-helpers');

const { openModpack } = require('./core/modpack-reader');
const { diffMods, diffFiles, MetaCache, TYPE } = require('./core/diff-engine');
const manifestStore = require('./core/manifest');
const applier = require('./core/applier');

const pcl2 = require('./services/pcl2-detector');
const scanner = require('./services/client-scanner');
const dialog = require('./services/dialog');

/* ------------------------------------------------------------------ */
/* 会话状态                                                            */
/* ------------------------------------------------------------------ */

const state = {
  env: null,          // { pcl2, versions }  探测结果，带缓存
  pack: null,         // ModpackHandle
  target: null,       // 选中的版本对象
  diff: null,         // { changes, conflicts, summary }
  cache: null,        // MetaCache
  busy: false,        // 应用/回滚进行中，挡住并发请求
  lastPackDir: null,
};

function closePack() {
  if (state.pack) {
    try { state.pack.close(); } catch (_) { /* ignore */ }
  }
  state.pack = null;
  state.diff = null;
}

/** 把 pack 转成能 JSON 化的摘要（ModpackHandle 里有 fd 和 Buffer，不能直接序列化） */
function packBrief(p) {
  if (!p) return null;
  return {
    zipPath: p.zipPath,
    fileName: path.basename(p.zipPath),
    packName: p.packName,
    packVersion: p.packVersion,
    versionName: p.versionName,
    gameVersion: p.gameVersion,
    loader: p.loader,
    loaderVersion: p.loaderVersion,
    format: p.format,
    isolationHint: p.isolationHint,
    counts: p.categoryCounts,
    size: (() => { try { return fs.statSync(p.zipPath).size; } catch (_) { return 0; } })(),
  };
}

function targetBrief(t) {
  if (!t) return null;
  return {
    name: t.name,
    folderName: t.folderName,
    folderPath: t.folderPath,
    versionDir: t.versionDir,
    contentDir: t.contentDir,
    isolated: t.isolated,
    isolationBasis: t.isolationBasis,
    gameVersion: t.gameVersion,
    loader: t.loader,
    loaderVersion: t.loaderVersion,
    modCount: t.modCount,
    managed: t.managed,
    isSelected: t.isSelected,
    manual: !!t.manual,
    score: t.score,
    why: t.why || [],
  };
}

/* ------------------------------------------------------------------ */
/* 环境探测                                                            */
/* ------------------------------------------------------------------ */

function detectEnv(force = false) {
  if (state.env && !force) return state.env;
  const p = pcl2.detect();
  const versions = scanner.scanAll(p.folders);
  state.env = {
    pcl2: {
      found: p.found,
      running: p.running,
      exePath: p.exePath,
      roots: p.roots.map(r => ({ path: r.path, source: r.source, isActive: r.isActive })),
      folders: p.folders.map(f => ({ name: f.name, path: f.path, isSelected: f.isSelected })),
    },
    versions: versions.map(targetBrief),
    _raw: versions,
  };
  return state.env;
}

/* ------------------------------------------------------------------ */
/* 对比                                                                */
/* ------------------------------------------------------------------ */

/** 变更列表按类型排序：需要注意的排前面 */
const TYPE_ORDER = { updated: 0, added: 1, removed: 2, unchanged: 3 };

/**
 * 合并重复模组冲突。
 *
 * 玩家的 mods 目录本来就是上次从整合包装的，所以整合包里重复的东西，
 * 本地必然也重复——同一个问题会在 zip 和 local 各报一次。
 * 两边文件列表一样时合成一条，并且说清楚根因在整合包。
 */
function mergeConflicts(list) {
  const byKey = new Map();
  for (const c of list) {
    if (!byKey.has(c.key)) byKey.set(c.key, []);
    byKey.get(c.key).push(c);
  }

  const out = [];
  for (const [key, group] of byKey) {
    if (group.length === 1) { out.push(group[0]); continue; }

    const sig = (c) => c.files.map(f => f.fileName).sort().join('|');
    const same = group.every(c => sig(c) === sig(group[0]));
    if (same) {
      const hasZip = group.some(c => c.side === 'zip');
      out.push({
        ...group[0],
        key,
        side: hasZip ? 'both' : group[0].side,
        // 根因在整合包，玩家自己删了也会被下次更新装回来
        rootCause: hasZip ? 'pack' : 'local',
      });
    } else {
      out.push(...group);
    }
  }
  return out;
}

function runDiff(categories, onProgress = () => {}) {
  const pack = state.pack;
  const target = state.target;
  if (!pack) throw new Error('还没有选择整合包');
  if (!target) throw new Error('还没有选择目标版本');

  const contentDir = target.contentDir;
  if (!state.cache) {
    state.cache = new MetaCache(path.join(contentDir, '.mcupdater-cache.json'));
  }

  const manifest = manifestStore.load(contentDir);
  const all = [];
  const conflicts = [];

  const active = config.MANAGED_CATEGORIES.filter(c => categories.includes(c.key));
  if (active.length === 0) throw new Error('至少要选一个更新范围');

  const skipped = { protected: 0, oversize: 0, samples: [] };

  let done = 0;
  for (const cat of active) {
    const zipItems = pack.listCategory(cat.key);
    const localDir = path.join(contentDir, cat.dir);
    onProgress(`正在对比${cat.label}…`, 40 + Math.round((done / active.length) * 45));

    if (cat.byModId) {
      const r = diffMods(zipItems, localDir, pack, manifest, state.cache,
        (m, p) => onProgress(m, p));
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
    done++;
  }

  state.cache.save();

  all.sort((a, b) => {
    const t = (TYPE_ORDER[a.type] ?? 9) - (TYPE_ORDER[b.type] ?? 9);
    if (t !== 0) return t;
    if (a.category !== b.category) return a.category.localeCompare(b.category);
    return String(a.displayName).localeCompare(String(b.displayName), 'zh');
  });

  const summary = { added: 0, updated: 0, removed: 0, unchanged: 0, bytes: 0, byCategory: {} };
  for (const c of all) {
    summary[c.type] = (summary[c.type] || 0) + 1;
    if (c.type !== TYPE.UNCHANGED && c.checked) summary.bytes += c.size || 0;
    const bc = summary.byCategory[c.category] || (summary.byCategory[c.category] = { added: 0, updated: 0, removed: 0, unchanged: 0 });
    bc[c.type] = (bc[c.type] || 0) + 1;
  }
  summary.changed = summary.added + summary.updated + summary.removed;
  summary.bytesText = formatBytes(summary.bytes);

  // 玩家自装的移除项单独统计，前端要突出提示
  summary.playerInstalledRemovals = all.filter(
    c => c.type === TYPE.REMOVED && c.playerInstalled === true
  ).length;
  summary.firstRun = !manifest || !Array.isArray(manifest.mods) || manifest.mods.length === 0;
  summary.skipped = skipped;

  state.diff = {
    changes: all,
    conflicts: mergeConflicts(conflicts),
    summary,
    categories: active.map(c => c.key),
  };
  logger.success(
    `对比完成：新增 ${summary.added} / 更新 ${summary.updated} / 移除 ${summary.removed} / 无变化 ${summary.unchanged}`
  );
  return state.diff;
}

/* ------------------------------------------------------------------ */
/* 请求体读取                                                          */
/* ------------------------------------------------------------------ */

function readBody(req, limit = 4 * 1024 * 1024) {
  return new Promise((resolve, reject) => {
    // P0-3: 写接口必须用 JSON，挡住恶意网页的 simple POST（只能发 text/plain 等）
    if (req.method === 'POST') {
      const ct = (req.headers['content-type'] || '').toLowerCase();
      if (!ct.includes('application/json')) {
        reject(new Error('请求必须是 application/json'));
        return;
      }
    }
    let size = 0;
    const chunks = [];
    req.on('data', (c) => {
      size += c.length;
      if (size > limit) { reject(new Error('请求体过大')); req.destroy(); return; }
      chunks.push(c);
    });
    req.on('end', () => {
      if (chunks.length === 0) return resolve({});
      try { resolve(JSON.parse(Buffer.concat(chunks).toString('utf8'))); }
      catch (e) { reject(new Error('请求体不是合法 JSON')); }
    });
    req.on('error', reject);
  });
}

/* ------------------------------------------------------------------ */
/* 路由表                                                              */
/* ------------------------------------------------------------------ */

const routes = {

  /* ---------- 基本信息 ---------- */

  'GET /api/state': async () => success({
    app: { name: config.APP_NAME, version: config.APP_VERSION },
    pack: packBrief(state.pack),
    target: targetBrief(state.target),
    hasDiff: !!state.diff,
    busy: state.busy,
    categories: config.MANAGED_CATEGORIES.map(c => ({
      key: c.key, label: c.label, defaultOn: c.defaultOn,
    })),
  }),

  'GET /api/env': async (req, res, query) => {
    const env = detectEnv(query.get('refresh') === '1');
    return success({ pcl2: env.pcl2, versions: env.versions });
  },

  /* ---------- 文件选择 ---------- */

  'POST /api/pick/pack': async () => {
    const p = await dialog.pickModpack(state.lastPackDir);
    return success({ path: p });
  },

  'POST /api/pick/folder': async (req) => {
    const body = await readBody(req);
    const p = await dialog.pickFolder(body.initial || (state.target && state.target.contentDir));
    return success({ path: p });
  },

  'POST /api/reveal': async (req) => {
    const body = await readBody(req);
    const ok = dialog.revealInExplorer(body.path);
    return ok ? success({ ok }) : error('NOT_FOUND', '路径不存在');
  },

  /* ---------- 打开整合包 ---------- */

  'POST /api/pack': async (req) => {
    // P0-4: apply 进行中换包会关掉 zip 句柄，致事务中断
    if (state.busy) return error('BUSY', '有操作正在进行中，请稍后');
    const body = await readBody(req);
    const zipPath = String(body.path || '').trim().replace(/^"|"$/g, '');
    if (!zipPath) return error('BAD_REQUEST', '没有提供整合包路径');
    if (!fs.existsSync(zipPath)) return error('NOT_FOUND', `文件不存在：${zipPath}`);
    if (fs.statSync(zipPath).isDirectory()) return error('BAD_REQUEST', '这是一个文件夹，请选择 zip 或 mrpack 文件');

    closePack();
    state.target = null;
    state.cache = null;
    state.lastPackDir = path.dirname(zipPath);

    const t0 = Date.now();
    const pack = await openModpack(zipPath);
    state.pack = pack;

    // 自动匹配目标
    const env = detectEnv();
    const { best, ranked } = scanner.matchTarget(env._raw, pack);
    if (best) state.target = best;

    logger.success(`整合包已就绪，用时 ${Date.now() - t0}ms`);
    return success({
      pack: packBrief(pack),
      target: targetBrief(state.target),
      candidates: ranked.map(targetBrief),
      autoMatched: !!best,
    });
  },

  /* ---------- 选择目标版本 ---------- */

  'POST /api/target': async (req) => {
    // P0-4: apply 进行中改 target 会让写入打到错误目录
    if (state.busy) return error('BUSY', '有操作正在进行中，请稍后');
    const body = await readBody(req);
    state.diff = null;
    state.cache = null;

    if (body.dir) {
      const dir = String(body.dir).trim().replace(/^"|"$/g, '');
      let t;
      try { t = scanner.describeDir(dir); }
      catch (e) { return error('BAD_TARGET', e.message); }
      state.target = t;
    } else if (body.contentDir) {
      const env = detectEnv();
      const hit = env._raw.find(v => v.contentDir.toLowerCase() === String(body.contentDir).toLowerCase());
      if (!hit) return error('NOT_FOUND', '没有找到这个版本，试试刷新一下');
      state.target = hit;
    } else {
      return error('BAD_REQUEST', '需要提供 dir 或 contentDir');
    }

    // 目标变了，给一句风险提示
    const p = state.pack;
    const t = state.target;
    const warnings = [];
    if (p && t) {
      if (p.gameVersion && t.gameVersion && p.gameVersion !== t.gameVersion) {
        warnings.push(`整合包是 ${p.gameVersion}，这个版本是 ${t.gameVersion}，加载器和 mod 可能不兼容`);
      }
      const pl = String(p.loader || '').toLowerCase();
      if (pl && t.loader && pl !== t.loader) {
        warnings.push(`整合包用的是 ${p.loader}，这个版本是 ${t.loader}，装上去大概率起不来`);
      }
      if (t.modCount === 0) {
        warnings.push('这个版本目前一个 mod 都没有，确认是要更新的那个吗？');
      }
    }
    logger.info(`目标版本已设为：${t.contentDir}`);
    return success({ target: targetBrief(t), warnings });
  },

  /* ---------- 对比 ---------- */

  'POST /api/diff': async (req, res) => {
    // P0-4: diff 会写 state，apply 进行中禁止跑
    if (state.busy) { sendJson(res, 409, error('BUSY', '有操作正在进行中')); return null; }
    state.busy = true;

    let body;
    try { body = await readBody(req); }
    catch (e) { state.busy = false; sendJson(res, 400, error('BAD_REQUEST', e.message)); return null; }

    const categories = Array.isArray(body.categories) && body.categories.length
      ? body.categories
      : config.MANAGED_CATEGORIES.filter(c => c.defaultOn).map(c => c.key);

    sendSseHeaders(res);
    const ping = setInterval(() => { try { res.write(': ping\n\n'); } catch (_) { /* ignore */ } }, 20000);
    try {
      const t0 = Date.now();
      const r = runDiff(categories, (msg, pct) => sendSseData(res, { event: 'progress', msg, pct }));
      sendSseData(res, {
        event: 'done',
        changes: r.changes,
        conflicts: r.conflicts,
        summary: r.summary,
        categories: r.categories,
        elapsed: Date.now() - t0,
      });
    } catch (e) {
      logger.error(`对比失败：${e.message}`);
      sendSseData(res, { event: 'error', message: e.message });
    } finally {
      clearInterval(ping);
      state.busy = false;
      res.end();
    }
    return null;   // 已自行处理响应
  },

  /* ---------- 应用更新 ---------- */

  'POST /api/apply': async (req, res) => {
    // P1-5: 同步检查并置位 busy，关闭 await readBody 期间的竞态窗口
    if (state.busy) { sendJson(res, 409, error('BUSY', '已经有一个更新在进行中')); return null; }
    state.busy = true;

    let body;
    try { body = await readBody(req); }
    catch (e) { state.busy = false; sendJson(res, 400, error('BAD_REQUEST', e.message)); return null; }

    if (!state.diff) { state.busy = false; sendJson(res, 400, error('NO_DIFF', '请先做一次对比')); return null; }

    const keys = new Set(Array.isArray(body.keys) ? body.keys : []);
    if (keys.size === 0) { state.busy = false; sendJson(res, 400, error('EMPTY', '一个变更都没勾选')); return null; }

    sendSseHeaders(res);
    // P1-4: 心跳，防止代理/浏览器因长时间无数据断连
    const ping = setInterval(() => { try { res.write(': ping\n\n'); } catch (_) { /* ignore */ } }, 20000);
    try {
      const r = await applier.apply({
        pack: state.pack,
        targetDir: state.target.contentDir,
        changes: state.diff.changes,
        selectedKeys: keys,
        force: !!body.force,
        onProgress: (msg, pct, detail) => sendSseData(res, { event: 'progress', msg, pct, detail }),
      });
      // 应用完了，本地状态已变，缓存的 diff 作废
      state.diff = null;
      // 目标版本的 mod 数量等信息也要重算
      state.env = null;
      sendSseData(res, { event: 'done', result: r });
    } catch (e) {
      logger.error(`更新失败：${e.message}`);
      sendSseData(res, { event: 'error', code: e.code || 'APPLY_FAILED', message: e.message, detail: e.detail || null, rolledBack: e.rolledBack });
    } finally {
      clearInterval(ping);
      state.busy = false;
      res.end();
    }
    return null;
  },

  /* ---------- 备份与回滚 ---------- */

  'GET /api/backups': async (req, res, query) => {
    const dir = query.get('dir') || (state.target && state.target.contentDir);
    if (!dir) return error('BAD_REQUEST', '还没有选择目标版本');
    return success({ backups: applier.listBackups(dir) });
  },

  'POST /api/rollback': async (req, res) => {
    // P1-5: 同步检查并置位 busy
    if (state.busy) { sendJson(res, 409, error('BUSY', '有操作正在进行中')); return null; }
    state.busy = true;

    let body;
    try { body = await readBody(req); }
    catch (e) { state.busy = false; sendJson(res, 400, error('BAD_REQUEST', e.message)); return null; }

    const dir = body.dir || (state.target && state.target.contentDir);
    if (!dir) { state.busy = false; sendJson(res, 400, error('BAD_REQUEST', '还没有选择目标版本')); return null; }
    if (!body.name) { state.busy = false; sendJson(res, 400, error('BAD_REQUEST', '没有指定要回滚到哪个备份')); return null; }
    // P1-6: 备份名只允许时间戳格式，防路径穿越
    const backupName = path.basename(String(body.name));
    if (!/^\d{8}-\d{6}$/.test(backupName)) {
      state.busy = false;
      sendJson(res, 400, error('BAD_NAME', '备份名格式不正确'));
      return null;
    }

    sendSseHeaders(res);
    // P1-4: 心跳，防止代理/浏览器因长时间无数据断连
    const ping = setInterval(() => { try { res.write(': ping\n\n'); } catch (_) { /* ignore */ } }, 20000);
    try {
      const r = await applier.rollbackToBackup(dir, backupName,
        (msg, pct) => sendSseData(res, { event: 'progress', msg, pct }));
      state.diff = null;
      state.env = null;
      state.cache = null;
      sendSseData(res, { event: 'done', result: r });
    } catch (e) {
      logger.error(`回滚失败：${e.message}`);
      sendSseData(res, { event: 'error', code: e.code || 'ROLLBACK_FAILED', message: e.message, rolledBack: e.rolledBack });
    } finally {
      clearInterval(ping);
      state.busy = false;
      res.end();
    }
    return null;
  },

  /* ---------- 日志流 ---------- */

  'GET /api/logs': async (req, res) => {
    sendSseHeaders(res);
    const off = logger.subscribe((entry) => sendSseData(res, { event: 'log', ...entry }));
    const ping = setInterval(() => { try { res.write(': ping\n\n'); } catch (_) { /* ignore */ } }, 20000);
    const cleanup = () => { clearInterval(ping); if (typeof off === 'function') off(); };
    req.on('close', cleanup);
    req.on('error', cleanup);
    return null;
  },

  /* ---------- 收尾 ---------- */

  'POST /api/reset': async () => {
    if (state.busy) return error('BUSY', '有操作正在进行中，请稍后');
    closePack();
    state.target = null;
    state.cache = null;
    state.env = null;
    logger.info('已重置');
    return success({ ok: true });
  },
};

/* ------------------------------------------------------------------ */
/* 分发                                                                */
/* ------------------------------------------------------------------ */

async function handleApi(req, res, url) {
  const key = `${req.method} ${url.pathname}`;
  const fn = routes[key];
  if (!fn) {
    sendJson(res, 404, error('NOT_FOUND', `没有这个接口: ${key}`));
    return;
  }
  try {
    const result = await fn(req, res, url.searchParams);
    if (result !== null && result !== undefined && !res.writableEnded) {
      sendJson(res, result.ok === false ? 400 : 200, result);
    }
  } catch (e) {
    logger.error(`${key} 出错: ${e.message}`);
    if (!res.headersSent) {
      sendJson(res, 500, error('INTERNAL', e.message));
    } else if (!res.writableEnded) {
      try { sendSseData(res, { event: 'error', message: e.message }); } catch (_) { /* ignore */ }
      res.end();
    }
  }
}

module.exports = { handleApi, state, closePack, detectEnv, runDiff };
