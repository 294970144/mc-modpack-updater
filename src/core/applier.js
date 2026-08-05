'use strict';

/**
 * 更新执行器
 *
 * 事务性设计（「任何一步失败都不留半成品」的具体实现）：
 *
 *   双保险 = 目录备份 + 操作日志（journal）
 *
 *   1. 动手前先把涉及的分类目录整个复制到 备份/<时间戳>/，这是最后一道保险，
 *      也是「一键回滚」的数据来源。
 *   2. 执行过程中每一步都写 journal：
 *        add     → 记新建路径，回滚时删掉
 *        replace → 旧文件先挪进 stash，回滚时挪回来
 *        delete  → 文件挪进 stash（不是真删），回滚时挪回来
 *   3. 任何一步抛错，立刻逆向回放 journal。只动碰过的文件，比整目录还原快得多。
 *   4. 全部成功才清空 stash、写 manifest、写更新日志。
 *
 *   写文件一律先写 .tmp 再 rename —— rename 在同盘符下是原子操作，
 *   避免半个文件写到一半断电/崩溃留下损坏的 jar。
 *
 * Windows 特有的坑：游戏开着的时候 jar 被 java 进程锁住，删/改都会 EBUSY。
 * 所以执行前必须检测 Minecraft 是否在跑，而且要精确到「是不是这个目录的实例」。
 */

const fs = require('fs');
const path = require('path');
const { execSync, execFileSync } = require('child_process');
const config = require('../config');
const logger = require('../utils/logger');
const manifestMod = require('./manifest');
const {
  ensureDir, copyDirSync, rmrf, dirStats, timestampName, formatBytes, listFilesRecursive, isSafeChild,
} = require('../utils/fs-helpers');
const { getProtectedMatcher } = require('./diff-engine');

// ==================== 前置检查 ====================

/**
 * 检测 Minecraft 是否正在运行。
 * 只报「命令行里包含目标目录」的进程，避免把用户其他 Java 程序误判成 MC。
 */
