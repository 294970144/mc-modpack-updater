'use strict';

/**
 * 系统文件/文件夹选择对话框
 *
 * 浏览器出于安全限制，<input type=file> 只给文件名不给完整路径，
 * 而这个工具必须拿到磁盘绝对路径，所以只能调 Windows 原生对话框。
 *
 * 两种实现，自动择优：
 *
 * 1) 【快】McDialog.exe —— 预编译的 C# 小工具（~20KB），放在 exe 同目录。
 *    运行时 execFile 它，.NET 冷启动 ~0.3~0.5s，无 PowerShell 引擎开销。
 *    没编译也没关系，自动回退到方案 2。
 *    编译方法见 tools/build-dialog.bat（一次性，本机 .NET Framework 即可）。
 *
 * 2) 【兜底】每次 spawn powershell.exe（-STA + WinForms）执行 .ps1 脚本。
 *    冷启动 ~1.5~2s（PowerShell 固有开销，无法消除）。脚本输出编码设 UTF-8
 *    防中文路径乱码；对话框用「TopMost Form 作 owner」保证弹在最上层。
 *
 * 两者都把选中的路径打到 stdout 第一行，取消则无输出 → 返回 null。
 */

const fs = require('fs');
const path = require('path');
const { execFile } = require('child_process');
const logger = require('../utils/logger');

/* ---------------- McDialog.exe 探测 ---------------- */

let _helper = undefined;  // undefined=未探测, null=没有, string=路径
function findHelper() {
  if (_helper !== undefined) return _helper;
  // 候选位置：SEA 打包后 process.execPath 是 exe 本体，取其目录；dev 模式再找项目根
  const cands = [
    path.join(path.dirname(process.execPath), 'McDialog.exe'),
    path.join(process.cwd(), 'dist', 'McDialog.exe'),
    path.join(process.cwd(), 'McDialog.exe'),
  ];
  for (const c of cands) {
    try { if (fs.existsSync(c)) { _helper = c; return c; } } catch (_) { /* ignore */ }
  }
  _helper = null;
  return null;
}

/** 用 McDialog.exe 弹对话框。mode: 'open' | 'folder'
 *  返回：string=选中路径；null=用户取消；undefined=无 helper / helper 异常（应回退 PowerShell） */
function runHelper(mode, { initialDir, filter, title } = {}) {
  const exe = findHelper();
  if (!exe) return Promise.resolve(undefined);
  const args = [mode];
  if (title) args.push('--title', title);
  if (filter) args.push('--filter', filter);
  if (initialDir) args.push('--initial', initialDir);
  return new Promise((resolve) => {
    execFile(exe, args, { windowsHide: true, maxBuffer: 1 << 20 }, (err, stdout) => {
      if (err) {
        // helper 异常 → 让上层回退到 PowerShell
        logger.warn(`McDialog.exe 异常（回退 PowerShell）: ${err.message}`);
        resolve(undefined);
        return;
      }
      const lines = stdout ? stdout.toString().split('\n').map((s) => s.trim()).filter(Boolean) : [];
      resolve(lines.length ? lines[0] : null);   // null = 用户取消（不要再回退！）
    });
  });
}

/* ---------------- PowerShell 兜底 ---------------- */

/** 写一段 PowerShell 脚本到临时文件（UTF-8 BOM，防中文被当 GBK 读） */
function writeScript(body) {
  const file = path.join(
    require('os').tmpdir(),
    `mcupdater-dlg-${process.pid}-${Date.now()}-${Math.random().toString(36).slice(2, 7)}.ps1`
  );
  fs.writeFileSync(file, '\ufeff' + body, 'utf-8');
  return file;
}

/** 公共头部：关掉报错刷屏 + 控制台输出编码 UTF-8 + 预加载 WinForms */
const HEAD = `
$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
`.trim();

/**
 * 执行一段 PowerShell 脚本，返回 stdout 第一行非空内容（选中路径）；取消则 null。
 * 对话框用 TopMost Form 作 owner，确保弹在最上层（否则会藏在别的窗口后面）。
 */
function runPs(body) {
  const file = writeScript(`${HEAD}\n${body}`);
  const args = ['-NoProfile', '-NonInteractive', '-STA', '-ExecutionPolicy', 'Bypass', '-File', file];
  return new Promise((resolve) => {
    execFile('powershell.exe', args, { windowsHide: true, maxBuffer: 1 << 20 }, (err, stdout, stderr) => {
      try { fs.unlinkSync(file); } catch (_) { /* 临时文件删不掉无所谓 */ }
      if (stderr) {
        const m = stderr.toString().trim();
        if (m) logger.warn('picker stderr: ' + m);
      }
      const lines = stdout ? stdout.toString().split('\n').map((s) => s.trim()).filter(Boolean) : [];
      resolve(lines.length ? lines[0] : null);
    });
  });
}

/* ---------------- 对外接口 ---------------- */

async function pickModpack(initialDir) {
  const init = initialDir && fs.existsSync(initialDir) ? initialDir : '';
  const filter = '整合包 (*.zip;*.mrpack)|*.zip;*.mrpack|压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*';
  const title = '选择整合包文件';

  // 优先 McDialog.exe（快）；undefined=无 helper/异常 → 回退 PowerShell（TopMost 兜底）
  let r = await runHelper('open', { initialDir: init, filter, title });
  if (r === undefined) {
    const body = `
$d = New-Object System.Windows.Forms.OpenFileDialog
$d.Title  = '${title}'
$d.Filter = '${filter}'
$d.Multiselect = $false
${init ? `$d.InitialDirectory = '${init.replace(/'/g, "''")}'` : ''}
$owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true; ShowInTaskbar = $false }
if ($d.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) {
  [Console]::Out.WriteLine($d.FileName)
}
`.trim();
    r = await runPs(body);
  }
  if (r) logger.info(`已选择整合包: ${r}`);
  return r;
}

async function pickFolder(initialDir) {
  const init = initialDir && fs.existsSync(initialDir) ? initialDir : '';
  const title = '选择游戏版本文件夹（里面应该有 mods 目录）';

  let r = await runHelper('folder', { initialDir: init, title });
  if (r === undefined) {
    const body = `
$d = New-Object System.Windows.Forms.FolderBrowserDialog
$d.Description = '${title}'
$d.ShowNewFolderButton = $false
${init ? `$d.SelectedPath = '${init.replace(/'/g, "''")}'` : ''}
$owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true; ShowInTaskbar = $false }
if ($d.ShowDialog($owner) -eq [System.Windows.Forms.DialogResult]::OK) {
  [Console]::Out.WriteLine($d.SelectedPath)
}
`.trim();
    r = await runPs(body);
  }
  if (r) logger.info(`已选择目录: ${r}`);
  return r;
}

/** 在资源管理器里定位一个文件/文件夹 */
function revealInExplorer(target) {
  if (!target || !fs.existsSync(target)) return false;
  const isDir = fs.statSync(target).isDirectory();
  const args = isDir ? [target] : ['/select,', target];
  try {
    execFile('explorer.exe', args, { windowsHide: false }, () => { /* explorer 返回码不可信，忽略 */ });
    return true;
  } catch (_) {
    return false;
  }
}

module.exports = { pickModpack, pickFolder, revealInExplorer };
