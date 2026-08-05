'use strict';

/**
 * PCL2 启动器探测
 *
 * 三条线索，可靠性从高到低：
 *   1. 正在运行的进程 → wmic 拿 exe 路径 → 反推 PCL2 根目录（最准，玩家此刻用的就是它）
 *   2. PCL/Log1~5.txt 里的「有效的 Minecraft 文件夹：名字 > 路径」→ 拿到所有历史文件夹
 *   3. 常见安装位置兜底扫描
 *
 * 三条线索取并集，去重后返回。
 */

const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');
const config = require('../config');
const logger = require('../utils/logger');

/* ------------------------------------------------------------------ */
/* Setup.ini                                                           */
/* ------------------------------------------------------------------ */

/**
 * PCL2 的 ini 是 `Key:Value` 而不是标准 `Key=Value`，
 * 而且 value 里可能自带冒号（比如 Logo 的 pack:// 路径、ReleaseTime 的时间），
 * 所以只按第一个冒号切。
 */
function parseSetupIni(iniPath) {
  const result = {};
  let text;
  try {
    text = fs.readFileSync(iniPath, 'utf-8');
  } catch (_) {
    return result;
  }
  for (const line of text.split(/\r?\n/)) {
    const s = line.trim();
    if (!s || s.startsWith(';') || s.startsWith('#')) continue;
    const i = s.indexOf(':');
    if (i === -1) continue;
    result[s.slice(0, i).trim()] = s.slice(i + 1).trim();
  }
  return result;
}

/** PCL2 用 `$` 前缀表示「相对于启动器根目录」 */
function resolvePcl2Path(pcl2Root, p) {
  if (!p) return null;
  if (p.startsWith('$')) return path.join(pcl2Root, p.slice(1));
  return p;
}

/* ------------------------------------------------------------------ */
/* 日志解析                                                            */
/* ------------------------------------------------------------------ */

/**
 * 从 Log1~5.txt 里捞出所有 PCL2 认过的 Minecraft 文件夹。
 * 日志是滚动的，Log1 最新，越往后越旧，所以先出现的优先级更高。
 */
function parseFoldersFromLogs(pcl2Root) {
  const folders = [];
  const seen = new Set();
  const logDir = path.join(pcl2Root, config.PCL2_LOG_DIR);

  for (const file of config.PCL2_LOG_FILES) {
    const logPath = path.join(logDir, file);
    let text;
    try {
      if (!fs.existsSync(logPath)) continue;
      text = fs.readFileSync(logPath, 'utf-8');
    } catch (_) {
      continue;
    }
    for (const line of text.split(/\r?\n/)) {
      const m = line.match(config.PCL2_LOG_FOLDER_PATTERN);
      if (!m) continue;
      const name = m[1].trim();
      const dir = m[2].trim().replace(/[\\/]+$/, '');
      const key = dir.toLowerCase();
      if (seen.has(key)) continue;
      seen.add(key);
      folders.push({ name, path: dir, source: 'log' });
    }
  }
  return folders;
}

/* ------------------------------------------------------------------ */
/* 进程探测                                                            */
/* ------------------------------------------------------------------ */

