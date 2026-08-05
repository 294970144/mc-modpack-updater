'use strict';

/**
 * 安装记录（manifest）
 *
 * 存在的唯一理由：区分「这个 mod 是上次整合包装的」还是「玩家自己装的」。
 *
 * 玩家常自己加小地图、背包整理、Mod 菜单这类客户端 mod，整合包里没有。
 * 如果「移除项」无脑默认勾选，一次更新就会把玩家自己装的东西全删光。
 * 有了 manifest 就能判断：
 *   - 能在记录里找到  → 上次是整合包装的 → 默认勾选删除
 *   - 找不到          → 玩家自己装的     → 默认不勾，并打上「你自己装的」标签
 *   - 完全没有 manifest（首次运行）→ 一律不勾，并提示无法区分
 *
 * 文件落在目标版本目录下，跟着这份客户端走。
 */

const fs = require('fs');
const path = require('path');
const config = require('../config');
const logger = require('../utils/logger');

function manifestPath(targetDir) {
  return path.join(targetDir, config.MANIFEST_NAME);
}

function load(targetDir) {
  const p = manifestPath(targetDir);
  try {
    if (!fs.existsSync(p)) return null;
    const j = JSON.parse(fs.readFileSync(p, 'utf8'));
    if (!j || typeof j !== 'object') return null;
    if (!Array.isArray(j.mods)) j.mods = [];
    if (!j.files || typeof j.files !== 'object') j.files = {};
    return j;
  } catch (e) {
    logger.warn(`安装记录读取失败（将当作首次运行处理）: ${e.message}`);
    return null;
  }
}

/**
 * 用「本次应用后的实际状态」重建 manifest。
 * 注意是全量重建而不是增量追加 —— 这样即使上一次记录漂了，
 * 一次更新之后就能自愈。
 */
function build({ targetDir, pack, modsState, filesState, stats, previous }) {
  const history = (previous && Array.isArray(previous.history) ? previous.history : []).slice(-9);
  history.push({
    at: new Date().toISOString(),
    packName: pack.packName,
    packVersion: pack.packVersion,
    gameVersion: pack.gameVersion,
    loader: pack.loader,
    loaderVersion: pack.loaderVersion,
    added: stats.added || 0,
    updated: stats.updated || 0,
    removed: stats.removed || 0,
  });

  return {
    version: 1,
    tool: config.APP_NAME,
    toolVersion: config.APP_VERSION,
    targetDir,
    packName: pack.packName,
    packVersion: pack.packVersion,
    gameVersion: pack.gameVersion,
    loader: pack.loader,
    loaderVersion: pack.loaderVersion,
    appliedAt: new Date().toISOString(),
    mods: modsState,
    files: filesState,
    history,
  };
}

function save(targetDir, data) {
  const p = manifestPath(targetDir);
  const tmp = p + '.tmp';
  try {
    fs.writeFileSync(tmp, JSON.stringify(data, null, 2), 'utf8');
    fs.renameSync(tmp, p);
    logger.success(`安装记录已更新: ${p}`);
    return true;
  } catch (e) {
    logger.error(`安装记录写入失败: ${e.message}`);
    try { fs.rmSync(tmp, { force: true }); } catch (_) { /* ignore */ }
    return false;
  }
}

/**
 * 从当前 mods 目录 + 对比结果推算「应用后」的 mods 状态。
 * @param {Array} changes  全部变更（含 unchanged）
 * @param {Array} applied  实际执行了的变更
 */
function projectModsState(changes, appliedKeys) {
  const state = [];
  for (const c of changes) {
    if (c.category !== 'mods') continue;
    const did = appliedKeys.has(c.key);

    if (c.type === 'unchanged') {
      state.push({ id: c.modId, fileName: c.fileName, version: c.newVersion || c.oldVersion });
    } else if (c.type === 'added') {
      if (did) state.push({ id: c.modId, fileName: c.fileName, version: c.newVersion });
    } else if (c.type === 'updated') {
      state.push(did
        ? { id: c.modId, fileName: c.fileName, version: c.newVersion }
        : { id: c.modId, fileName: c.oldFileName, version: c.oldVersion });
    } else if (c.type === 'removed') {
      // 删掉了就不再记录；没删的保留（可能是玩家自装的）
      if (!did) state.push({ id: c.modId, fileName: c.fileName, version: c.oldVersion, playerInstalled: c.playerInstalled === true });
    }
  }
  return state;
}

function projectFilesState(changes, appliedKeys, categoryKey) {
  const state = [];
  for (const c of changes) {
    if (c.category !== categoryKey) continue;
    const did = appliedKeys.has(c.key);
    if (c.type === 'unchanged') state.push({ path: c.fileName });
    else if (c.type === 'added' && did) state.push({ path: c.fileName });
    else if (c.type === 'updated') state.push({ path: c.fileName });
    else if (c.type === 'removed' && !did) state.push({ path: c.fileName, playerInstalled: c.playerInstalled === true });
  }
  return state;
}

module.exports = { load, save, build, manifestPath, projectModsState, projectFilesState };
