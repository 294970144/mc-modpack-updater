'use strict';

/**
 * Mod 元数据解析器
 *
 * jar 本身就是一个 zip，需要在内存里二次解压。解析优先级：
 *   1. fabric.mod.json            Fabric
 *   2. quilt.mod.json             Quilt
 *   3. META-INF/neoforge.mods.toml NeoForge 1.20.5+
 *   4. META-INF/mods.toml          Forge / 早期 NeoForge
 *   5. mcmod.info                  1.12.2 及更早的 Forge
 *   6. 文件名正则                   兜底
 *
 * 关键点：
 *   - version 常见 "${file.jarVersion}" 占位符，需回落到 MANIFEST.MF 的 Implementation-Version
 *   - PCL2 会给 jar 改中文名（如 "[JEI物品管理器] jei-xxx.jar"），
 *     所以绝不能按文件名匹配 mod，必须按解析出的 modId
 */

const { ZipReader } = require('./zip-reader');
const toml = require('./toml-lite');

/**
 * 一趟状态机清洗非法 JSON。
 *
 * 现实中的 fabric.mod.json 什么脏东西都有，实测踩到的：
 *   BetterGrassify 的 description 字段里直接嵌了裸换行 —— JSON 规范不允许，
 *   原生 JSON.parse 直接抛 "Bad control character in string literal"。
 * 用状态机而不是正则，是因为要精确区分「字符串内」和「字符串外」：
 *   字符串内的裸控制字符 → 转义；字符串外的 // 和 /* *\/ → 当注释删掉。
 *   纯正则会把 "https://..." 里的 // 误当注释砍掉。
 */
function sanitizeJson(src) {
  let out = '';
  let inStr = false;
  for (let i = 0; i < src.length; i++) {
    const c = src[i];
    const code = src.charCodeAt(i);

    if (inStr) {
      if (c === '\\') { out += c + (src[i + 1] ?? ''); i++; continue; }
      if (c === '"') { inStr = false; out += c; continue; }
      if (code < 0x20) {
        // 字符串内的裸控制字符：能转义的转义，其余丢弃
        if (c === '\n') out += '\\n';
        else if (c === '\r') out += '\\r';
        else if (c === '\t') out += '\\t';
        continue;
      }
      out += c;
      continue;
    }

    if (c === '"') { inStr = true; out += c; continue; }
    if (c === '/' && src[i + 1] === '/') {
      while (i < src.length && src[i] !== '\n') i++;
      out += '\n';
      continue;
    }
    if (c === '/' && src[i + 1] === '*') {
      i += 2;
      while (i < src.length && !(src[i] === '*' && src[i + 1] === '/')) i++;
      i++;
      continue;
    }
    out += c;
  }
  return out;
}

/** 容错 JSON 解析：去 BOM、清控制字符与注释、去尾逗号 */
function looseJsonParse(text) {
  const s = text.replace(/^\uFEFF/, '');
  try {
    return JSON.parse(s);
  } catch (_) { /* 继续修 */ }

  const cleaned = sanitizeJson(s).replace(/,\s*([}\]])/g, '$1');
  return JSON.parse(cleaned);
}

/** 读 MANIFEST.MF 里的版本，用于替换 ${file.jarVersion} */
function readManifestVersion(jar) {
  try {
    const ent = jar.entry('META-INF/MANIFEST.MF');
    if (!ent) return null;
    const txt = jar.read(ent).toString('utf8');
    const m = txt.match(/Implementation-Version:\s*(.+)/i);
    if (m) return m[1].trim();
    const m2 = txt.match(/Specification-Version:\s*(.+)/i);
    if (m2) return m2[1].trim();
  } catch (_) { /* ignore */ }
  return null;
}

function resolvePlaceholder(version, jar) {
  if (typeof version !== 'string') return String(version ?? '');
  if (!version.includes('${')) return version;
  const fromManifest = readManifestVersion(jar);
  if (fromManifest) return fromManifest;
  return version.replace(/\$\{[^}]+\}/g, '').trim() || '未知';
}

