'use strict';

/**
 * 极简 TOML 子集解析器 —— 只为读 Forge/NeoForge 的 mods.toml 而生。
 *
 * 支持：
 *   - [table] / [[array of tables]]
 *   - key = "value" / 'value' / """多行""" / '''多行'''
 *   - 数字、true/false
 *   - # 注释（含行尾注释，但字符串内的 # 不会被误伤）
 *
 * 不支持（mods.toml 用不到）：日期时间、内联表、数组嵌套、点分键
 */

function stripInlineComment(s) {
  let inSingle = false, inDouble = false;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === '\\' && inDouble) { i++; continue; }
    if (c === "'" && !inDouble) inSingle = !inSingle;
    else if (c === '"' && !inSingle) inDouble = !inDouble;
    else if (c === '#' && !inSingle && !inDouble) return s.slice(0, i);
  }
  return s;
}

function unescape(s) {
  return s.replace(/\\(u[0-9a-fA-F]{4}|.)/g, (m, g) => {
    if (g[0] === 'u') return String.fromCharCode(parseInt(g.slice(1), 16));
    const map = { n: '\n', t: '\t', r: '\r', '"': '"', "'": "'", '\\': '\\', b: '\b', f: '\f' };
    return map[g] !== undefined ? map[g] : g;
  });
}

function parseValue(raw) {
  const s = raw.trim();
  if (!s) return '';
  if (s === 'true') return true;
  if (s === 'false') return false;
  if (/^-?\d+$/.test(s)) return parseInt(s, 10);
  if (/^-?\d*\.\d+$/.test(s)) return parseFloat(s);
  if (s.startsWith('"') && s.endsWith('"') && s.length >= 2) return unescape(s.slice(1, -1));
  if (s.startsWith("'") && s.endsWith("'") && s.length >= 2) return s.slice(1, -1);
  if (s.startsWith('[')) {
    // 简单单行数组
    const inner = s.replace(/^\[|\]$/g, '').trim();
    if (!inner) return [];
    return inner.split(',').map(x => parseValue(x)).filter(x => x !== '');
  }
  return s;
}

function parse(text) {
  const root = {};
  let current = root;

  // 先按行切，但要处理多行字符串
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  let i = 0;

  while (i < lines.length) {
    let line = lines[i];
    const trimmed = line.trim();
    i++;

    if (!trimmed || trimmed.startsWith('#')) continue;

    // [[array of tables]]
    let m = trimmed.match(/^\[\[\s*([^\]]+?)\s*\]\]$/);
    if (m) {
      const key = m[1].trim().replace(/^["']|["']$/g, '');
      if (!Array.isArray(root[key])) root[key] = [];
      current = {};
      root[key].push(current);
      continue;
    }

    // [table]
    m = trimmed.match(/^\[\s*([^\]]+?)\s*\]$/);
    if (m) {
      const key = m[1].trim().replace(/^["']|["']$/g, '');
      if (typeof root[key] !== 'object' || root[key] === null || Array.isArray(root[key])) {
        root[key] = {};
      }
      current = root[key];
      continue;
    }

    // key = value
    const eq = trimmed.indexOf('=');
    if (eq === -1) continue;
    const key = trimmed.slice(0, eq).trim().replace(/^["']|["']$/g, '');
    let valPart = trimmed.slice(eq + 1).trim();

    // 多行字符串
    const ml = valPart.match(/^("""|''')/);
    if (ml) {
      const delim = ml[1];
      let body = valPart.slice(3);
      if (body.trimEnd().endsWith(delim) && body.trimEnd().length >= 3) {
        body = body.trimEnd().slice(0, -3);
      } else {
        const parts = [body];
        while (i < lines.length) {
          const l = lines[i]; i++;
          const idx = l.indexOf(delim);
          if (idx !== -1) { parts.push(l.slice(0, idx)); break; }
          parts.push(l);
        }
        body = parts.join('\n');
      }
      // TOML: 紧跟定界符的首个换行会被剔除
      current[key] = body.replace(/^\n/, '');
      continue;
    }

    current[key] = parseValue(stripInlineComment(valPart));
  }

  return root;
}

module.exports = { parse };