function detectRunningMinecraft(targetDir) {
  const result = { running: false, matched: [], anyJava: 0 };
  // 归一化：小写、统一斜杠、去尾部分隔符
  // 避免 D:\mc\v1 误匹配 D:\mc\v1.21（前缀撞车）
  const needle = String(targetDir || '').toLowerCase().replace(/\//g, '\\').replace(/\\+$/, '');
  const hit = (cmd) => {
    if (!needle) return false;
    return cmd.includes(needle + '\\') || cmd.includes(needle + '/')
        || cmd.endsWith(needle) || cmd.endsWith('"' + needle + '"');
  };

  try {
    // PowerShell 的 CIM 查询比 wmic 稳（wmic 在新版 Windows 已被移除）。
    // 用 execFileSync 数组传参而非 execSync —— 后者走 cmd.exe shell，
    // -Command "..." 里再嵌双引号（WQL 的 "Name='javaw.exe'"）会被 shell 截断，
    // 导致 WQL 被当成位置参数报「参数绑定」错误。
    const out = execFileSync('powershell.exe', [
      '-NoProfile', '-NonInteractive', '-Command',
      `Get-CimInstance Win32_Process -Filter "Name='javaw.exe' OR Name='java.exe'" | Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress`,
    ], {
      encoding: 'utf8', timeout: 8000, windowsHide: true,
    }).trim();

    if (out) {
      let arr = JSON.parse(out);
      if (!Array.isArray(arr)) arr = [arr];
      result.anyJava = arr.length;
      for (const p of arr) {
        const cmd = String(p.CommandLine || '').toLowerCase();
        if (hit(cmd)) {
          result.matched.push({ pid: p.ProcessId, cmd: String(p.CommandLine || '').slice(0, 200) });
        }
      }
      result.running = result.matched.length > 0;
    }
  } catch (_) {
    // PowerShell 不可用时退化成 tasklist：只能知道有没有 java，判断不了是哪个实例
    try {
      const out = execSync('tasklist /FI "IMAGENAME eq javaw.exe" /FO CSV /NH', {
        encoding: 'utf8', timeout: 5000, windowsHide: true,
      });
      result.anyJava = (out.match(/javaw\.exe/gi) || []).length;
    } catch (__) { /* ignore */ }
  }

  return result;
}

/** 粗略检查目标盘剩余空间是否够放备份 + 新文件 */
function checkDiskSpace(targetDir, needBytes) {
  try {
    const drive = path.parse(path.resolve(targetDir)).root.replace(/\\$/, '');
    const ps = `(Get-PSDrive -Name '${drive.replace(':', '')}').Free`;
    const out = execSync(`powershell -NoProfile -NonInteractive -Command "${ps}"`, {
      encoding: 'utf8', timeout: 8000, windowsHide: true,
    }).trim();
    const free = parseInt(out, 10);
    if (Number.isFinite(free)) {
      return { ok: free > needBytes, free, need: needBytes };
    }
  } catch (_) { /* ignore */ }
  return { ok: true, free: null, need: needBytes };
}

/** 目标目录是否可写 */
function checkWritable(dir) {
  try {
    ensureDir(dir);
    const probe = path.join(dir, `.mcupdater-write-test-${Date.now()}`);
    fs.writeFileSync(probe, 'x');
    fs.rmSync(probe, { force: true });
    return true;
  } catch (_) {
    return false;
  }
}

// ==================== 备份 ====================

function backupRoot(targetDir) {
  return path.join(targetDir, config.BACKUP_DIR_NAME);
}

/** 列出已有备份，新的在前 */
function listBackups(targetDir) {
  const root = backupRoot(targetDir);
  if (!fs.existsSync(root)) return [];
  const out = [];
  for (const it of fs.readdirSync(root, { withFileTypes: true })) {
    if (!it.isDirectory() || it.name.startsWith('.')) continue;
    const dir = path.join(root, it.name);
    let meta = null;
    try { meta = JSON.parse(fs.readFileSync(path.join(dir, 'meta.json'), 'utf8')); } catch (_) { /* ignore */ }
    const st = dirStats(dir);
    out.push({
      name: it.name,
      path: dir,
      createdAt: meta?.createdAt || null,
      categories: meta?.categories || [],
      packName: meta?.packName || null,
      packVersion: meta?.packVersion || null,
      files: st.files,
      bytes: st.bytes,
      sizeText: formatBytes(st.bytes),
    });
  }
  out.sort((a, b) => (a.name < b.name ? 1 : -1));
  return out;
}

/** 只保留最近 N 份 */
function pruneBackups(targetDir, keep = config.BACKUP_KEEP) {
  const list = listBackups(targetDir);
  const drop = list.slice(keep);
  for (const b of drop) {
    logger.info(`清理旧备份: ${b.name} (${b.sizeText})`);
    rmrf(b.path);
  }
  return drop.length;
}

/** 备份涉及的分类目录（跳过受保护文件，避免备份膨胀 + 回滚误伤玩家个人数据） */
async function createBackup(targetDir, categoryKeys, pack, onProgress = () => {}) {
  const name = timestampName();
  const dir = path.join(backupRoot(targetDir), name);
  ensureDir(dir);

  const done = [];
  let copied = 0;
  for (const key of categoryKeys) {
    const cat = config.MANAGED_CATEGORIES.find(c => c.key === key);
    if (!cat) continue;
    const src = path.join(targetDir, cat.dir);
    if (!fs.existsSync(src)) continue;

    // 受保护文件（fuji/backup 的存档 zip、jei 查询历史、缓存等）不进备份：
    // 它们可能几百 MB，而且整目录回滚时会把玩家个人数据一起覆盖
    const isProtected = getProtectedMatcher(key);
    const maxSize = key === 'config' ? config.CONFIG_MAX_FILE : Infinity;
    const files = listFilesRecursive(src);
    let skipped = 0;
    onProgress(`正在备份 ${cat.label}（${files.length} 个文件）…`);
    let i = 0;
    for (const rel of files) {
      if (isProtected(rel)) { skipped++; continue; }
      const fullSrc = path.join(src, rel);
      try {
        if (fs.statSync(fullSrc).size > maxSize) { skipped++; continue; }
      } catch (_) { continue; }
      const fullDst = path.join(dir, cat.dir, rel);
      ensureDir(path.dirname(fullDst));
      // P1-1: 用 fs.promises.copyFile 走 libuv 线程池，不阻塞 event loop，
      // SSE 进度帧能实时推送，HTTP 服务能响应
      await fs.promises.copyFile(fullSrc, fullDst);
      copied++;
      // 每 8 个文件让出一次事件循环，让 SSE 帧 flush
      if ((++i) % 8 === 0) await new Promise(r => setImmediate(r));
    }
    if (skipped) logger.info(`备份 ${cat.label} 跳过 ${skipped} 个受保护/超大文件`);
    done.push(key);
  }

  const meta = {
    createdAt: new Date().toISOString(),
    tool: config.APP_NAME,
    categories: done,
    packName: pack?.packName || null,
    packVersion: pack?.packVersion || null,
    gameVersion: pack?.gameVersion || null,
    fileCount: copied,
  };
  fs.writeFileSync(path.join(dir, 'meta.json'), JSON.stringify(meta, null, 2), 'utf8');

  logger.success(`备份完成: ${name}（${copied} 个文件）`);
  return { name, path: dir, categories: done, fileCount: copied };
}

// ==================== 事务执行 ====================

class Journal {
  constructor(stashDir) {
    this.stashDir = stashDir;
    this.ops = [];
    this._n = 0;
    ensureDir(stashDir);
  }

  _stashSlot(ext = '') {
    return path.join(this.stashDir, `s${String(this._n++).padStart(5, '0')}${ext}`);
  }

  /** 新建文件：回滚时删掉 */
  recordAdd(dest) {
    this.ops.push({ op: 'add', dest });
  }

  /** 覆盖前把旧文件挪进 stash */
  stashExisting(dest) {
    if (!fs.existsSync(dest)) return null;
    const slot = this._stashSlot(path.extname(dest));
    fs.renameSync(dest, slot);
    return slot;
  }

  recordReplace(dest, stash) {
    this.ops.push({ op: 'replace', dest, stash });
  }

  recordDelete(dest, stash) {
    this.ops.push({ op: 'delete', dest, stash });
  }

  /**
   * 原子操作：stash 旧文件并立刻记录到 journal。
   * 把 rename 和 record 放在同一个同步方法里，确保两者之间不会被打断
   * （避免 rename 成功后、record 前抛错导致旧文件永久滞留在 stash 里）。
   */
  stashAndReplace(dest) {
    const slot = this.stashExisting(dest);
    if (slot !== null) this.recordReplace(dest, slot);
    return slot;
  }
  stashAndDelete(dest) {
    const slot = this.stashExisting(dest);
    if (slot !== null) this.recordDelete(dest, slot);
    return slot;
  }

  /** 逆向回放：从最后一步往回撤 */
  rollback() {
    let restored = 0, failed = 0;
    for (let i = this.ops.length - 1; i >= 0; i--) {
      const o = this.ops[i];
      try {
        if (o.op === 'add') {
          if (fs.existsSync(o.dest)) fs.rmSync(o.dest, { force: true });
          restored++;
        } else if (o.op === 'replace' || o.op === 'delete') {
          if (fs.existsSync(o.dest)) fs.rmSync(o.dest, { force: true });
          if (o.stash && fs.existsSync(o.stash)) {
            ensureDir(path.dirname(o.dest));
            fs.renameSync(o.stash, o.dest);
          }
          restored++;
        }
      } catch (e) {
        failed++;
        logger.error(`回滚失败: ${o.dest} — ${e.message}`);
      }
    }
    return { restored, failed };
  }

  cleanup() {
    rmrf(this.stashDir);
  }
}

/** 原子写入：先写 .tmp 再 rename，失败也清理残留 tmp */
function atomicWrite(dest, buf) {
  ensureDir(path.dirname(dest));
  const tmp = `${dest}.mcupd-tmp`;
  try {
    fs.writeFileSync(tmp, buf);
    fs.renameSync(tmp, dest);
  } finally {
    try { if (fs.existsSync(tmp)) fs.unlinkSync(tmp); } catch (_) { /* 占用导致删不掉就算了 */ }
  }
}

/**
 * 应用更新
 *
 * @param {object}   opts.pack          ModpackHandle
 * @param {string}   opts.targetDir     目标版本目录
 * @param {Array}    opts.changes       全部变更
 * @param {Set}      opts.selectedKeys  用户勾选的 `${category}::${key}`
 * @param {Function} opts.onProgress    (msg, pct, detail) => void
 * @param {boolean}  opts.force         忽略「游戏正在运行」的阻断
 */
async function apply({ pack, targetDir, changes, selectedKeys, onProgress = () => {}, force = false }) {
  const t0 = Date.now();
  const selKey = (c) => `${c.category}::${c.key}`;
  const todo = changes.filter(c => c.type !== 'unchanged' && selectedKeys.has(selKey(c)));

  if (todo.length === 0) {
    throw new Error('没有勾选任何要应用的变更');
  }

  const touchedCategories = [...new Set(todo.map(c => c.category))];
  const counts = {
    added: todo.filter(c => c.type === 'added').length,
    updated: todo.filter(c => c.type === 'updated').length,
    removed: todo.filter(c => c.type === 'removed').length,
  };
  logger.step(`准备应用: 新增 ${counts.added} / 更新 ${counts.updated} / 移除 ${counts.removed}`);

  // ---------- 1. 前置检查 ----------
  onProgress('正在做安全检查…', 2);

  const mc = detectRunningMinecraft(targetDir);
  if (mc.running && !force) {
    const err = new Error(
      `检测到 Minecraft 正在运行（进程 ${mc.matched.map(m => m.pid).join(', ')}）。` +
      `游戏运行时 mod 文件被占用，更新会失败。请先关闭游戏再试。`
    );
    err.code = 'MC_RUNNING';
    err.detail = mc;
    err.rolledBack = true;   // 前置检查，还没动任何文件
    throw err;
  }
  if (mc.anyJava > 0 && !mc.running) {
    logger.warn(`检测到 ${mc.anyJava} 个 Java 进程，但都不是这个目录的实例，继续执行`);
  }

  if (!checkWritable(targetDir)) {
    const e = new Error(`目标目录不可写：${targetDir}（可能需要管理员权限，或被杀毒软件锁定）`);
    e.rolledBack = true;
    throw e;
  }

  const needBytes = todo.reduce((s, c) => s + (c.size || 0), 0);
  const backupBytes = touchedCategories.reduce(
    (s, k) => {
      const cat = config.MANAGED_CATEGORIES.find(c => c.key === k);
      return s + (cat ? dirStats(path.join(targetDir, cat.dir)).bytes : 0);
    }, 0
  );
  const disk = checkDiskSpace(targetDir, needBytes + backupBytes + 100 * 1024 * 1024);
  if (!disk.ok) {
    const e = new Error(
      `磁盘空间不足：需要约 ${formatBytes(disk.need)}，剩余 ${formatBytes(disk.free)}。` +
      `请清理磁盘后重试。`
    );
    e.rolledBack = true;
    throw e;
  }

  // ---------- 2. 备份 ----------
  onProgress('正在备份当前文件…', 5);
  const backup = await createBackup(targetDir, touchedCategories, pack, (m) => onProgress(m, 8));

  // ---------- 3. 建立 zip 条目索引 ----------
  const entryIndex = new Map();
  for (const key of touchedCategories) {
    for (const item of pack.listCategory(key)) {
      entryIndex.set(`${key}::${item.relPath}`, item.entry);
    }
  }

  // ---------- 4. 事务执行 ----------
  const stashDir = path.join(backupRoot(targetDir), `.stash-${backup.name}`);
  const journal = new Journal(stashDir);
  const appliedKeys = new Set();
  const applied = [];
  const failures = [];

  try {
    let i = 0;
    for (const c of todo) {
      i++;
      const pct = 15 + Math.round((i / todo.length) * 75);
      const cat = config.MANAGED_CATEGORIES.find(x => x.key === c.category);
      const catDir = path.join(targetDir, cat.dir);

      if (c.type === 'removed') {
        const dest = c.localPath || path.join(catDir, c.fileName);
        onProgress(`移除 ${c.displayName}`, pct, { type: 'removed', name: c.displayName });
        if (fs.existsSync(dest)) {
          journal.stashAndDelete(dest);
        }
        appliedKeys.add(c.key);
        applied.push(c);
        continue;
      }

      // added / updated：从 zip 解出来写进去
      const entry = entryIndex.get(`${c.category}::${c.zipRelPath}`);
      if (!entry) {
        throw new Error(`整合包里找不到条目：${c.category}/${c.zipRelPath}`);
      }

      const dest = path.join(catDir, c.zipRelPath.replace(/\//g, path.sep));
      // P0-2 路径穿越防御：zipRelPath 来自整合包中央目录，恶意/损坏包可能构造 ../../
      // 逃出分类目录覆盖存档。isSafeChild 确保写入路径仍在分类目录内。
      if (!isSafeChild(catDir, dest)) {
        throw new Error(`整合包含非法路径，已中止（防止覆盖分类目录之外的文件）：${c.category}/${c.zipRelPath}`);
      }
      const label = c.type === 'added' ? '安装' : '更新';
      onProgress(`${label} ${c.displayName}`, pct, { type: c.type, name: c.displayName });

      const buf = pack.read(entry);

      if (c.type === 'updated') {
        // 旧文件名可能和新的不一样（PCL2 改过中文名），要删掉旧的那个
        const oldPath = c.localPath || path.join(catDir, c.oldFileName || c.fileName);
        journal.stashAndReplace(oldPath);

        if (path.resolve(oldPath) !== path.resolve(dest)) {
          if (!journal.stashAndReplace(dest)) journal.recordAdd(dest);
        }
      } else {
        if (!journal.stashAndReplace(dest)) journal.recordAdd(dest);
      }

      atomicWrite(dest, buf);
      appliedKeys.add(c.key);
      applied.push(c);
      // P1-1: 每 5 个文件让出事件循环，让 SSE 进度帧能 flush 出去
      // （否则同步循环要等全部跑完才一次性 flush，进度条形同虚设）
      if (i % 5 === 0) await new Promise(r => setImmediate(r));
    }
  } catch (e) {
    logger.error(`执行中断：${e.message}`);
    onProgress('执行失败，正在回滚…', 92);
    const rb = journal.rollback();
    journal.cleanup();
    logger.warn(`回滚完成：还原 ${rb.restored} 项，失败 ${rb.failed} 项`);

    const err = new Error(
      `更新失败并已回滚：${e.message}` +
      (rb.failed > 0
        ? `（有 ${rb.failed} 项回滚失败，请手动从 备份/${backup.name} 恢复）`
        : '（所有改动已还原，文件保持更新前的状态）')
    );
    err.code = 'APPLY_FAILED';
    err.rollback = rb;
    err.backup = backup.name;
    // P1-9: 明确告诉前端回滚是否成功，避免误导
    err.rolledBack = rb.failed === 0;
    throw err;
  }

  // ---------- 5. 收尾 ----------
  onProgress('正在写入记录…', 93);
  journal.cleanup();

  const previous = manifestMod.load(targetDir);
  const modsState = manifestMod.projectModsState(changes, appliedKeys);
  const filesState = {};
  for (const cat of config.MANAGED_CATEGORIES) {
    if (cat.key === 'mods') continue;
    filesState[cat.key] = manifestMod.projectFilesState(changes, appliedKeys, cat.key);
  }
  manifestMod.save(targetDir, manifestMod.build({
    targetDir, pack, modsState, filesState, stats: counts, previous,
  }));

  onProgress('正在生成更新日志…', 96);
  const logPath = writeChangelog(targetDir, { pack, applied, counts, backup, targetDir });

  onProgress('正在清理旧备份…', 98);
  const pruned = pruneBackups(targetDir);

  const elapsed = Date.now() - t0;
  logger.success(`更新完成，耗时 ${(elapsed / 1000).toFixed(1)}s`);
  onProgress('完成', 100);

  return {
    ok: true,
    counts,
    appliedCount: applied.length,
    failures,
    backup: backup.name,
    backupPath: backup.path,
    changelogPath: logPath,
    prunedBackups: pruned,
    elapsedMs: elapsed,
  };
}

// ==================== 更新日志 ====================

function writeChangelog(targetDir, { pack, applied, counts, backup }) {
  const now = new Date();
  const pad = (n) => String(n).padStart(2, '0');
  const stamp = `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())} ${pad(now.getHours())}:${pad(now.getMinutes())}:${pad(now.getSeconds())}`;

  const L = [];
  L.push('='.repeat(60));
  L.push(`  ${config.APP_NAME} — 更新日志`);
  L.push('='.repeat(60));
  L.push('');
  L.push(`更新时间: ${stamp}`);
  L.push(`整合包   : ${pack.packName}${pack.packVersion ? ' ' + pack.packVersion : ''}`);
  L.push(`游戏版本 : ${pack.gameVersion || '未知'}`);
  L.push(`加载器   : ${pack.loader || '未知'} ${pack.loaderVersion || ''}`);
  L.push(`目标目录 : ${targetDir}`);
  L.push(`备份位置 : ${config.BACKUP_DIR_NAME}\\${backup.name}`);
  L.push('');
  L.push(`合计: 新增 ${counts.added} / 更新 ${counts.updated} / 移除 ${counts.removed}`);
  L.push('');

  const group = (type, title) => {
    const list = applied.filter(c => c.type === type);
    if (!list.length) return;
    L.push('-'.repeat(60));
    L.push(`${title}（${list.length}）`);
    L.push('-'.repeat(60));
    for (const c of list) {
      if (type === 'updated') {
        L.push(`  ${c.displayName}`);
        L.push(`      ${c.oldVersion}  →  ${c.newVersion}`);
        if (c.downgrade) L.push('      ⚠ 这是一次版本回退');
        if (c.reason === 'content') L.push('      （版本号未变，但文件内容有更新）');
      } else if (type === 'added') {
        L.push(`  ${c.displayName}  ${c.newVersion || ''}`);
      } else {
        L.push(`  ${c.displayName}  ${c.oldVersion || ''}`);
        if (c.playerInstalled) L.push('      ⚠ 这是你自己安装的，已按你的勾选移除');
      }
    }
    L.push('');
  };

  group('added', '新增');
  group('updated', '更新');
  group('removed', '移除');

  L.push('='.repeat(60));
  L.push('如需撤销本次更新，可在工具里选择「回滚到上次备份」，');
  L.push(`或手动把 ${config.BACKUP_DIR_NAME}\\${backup.name} 里的文件夹复制回来覆盖。`);
  L.push('='.repeat(60));

  const dest = path.join(targetDir, config.CHANGELOG_NAME);
  try {
    // 追加模式：保留历史记录，新的在最上面
    let prev = '';
    if (fs.existsSync(dest)) {
      prev = '\n\n\n' + fs.readFileSync(dest, 'utf8');
    }
    fs.writeFileSync(dest, L.join('\r\n') + prev, 'utf8');
    logger.success(`更新日志已写入: ${dest}`);
  } catch (e) {
    logger.warn(`更新日志写入失败: ${e.message}`);
  }
  return dest;
}

// ==================== 回滚 ====================

/** 一键回滚到指定备份 */
async function rollbackToBackup(targetDir, backupName, onProgress = () => {}) {
  const dir = path.join(backupRoot(targetDir), backupName);
  if (!fs.existsSync(dir)) {
    const e = new Error(`备份不存在: ${backupName}`);
    e.rolledBack = true;
    throw e;
  }

  const mc = detectRunningMinecraft(targetDir);
  if (mc.running) {
    const err = new Error('检测到 Minecraft 正在运行，请先关闭游戏再回滚。');
    err.code = 'MC_RUNNING';
    err.rolledBack = true;
    throw err;
  }

  let meta = {};
  try { meta = JSON.parse(fs.readFileSync(path.join(dir, 'meta.json'), 'utf8')); } catch (_) { /* ignore */ }
  const cats = Array.isArray(meta.categories) && meta.categories.length
    ? meta.categories
    : config.MANAGED_CATEGORIES.map(c => c.key).filter(k => fs.existsSync(path.join(dir, k)));

  onProgress('正在回滚…', 5);

  // P1-3: 文件级回滚（不再整目录替换）
  //   - 备份里有的文件 → 覆盖回 dest（恢复备份状态）
  //   - dest 里有、备份没有、且在当前 manifest 里的 → apply 新增的，删除
  //   - dest 里有、备份没有、不在 manifest 里的 → 玩家后加的，保留
  //   - 受保护文件一律不碰
  // 用 journal 记录每步操作，失败时逆向恢复（和 apply 一样的事务保证）
  const currentManifest = manifestMod.load(targetDir);
  const stashDir = path.join(backupRoot(targetDir), `.rollback-stash-${timestampName()}`);
  const journal = new Journal(stashDir);
  const restored = [];
  const totalCats = cats.length;

  try {
    let catIdx = 0;
    for (const key of cats) {
      catIdx++;
      const cat = config.MANAGED_CATEGORIES.find(c => c.key === key);
      if (!cat) continue;
      const backupCat = path.join(dir, cat.dir);
      if (!fs.existsSync(backupCat)) continue;
      const destCat = path.join(targetDir, cat.dir);
      ensureDir(destCat);

      const isProtected = getProtectedMatcher(key);
      const backupFiles = listFilesRecursive(backupCat);
      const currentFiles = fs.existsSync(destCat) ? listFilesRecursive(destCat) : [];
      const backupSet = new Set(backupFiles.map(f => f.toLowerCase()));

      // 当前 manifest（apply 后的 M2）记录的整合包管理文件，用于识别 apply 新增项
      let m2files = new Set();
      if (key === 'mods') {
        m2files = new Set((currentManifest.mods || []).map(m => String(m.fileName || '').toLowerCase()));
      } else {
        m2files = new Set(((currentManifest.files || {})[key] || []).map(f => String(f.path || '').toLowerCase()));
      }

      onProgress(`正在还原 ${cat.label}…`, 10 + Math.round((catIdx / totalCats) * 80));
      let done = 0;
      const total = backupFiles.length + currentFiles.length;

      // 1. 恢复备份文件（覆盖 dest，跳过受保护）
      for (const rel of backupFiles) {
        if (isProtected(rel)) { done++; continue; }
        const srcFile = path.join(backupCat, rel);
        const dstFile = path.join(destCat, rel);
        ensureDir(path.dirname(dstFile));
        if (fs.existsSync(dstFile)) journal.stashAndReplace(dstFile);
        await fs.promises.copyFile(srcFile, dstFile);
        done++;
        if (done % 8 === 0) {
          onProgress(`正在还原 ${cat.label}…（${done}/${total}）`, 10 + Math.round((catIdx / totalCats) * 80));
          await new Promise(r => setImmediate(r));
        }
      }

      // 2. 删除 apply 新增的（currentFiles 有、备份没有、M2 有、非受保护）
      for (const rel of currentFiles) {
        if (isProtected(rel)) continue;
        const relL = rel.toLowerCase();
        if (!backupSet.has(relL) && m2files.has(relL)) {
          const dstFile = path.join(destCat, rel);
          if (fs.existsSync(dstFile)) {
            // stashAndDelete 会把文件 rename 到 stash（即等于删除 dest 里的），无需再 unlink
            journal.stashAndDelete(dstFile);
          }
        }
        // else: 玩家后加的（不在备份、不在 M2）→ 保留
      }

      restored.push(cat.label);
    }
    journal.cleanup();
  } catch (e) {
    logger.error(`回滚过程出错，正在逆向恢复: ${e.message}`);
    const rb = journal.rollback();
    journal.cleanup();
    logger.warn(`逆向恢复：还原 ${rb.restored} 项，失败 ${rb.failed} 项`);
    const err = new Error(`回滚失败，已恢复到回滚前的状态：${e.message}` +
      (rb.failed > 0 ? `（有 ${rb.failed} 项恢复失败，请从 备份/${backupName} 手动恢复）` : ''));
    err.rolledBack = rb.failed === 0;
    err.rollback = rb;
    throw err;
  }

  onProgress('回滚完成', 100);
  logger.success(`已回滚到备份 ${backupName}（还原 ${restored.join('、')}）`);
  return { ok: true, backupName, restored, createdAt: meta.createdAt || null };
}

module.exports = {
  apply, rollbackToBackup, listBackups, pruneBackups, createBackup,
  detectRunningMinecraft, checkDiskSpace, checkWritable, writeChangelog,
};