/** 从文件名兜底推断 id 与 version */
function parseFromFileName(fileName) {
  // 先剥掉 PCL2 加的中文前缀 "[xxx] "
  let base = fileName.replace(/^\[[^\]]*\]\s*/, '');
  base = base.replace(/\.jar$/i, '').replace(/\.disabled$/i, '');

  // 常见形态：name-1.2.3+mc1.21.1 / name-fabric-1.2.3 / name_1.2.3
  const m = base.match(/^(.+?)[-_]v?(\d[\w.+\-]*)$/);
  let id, version;
  if (m) {
    id = m[1];
    version = m[2];
  } else {
    id = base;
    version = '未知';
  }

  // 去掉 id 尾部的加载器标识
  id = id.replace(/[-_](fabric|forge|neoforge|quilt|mc\d[\w.]*)$/i, '');
  id = id.toLowerCase().replace(/[^a-z0-9_]/g, '');

  return { id: id || base.toLowerCase(), version, guessed: true };
}

/**
 * 解析一个 jar 的元数据
 * @param {Buffer} jarBuf  jar 文件内容
 * @param {string} fileName 原始文件名（用于兜底与展示）
 * @returns {{id, name, version, loader, guessed, fileName, error?}}
 */
function parseJar(jarBuf, fileName) {
  let jar = null;
  try {
    jar = ZipReader.fromBuffer(jarBuf);
    jar.entries();
  } catch (e) {
    return { ...parseFromFileName(fileName), name: fileName, loader: 'unknown', fileName, error: 'jar 无法打开' };
  }

  try {
    // ---- 1. Fabric ----
    if (jar.has('fabric.mod.json')) {
      const j = looseJsonParse(jar.readText('fabric.mod.json'));
      if (j && j.id) {
        return {
          id: String(j.id),
          name: String(j.name || j.id),
          version: resolvePlaceholder(j.version, jar),
          loader: 'fabric',
          guessed: false,
          fileName,
        };
      }
    }

    // ---- 2. Quilt ----
    if (jar.has('quilt.mod.json')) {
      const j = looseJsonParse(jar.readText('quilt.mod.json'));
      const ql = j && j.quilt_loader;
      if (ql && ql.id) {
        return {
          id: String(ql.id),
          name: String((ql.metadata && ql.metadata.name) || ql.id),
          version: resolvePlaceholder(ql.version, jar),
          loader: 'quilt',
          guessed: false,
          fileName,
        };
      }
    }

    // ---- 3/4. NeoForge / Forge ----
    for (const [path_, loader] of [
      ['META-INF/neoforge.mods.toml', 'neoforge'],
      ['META-INF/mods.toml', 'forge'],
    ]) {
      if (!jar.has(path_)) continue;
      const t = toml.parse(jar.readText(path_));
      const arr = t.mods;
      if (Array.isArray(arr) && arr.length > 0 && arr[0].modId) {
        const m = arr[0];
        return {
          id: String(m.modId),
          name: String(m.displayName || m.modId),
          version: resolvePlaceholder(m.version, jar),
          loader,
          guessed: false,
          fileName,
        };
      }
    }

    // ---- 5. 老版 Forge mcmod.info ----
    if (jar.has('mcmod.info')) {
      const j = looseJsonParse(jar.readText('mcmod.info'));
      const arr = Array.isArray(j) ? j : (j && Array.isArray(j.modList) ? j.modList : null);
      if (arr && arr.length > 0 && arr[0].modid) {
        return {
          id: String(arr[0].modid),
          name: String(arr[0].name || arr[0].modid),
          version: String(arr[0].version || '未知'),
          loader: 'forge',
          guessed: false,
          fileName,
        };
      }
    }
  } catch (e) {
    // 解析失败，落到文件名兜底
    const fb = parseFromFileName(fileName);
    return { ...fb, name: fileName, loader: 'unknown', fileName, error: e.message };
  } finally {
    try { jar.close(); } catch (_) { /* ignore */ }
  }

  // ---- 6. 兜底 ----
  const fb = parseFromFileName(fileName);
  return { ...fb, name: fileName.replace(/\.jar$/i, ''), loader: 'unknown', fileName };
}

module.exports = { parseJar, parseFromFileName, looseJsonParse, sanitizeJson };