function run(cmd, timeout = 6000) {
  try {
    return execSync(cmd, { encoding: 'utf-8', timeout, windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'] });
  } catch (_) {
    return '';
  }
}

/**
 * 查 PCL2 进程并拿到 exe 完整路径。
 * wmic 在新版 Windows 上可能被移除，所以加了 PowerShell 兜底。
 */
function checkRunning() {
  const out = run('tasklist /FO CSV /NH');
  const hit = config.PCL2_EXE_NAMES.some(n => out.includes(n)) || /Plain Craft Launcher/i.test(out);
  if (!hit) return { running: false, exePath: null };

  // 优先 PowerShell（wmic 在 Win11 24H2 起已被移除）
  const ps = run(
    'powershell -NoProfile -NonInteractive -Command ' +
    '"Get-CimInstance Win32_Process -Filter \\"Name LIKE \'%Plain Craft Launcher%\'\\" ' +
    '| Select-Object -ExpandProperty ExecutablePath"'
  ).trim();
  if (ps) {
    const first = ps.split(/\r?\n/).map(s => s.trim()).filter(Boolean)[0];
    if (first) return { running: true, exePath: first };
  }

  const wmic = run('wmic process where "name like \'%Plain Craft Launcher%\'" get ExecutablePath /FORMAT:LIST');
  const m = wmic.match(/ExecutablePath=(.+)/);
  if (m && m[1].trim()) return { running: true, exePath: m[1].trim() };

  return { running: true, exePath: null };
}

/* ------------------------------------------------------------------ */
/* 根目录候选                                                          */
/* ------------------------------------------------------------------ */

/** 一个目录像不像 PCL2 根：有 exe 或者有 PCL/Setup.ini */
function looksLikePcl2Root(dir) {
  if (!dir || !fs.existsSync(dir)) return false;
  if (fs.existsSync(path.join(dir, config.PCL2_SETUP_INI))) return true;
  return config.PCL2_EXE_NAMES.some(n => fs.existsSync(path.join(dir, n)));
}

function collectRootCandidates(customRoot) {
  const cands = [];
  const push = (p, source) => {
    if (!p) return;
    const abs = path.resolve(p);
    if (cands.some(c => c.path.toLowerCase() === abs.toLowerCase())) return;
    cands.push({ path: abs, source });
  };

  if (customRoot) push(customRoot, 'custom');

  const proc = checkRunning();
  if (proc.exePath) push(path.dirname(proc.exePath), 'process');

  for (const r of config.PCL2_DEFAULT_ROOTS) push(r, 'preset');

  // 各盘根下的常见目录名
  const drives = ['C:', 'D:', 'E:', 'F:', 'G:'];
  const names = ['PCL2', 'PCL', 'pcl2', 'Minecraft', 'mc'];
  for (const d of drives) {
    if (!fs.existsSync(d + '\\')) continue;
    for (const n of names) push(path.join(d + '\\', n), 'scan');
  }

  return { candidates: cands.filter(c => looksLikePcl2Root(c.path)), process: proc };
}

/* ------------------------------------------------------------------ */
/* 主入口                                                              */
/* ------------------------------------------------------------------ */

/**
 * 探测 PCL2。
 * @param {string} [customRoot] 用户手动指定的根目录
 * @returns {{found:boolean, running:boolean, exePath:?string, roots:Array, folders:Array}}
 */
function detect(customRoot) {
  const { candidates, process: proc } = collectRootCandidates(customRoot);

  if (candidates.length === 0) {
    logger.warn('没有找到 PCL2 启动器，稍后可以手动指定游戏目录');
    return { found: false, running: proc.running, exePath: proc.exePath, roots: [], folders: [] };
  }

  const roots = [];
  const folders = [];
  const seenFolder = new Set();

  for (const c of candidates) {
    const setup = parseSetupIni(path.join(c.path, config.PCL2_SETUP_INI));
    const logFolders = parseFoldersFromLogs(c.path);

    roots.push({
      path: c.path,
      source: c.source,
      isActive: !!(proc.exePath && path.dirname(proc.exePath).toLowerCase() === c.path.toLowerCase()),
      setup,
    });

    // Setup.ini 里记录的当前选中文件夹
    const selected = setup.LaunchFolderSelect
      ? path.resolve(resolvePcl2Path(c.path, setup.LaunchFolderSelect)).replace(/[\\/]+$/, '')
      : null;

    const addFolder = (name, dir, source) => {
      if (!dir) return;
      const abs = path.resolve(dir);
      if (!fs.existsSync(abs)) return;
      const key = abs.toLowerCase();
      if (seenFolder.has(key)) return;
      seenFolder.add(key);
      folders.push({
        name,
        path: abs,
        source,
        pcl2Root: c.path,
        isSelected: selected ? key === selected.toLowerCase() : false,
      });
    };

    for (const f of logFolders) addFolder(f.name, f.path, 'log');
    if (selected) addFolder(path.basename(path.dirname(selected)) || '当前文件夹', selected, 'setup');
    addFolder('启动器目录', path.join(c.path, '.minecraft'), 'root');

    // 根目录下一层的 xxx/.minecraft
    try {
      for (const e of fs.readdirSync(c.path, { withFileTypes: true })) {
        if (!e.isDirectory() || e.name === 'PCL') continue;
        addFolder(e.name, path.join(c.path, e.name, '.minecraft'), 'scan');
      }
    } catch (_) { /* 目录读不了就算了 */ }
  }

  logger.success(`找到 ${roots.length} 个启动器目录、${folders.length} 个游戏文件夹`);
  return { found: true, running: proc.running, exePath: proc.exePath, roots, folders };
}

module.exports = { detect, parseSetupIni, resolvePcl2Path, parseFoldersFromLogs, checkRunning, looksLikePcl2Root };
