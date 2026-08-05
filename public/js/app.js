/* ================================================================
   主流程
   选包 → 确认目标 → 检查变更 → 应用
   ================================================================ */

(() => {
const { $, $$, esc, bytes, shortPath, toast, overlay, modal, confirm, notice } = UI;

const S = {
  app: null,
  pack: null,
  target: null,
  candidates: [],
  categories: [],          // 可选范围定义
  scope: new Set(),        // 勾选的范围
  changes: [],
  conflicts: [],
  summary: null,
  selected: new Set(),     // 勾选的变更 key（`${category}::${key}`）
  filter: 'all',
  search: '',
  lastResult: null,
};

const CAT_LABEL = {};
const selKey = (c) => `${c.category}::${c.key}`;
const TYPE_CN = { added: '新增', updated: '更新', removed: '移除', unchanged: '无变化' };

/* ================= 步骤切换 ================= */

function goStep(n) {
  for (let i = 1; i <= 4; i++) $('#panel-' + i).classList.toggle('hidden', i !== n);
  $$('#steps .step').forEach(el => {
    const k = +el.dataset.step;
    el.classList.toggle('is-active', k === n);
    el.classList.toggle('is-done', k < n);
  });
  window.scrollTo({ top: 0, behavior: 'smooth' });
}

/* ================= 步骤 1：选包 ================= */

async function openPack(p) {
  if (!p) return;
  $('#pack-hint').className = 'hint';
  $('#pack-hint').textContent = '正在读取整合包…';

  overlay.open('正在读取整合包', { indeterminate: true, msg: '正在解析整合包…' });
  try {
    const r = await API.openPack(p);
    S.pack = r.pack;
    S.target = r.target;
    S.candidates = r.candidates || [];
    overlay.progress('完成', 100);
    renderStep2(r.autoMatched);
    goStep(2);
    $('#pack-hint').textContent = '';
  } catch (e) {
    $('#pack-hint').className = 'hint is-error';
    $('#pack-hint').textContent = e.message;
    toast(e.message, 'err', 5000);
  } finally {
    overlay.close();
  }
}

function bindStep1() {
  $('#btn-pick-pack').onclick = async () => {
    const btn = $('#btn-pick-pack');
    const originalText = btn.textContent;
    btn.disabled = true;
    btn.textContent = '正在打开对话框…';
    try {
      const r = await API.pickPack();
      if (r.path) await openPack(r.path);
    } catch (e) {
      toast(e.message, 'err');
    } finally {
      btn.disabled = false;
      btn.textContent = originalText;
    }
  };

  const input = $('#input-pack-path');
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && input.value.trim()) openPack(input.value.trim());
  });

  // 拖放支持：通过 File System Access API 尝试获取路径，失败则提示
  const dz = $('#dropzone');
  let dragCounter = 0;
  
  ['dragenter', 'dragover'].forEach(ev => dz.addEventListener(ev, (e) => {
    e.preventDefault();
    e.stopPropagation();
    dragCounter++;
    dz.classList.add('is-over');
  }));
  
  ['dragleave', 'drop'].forEach(ev => dz.addEventListener(ev, (e) => {
    e.preventDefault();
    e.stopPropagation();
    dragCounter--;
    if (dragCounter <= 0) {
      dz.classList.remove('is-over');
      dragCounter = 0;
    }
  }));
  
  dz.addEventListener('drop', async (e) => {
    const f = e.dataTransfer.files && e.dataTransfer.files[0];
    if (!f) return;
    
    // 尝试通过 showOpenFilePicker API 获取真实路径（仅 Chromium 86+ 支持）
    try {
      if ('showOpenFilePicker' in window) {
        // 这个 API 需要用户主动触发，拖放场景不适用
        // 但我们可以通过文件对象本身来获取一些信息
        const handle = await getFileHandleFromDrop(f);
        if (handle && handle.fullPath) {
          await openPack(handle.fullPath);
          return;
        }
      }
    } catch (_) { /* ignore */ }
    
    // 浏览器安全策略下拿不到拖入文件的真实路径
    // 显示友好提示并提供快速操作
    $('#pack-hint').className = 'hint is-error';
    $('#pack-hint').textContent =
      `浏览器不允许网页读取「${f.name}」的完整路径，请点上面的「浏览文件…」按钮，或把路径粘贴到输入框。`;
    
    // 3 秒后自动清除提示
    setTimeout(() => {
      if ($('#pack-hint').textContent.includes(f.name)) {
        $('#pack-hint').className = 'hint';
        $('#pack-hint').textContent = '';
      }
    }, 5000);
  });
}

/**
 * 尝试从拖放的文件获取句柄（实验性）
 * 注意：由于安全限制，大多数情况下无法获取完整路径
 */
async function getFileHandleFromDrop(file) {
  // File System Access API 在拖放场景中通常不提供 fullPath
  // 这里仅作为未来兼容性的预留
  return null;
}

/* ================= 步骤 2：目标 + 范围 ================= */

function renderPackInfo() {
  const p = S.pack;
  $('#pack-info').innerHTML = `
    <dl class="kv">
      <dt>名称</dt><dd>${esc(p.packName || p.fileName)}</dd>
      <dt>游戏版本</dt><dd>${esc(p.gameVersion || '未知')}
        ${p.loader ? `<span class="tag tag-green">${esc(p.loader)} ${esc(p.loaderVersion || '')}</span>` : ''}</dd>
      <dt>内含</dt><dd>${
        S.categories.map(c => {
          const n = (p.counts && p.counts[c.key]) || 0;
          return n ? `<span class="tag">${esc(c.label)} ${n}</span>` : '';
        }).join('') || '<span class="muted">空</span>'
      }</dd>
      <dt>文件</dt><dd class="path" title="${esc(p.zipPath)}">${esc(shortPath(p.zipPath, 46))} · ${bytes(p.size)}</dd>
    </dl>`;
}

function renderTargetInfo(warnings) {
  const t = S.target;
  const box = $('#target-info');
  if (!t) {
    box.innerHTML = `<div class="notice notice-warn" style="margin:0">
      <div class="notice-icon">⚠</div>
      <div class="notice-body"><strong>没能自动认出该更新哪个版本</strong>
      点右上角「手动选择」指定游戏版本文件夹。</div></div>`;
    return;
  }

  const tags = [];
  if (t.isSelected) tags.push('<span class="tag tag-green">PCL2 当前选中</span>');
  if (t.managed) tags.push('<span class="tag tag-blue">本工具管理中</span>');
  if (t.manual) tags.push('<span class="tag tag-purple">手动指定</span>');
  if (!t.isolated) tags.push('<span class="tag tag-amber">未开版本隔离</span>');

  box.innerHTML = `
    <dl class="kv">
      <dt>版本</dt><dd>${esc(t.name)} <span class="muted small">（${esc(t.folderName || '')}）</span><br>${tags.join(' ')}</dd>
      <dt>现有模组</dt><dd>${t.modCount} 个</dd>
      <dt>目录</dt><dd class="path" title="${esc(t.contentDir)}">${esc(shortPath(t.contentDir, 46))}</dd>
    </dl>
    ${(warnings && warnings.length) ? notice('warn', '这些地方对不上，确认一下', 
      '<ul>' + warnings.map(w => `<li>${esc(w)}</li>`).join('') + '</ul>') : ''}
    ${(t.why && t.why.length && !t.manual) ? `<p class="muted small" style="margin-top:8px">
      自动匹配依据：${esc(t.why.join('、'))}</p>` : ''}`;
}

function renderScope() {
  const p = S.pack;
  const desc = {
    mods: '整合包的核心，一般都要更新',
    config: '模组的参数设置，你手动调过的会被覆盖',
    resourcepacks: '材质包文件',
    shaderpacks: '光影包文件',
  };
  $('#scope-list').innerHTML = S.categories.map(c => {
    const n = (p.counts && p.counts[c.key]) || 0;
    const on = S.scope.has(c.key);
    const dis = n === 0;
    return `<label class="scope ${on ? 'is-on' : ''}" data-key="${c.key}" ${dis ? 'style="opacity:.45;cursor:not-allowed"' : ''}>
      <input type="checkbox" class="cb" ${on ? 'checked' : ''} ${dis ? 'disabled' : ''}>
      <div class="scope-main">
        <div class="scope-name">${esc(c.label)} ${dis ? '<span class="muted small">（包里没有）</span>' : `<span class="muted small">${n} 项</span>`}</div>
        <div class="scope-desc">${desc[c.key] || ''}</div>
      </div>
    </label>`;
  }).join('');

  $$('#scope-list .scope').forEach(el => {
    const cb = el.querySelector('input');
    cb.onchange = () => {
      const k = el.dataset.key;
      if (cb.checked) S.scope.add(k); else S.scope.delete(k);
      el.classList.toggle('is-on', cb.checked);
      $('#btn-diff').disabled = S.scope.size === 0;
    };
  });
  $('#btn-diff').disabled = S.scope.size === 0;
}

function renderStep2(autoMatched, warnings) {
  // 默认范围：配置里 defaultOn 且包里确实有内容
  if (S.scope.size === 0) {
    for (const c of S.categories) {
      const n = (S.pack.counts && S.pack.counts[c.key]) || 0;
      if (c.defaultOn && n > 0) S.scope.add(c.key);
    }
  }
  renderPackInfo();
  renderTargetInfo(warnings);
  renderScope();
  if (autoMatched === false) toast('没能自动匹配到目标版本，请手动选择', 'warn', 5000);
}

async function chooseTarget() {
  const list = S.candidates.length ? S.candidates : (await API.env()).versions;
  const cur = S.target && S.target.contentDir;

  const body = `
    <p class="muted small" style="margin-bottom:12px">选择要更新的游戏版本，列表按匹配度排序。</p>
    <div class="pick-list">
      ${list.map((v, i) => `
        <button class="pick-item ${i === 0 ? 'is-best' : ''}" data-dir="${esc(v.contentDir)}">
          <div class="pick-main">
            <div class="pick-name">${esc(v.name)}
              <span class="muted small">${esc(v.folderName || '')}</span>
              ${v.contentDir === cur ? '<span class="tag tag-green">当前</span>' : ''}
              ${v.managed ? '<span class="tag tag-blue">管理中</span>' : ''}
            </div>
            <div class="pick-path">${esc(v.contentDir)}</div>
            <div class="muted small">${v.modCount} 个模组${v.gameVersion ? ' · ' + esc(v.gameVersion) : ''}${v.loader ? ' · ' + esc(v.loader) : ''}</div>
          </div>
        </button>`).join('') || '<p class="muted">没有扫描到任何游戏版本</p>'}
    </div>`;

  const p = modal({
    title: '选择目标版本',
    body,
    actions: [
      { label: '取消', value: null },
      { label: '浏览文件夹…', value: '__browse__' },
    ],
  });

  // 列表项点击直接返回
  $$('#modal-body .pick-item').forEach(el => {
    el.onclick = () => UI.closeModal(el.dataset.dir);
  });

  const picked = await p;
  if (!picked) return;

  let payload;
  if (picked === '__browse__') {
    const r = await API.pickFolder(cur);
    if (!r.path) return;
    payload = { dir: r.path };
  } else {
    payload = { contentDir: picked };
  }

  try {
    const r = await API.setTarget(payload);
    S.target = r.target;
    renderTargetInfo(r.warnings);
    toast('目标版本已切换到 ' + r.target.name, 'ok');
  } catch (e) {
    toast(e.message, 'err', 5000);
  }
}

function bindStep2() {
  $('#btn-change-pack').onclick = () => goStep(1);
  $('#btn-change-target').onclick = chooseTarget;
  $('#btn-back-1').onclick = () => goStep(1);
  $('#btn-diff').onclick = runDiff;
}

/* ================= 步骤 3：对比 ================= */

async function runDiff() {
  overlay.open('正在对比', { indeterminate: true, msg: '正在建立连接…' });
  try {
    const r = await API.diff([...S.scope], (e) => {
      if (e.event === 'progress') overlay.progress(e.msg, e.pct);
    });
    S.changes = r.changes;
    S.conflicts = r.conflicts || [];
    S.summary = r.summary;
    S.selected = new Set(r.changes.filter(c => c.type !== 'unchanged' && c.checked).map(selKey));
    S.filter = 'all';
    S.search = '';
    $('#search').value = '';

    renderStep3();
    goStep(3);
    toast(`对比完成，用时 ${r.elapsed}ms`, 'ok');
  } catch (e) {
    toast(e.message, 'err', 6000);
  } finally {
    overlay.close();
  }
}

function renderNotices() {
  const s = S.summary;
  let html = '';

  if (S.conflicts.length) {
    const WHERE = { zip: '整合包里', local: '你的游戏里', both: '整合包和你的游戏里都' };
    const fromPack = S.conflicts.filter(c => c.side !== 'local').length;
    const items = S.conflicts.map(c => `
      <li>${WHERE[c.side] || ''}有 ${c.files.length} 份 <b>${esc(c.displayName)}</b>：
        ${c.files.map(f => `<code>${esc(f.fileName)}</code>`).join(' ')}</li>`).join('');

    html += notice('danger', `发现 ${S.conflicts.length} 处重复模组`,
      `<p>同一个模组装了多份，Fabric / Forge 启动时会直接报错退出。</p>
       <ul>${items}</ul>
       ${fromPack ? `<p style="margin-top:8px">其中 <b>${fromPack} 处是整合包自带的</b>——
         这是打包时的疏漏，你在本地删掉多余的那份，下次更新还会被装回来。
         建议把这条截图发给服主，让他重新打包。</p>` : ''}`);
  }

  if (s.playerInstalledRemovals > 0) {
    html += notice('warn', `有 ${s.playerInstalledRemovals} 个你自己装的模组不在整合包里`,
      `<p>这些默认<b>不会</b>被删除，标了「你装的」。如果想跟整合包完全保持一致，手动勾上它们。</p>`);
  }

  if (s.firstRun && s.removed > 0) {
    html += notice('info', '这是第一次用本工具更新这个版本',
      `<p>还没有安装记录，分不清哪些是你自己装的，所以移除项<b>默认全都不勾</b>。
       请自己过一遍，确认哪些该删。这次更新之后就会有记录，下次会自动判断。</p>`);
  }

  const sk = s.skipped;
  if (sk && (sk.protected || sk.oversize)) {
    html += notice('info', `已跳过 ${sk.protected + sk.oversize} 个文件，不参与更新`,
      `<p>配置目录里混着模组运行时生成的个人数据——存档备份、物品查询历史、语音设备设置、缓存等。
       这些不会被列出，也不会被修改或删除。</p>
       ${sk.samples && sk.samples.length ? `<p class="small" style="margin-top:6px;opacity:.85">
         例如：${sk.samples.slice(0, 4).map(x => `<code>${esc(x)}</code>`).join(' ')}</p>` : ''}`);
  }

  $('#notice-box').innerHTML = '';
  $('#conflict-box').innerHTML = html;
}

function visibleChanges() {
  const q = S.search.trim().toLowerCase();
  return S.changes.filter(c => {
    if (S.filter !== 'all' && c.type !== S.filter) return false;
    if (S.filter === 'all' && c.type === 'unchanged') return false;  // 全部视图里藏掉无变化
    if (!q) return true;
    return String(c.displayName).toLowerCase().includes(q)
        || String(c.fileName || '').toLowerCase().includes(q);
  });
}

function rowHtml(c) {
  const k = selKey(c);
  const on = S.selected.has(k);
  const canCheck = c.type !== 'unchanged';

  const tags = [];
  if (c.playerInstalled === true) tags.push('<span class="tag tag-amber">你装的</span>');
  if (c.downgrade) tags.push('<span class="tag tag-red">降级</span>');
  if (c.reason === 'content' && c.type === 'updated') tags.push('<span class="tag tag-blue">版本号没变但内容变了</span>');
  if (c.duplicate) {
    // 本地多余的重复 mod：明确告诉玩家保留的是哪个，默认不勾
    tags.push(`<span class="tag tag-amber" title="和「${esc(c.duplicateOf)}」重复，建议删掉这份保留另一份">重复·保留 ${esc(UI.shortPath(c.duplicateOf, 22))}</span>`);
  } else if (c.duplicated) {
    tags.push('<span class="tag tag-red">重复</span>');
  }
  if (c.disabled) tags.push('<span class="tag">已禁用</span>');
  if (c.renamed) tags.push('<span class="tag tag-purple">仅文件名不同</span>');

  let ver = '';
  if (c.type === 'updated') {
    ver = `<span class="old">${esc(c.oldVersion || '?')}</span><span class="arw">→</span><span class="new">${esc(c.newVersion || '?')}</span>`;
  } else if (c.type === 'added') {
    ver = `<span class="new">${esc(c.newVersion || '')}</span>`;
  } else if (c.type === 'removed') {
    ver = `<span class="old">${esc(c.oldVersion || '')}</span>`;
  } else {
    ver = `<span class="muted">${esc(c.newVersion || '')}</span>`;
  }

  return `<div class="row ${on ? '' : 'is-unchecked'} ${c.type === 'unchanged' ? 'is-unchanged' : ''}" data-key="${esc(k)}">
    <input type="checkbox" class="cb" ${on ? 'checked' : ''} ${canCheck ? '' : 'disabled'}>
    <span class="row-badge ${c.type}"></span>
    <div class="row-main">
      <div class="row-name">${esc(c.displayName)} ${tags.join('')}</div>
      <div class="row-sub">${esc(c.fileName || '')}</div>
    </div>
    <div class="row-ver">${ver}</div>
    <div class="row-size">${c.size ? bytes(c.size) : ''}</div>
  </div>`;
}

function renderChanges() {
  const list = visibleChanges();
  const box = $('#changes');

  if (!list.length) {
    box.innerHTML = `<div class="empty">${
      S.summary.changed === 0 && S.filter === 'all'
        ? '你的版本和整合包完全一致，不需要更新 🎉'
        : '没有符合条件的项目'
    }</div>`;
    return;
  }

  // 按分类分组
  const groups = new Map();
  for (const c of list) {
    if (!groups.has(c.category)) groups.set(c.category, []);
    groups.get(c.category).push(c);
  }

  let html = '';
  for (const [cat, items] of groups) {
    const selectable = items.filter(c => c.type !== 'unchanged');
    html += `<div class="group-head">
      ${esc(CAT_LABEL[cat] || cat)}
      <span class="count">${items.length} 项</span>
      ${selectable.length ? `<button class="link-btn" data-group="${esc(cat)}">全选 / 全不选</button>` : ''}
    </div>`;
    html += items.map(rowHtml).join('');
  }
  box.innerHTML = html;

  box.querySelectorAll('.row .cb').forEach(cb => {
    cb.onchange = () => {
      const row = cb.closest('.row');
      const k = row.dataset.key;
      if (cb.checked) S.selected.add(k); else S.selected.delete(k);
      row.classList.toggle('is-unchecked', !cb.checked);
      renderStatus();
    };
  });

  box.querySelectorAll('.group-head .link-btn').forEach(b => {
    b.onclick = () => {
      const cat = b.dataset.group;
      const items = visibleChanges().filter(c => c.category === cat && c.type !== 'unchanged');
      const allOn = items.every(c => S.selected.has(selKey(c)));
      items.forEach(c => allOn ? S.selected.delete(selKey(c)) : S.selected.add(selKey(c)));
      renderChanges();
      renderStatus();
    };
  });
}

function renderFilters() {
  const s = S.summary;
  $$('#filters .chip').forEach(el => {
    const f = el.dataset.filter;
    el.classList.toggle('is-on', f === S.filter);
    const b = el.querySelector('b');
    if (b) b.textContent = s[f] || 0;
  });
}

function renderStatus() {
  const chosen = S.changes.filter(c => S.selected.has(selKey(c)));
  const n = { added: 0, updated: 0, removed: 0 };
  let size = 0;
  for (const c of chosen) { n[c.type] = (n[c.type] || 0) + 1; size += c.size || 0; }

  $('#status-left').innerHTML = chosen.length
    ? `已勾选 <span class="num">${chosen.length}</span> 项
       <span class="muted">·</span> 新增 <span class="num">${n.added}</span>
       <span class="muted">·</span> 更新 <span class="num">${n.updated}</span>
       <span class="muted">·</span> 移除 <span class="num">${n.removed}</span>
       <span class="muted">·</span> 写入约 <span class="num">${bytes(size)}</span>`
    : `<span class="muted">还没有勾选任何变更</span>`;

  $('#btn-apply').disabled = chosen.length === 0;
}

function renderStep3() {
  renderNotices();
  renderFilters();
  renderChanges();
  renderStatus();
}

function bindStep3() {
  $$('#filters .chip').forEach(el => {
    el.onclick = () => { S.filter = el.dataset.filter; renderFilters(); renderChanges(); };
  });

  let t = null;
  $('#search').oninput = (e) => {
    clearTimeout(t);
    t = setTimeout(() => { S.search = e.target.value; renderChanges(); }, 160);
  };

  $('#btn-check-recommend').onclick = () => {
    S.selected = new Set(S.changes.filter(c => c.type !== 'unchanged' && c.checked).map(selKey));
    renderChanges(); renderStatus();
    toast('已恢复推荐勾选', 'ok');
  };

  $('#btn-export').onclick = exportChangelog;

  $('#btn-back-2').onclick = () => goStep(2);
  $('#btn-apply').onclick = doApply;
}

/* ================= 导出变更清单 ================= */

function exportChangelog() {
  if (!S.changes.length) { toast('还没有对比结果，先对比一次', 'warn'); return; }
  const p = S.pack, t = S.target, s = S.summary;
  const L = [];
  L.push('我的世界更新助手 — 变更清单');
  L.push('生成时间：' + new Date().toLocaleString('zh-CN'));
  L.push('');
  L.push('整合包：' + (p ? (p.packName || '') + (p.packVersion ? ' ' + p.packVersion : '') : '未知'));
  if (p && p.gameVersion) L.push('游戏版本：' + p.gameVersion + (p.loader ? ' / ' + p.loader + (p.loaderVersion ? ' ' + p.loaderVersion : '') : ''));
  L.push('目标版本：' + (t ? t.name : '未知') + (t && t.contentDir ? '\n目录：' + t.contentDir : ''));
  L.push('');
  L.push('统计：新增 ' + (s.added || 0) + ' / 更新 ' + (s.updated || 0) + ' / 移除 ' + (s.removed || 0) + ' / 无变化 ' + (s.unchanged || 0));
  if (S.conflicts.length) L.push('重复模组冲突：' + S.conflicts.length + ' 处（详见下方）');
  L.push('');
  L.push('勾选图例：✓ 已勾选（将应用）  ○ 未勾选（不处理）');
  L.push('');
  L.push('='.repeat(60));

  const group = (type, label) => {
    const items = S.changes.filter(c => c.type === type);
    if (!items.length) return;
    L.push('');
    L.push('【' + label + '】' + items.length + ' 项');
    items.forEach(c => {
      let line = '  ' + (c.checked ? '✓' : '○') + '  ' + c.displayName;
      if (c.fileName && c.fileName !== c.displayName) line += '  (' + c.fileName + ')';
      if (c.type === 'updated') line += '   v' + (c.oldVersion || '?') + ' → v' + (c.newVersion || '?');
      else if (c.oldVersion) line += '   v' + c.oldVersion;
      else if (c.newVersion) line += '   v' + c.newVersion;
      const marks = [];
      if (c.duplicate) marks.push('重复·保留 ' + c.duplicateOf);
      if (c.playerInstalled) marks.push('你装的');
      if (c.downgrade) marks.push('降级');
      if (c.disabled) marks.push('已禁用');
      if (marks.length) line += '   [' + marks.join('，') + ']';
      L.push(line);
    });
  };
  group('updated', '更新');
  group('added', '新增');
  group('removed', '移除');

  if (S.conflicts.length) {
    L.push('');
    L.push('='.repeat(60));
    L.push('【重复模组冲突】' + S.conflicts.length + ' 处');
    S.conflicts.forEach(c => {
      const where = c.side === 'zip' ? '整合包' : (c.side === 'local' ? '本地' : '整合包和本地');
      L.push('  ' + c.displayName + '（' + where + '有 ' + c.files.length + ' 份）');
      c.files.forEach(f => L.push('      ' + f.fileName + (f.version ? ' v' + f.version : '')));
    });
  }

  L.push('');
  L.push('='.repeat(60));
  L.push('提示：存档、截图、地图、按键设置等个人数据不受本次更新影响');

  const blob = new Blob([L.join('\r\n')], { type: 'text/plain;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  const d = new Date();
  const pad = (n) => String(n).padStart(2, '0');
  a.download = '更新清单_' + (p && p.packName ? p.packName : 'modpack') + '_' +
    d.getFullYear() + pad(d.getMonth() + 1) + pad(d.getDate()) + '.txt';
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
  toast('清单已导出到下载文件夹', 'ok');
}

/* ================= 应用 ================= */

async function doApply(force = false) {
  const chosen = S.changes.filter(c => S.selected.has(selKey(c)));
  const removes = chosen.filter(c => c.type === 'removed');
  const playerRemoves = removes.filter(c => c.playerInstalled === true);

  if (force !== true) {
    const n = { added: 0, updated: 0, removed: 0 };
    chosen.forEach(c => n[c.type]++);
    const ok = await confirm('确认应用更新？', `
      <p>即将对 <code>${esc(S.target.contentDir)}</code> 做这些改动：</p>
      <ul style="margin:10px 0 0 20px;line-height:1.9">
        <li>新增 <b>${n.added}</b> 个文件</li>
        <li>更新 <b>${n.updated}</b> 个文件</li>
        <li>删除 <b>${n.removed}</b> 个文件</li>
      </ul>
      ${playerRemoves.length ? notice('warn', `其中 ${playerRemoves.length} 个是你自己装的模组`,
        '<ul>' + playerRemoves.slice(0, 8).map(c => `<li>${esc(c.displayName)}</li>`).join('') +
        (playerRemoves.length > 8 ? `<li class="muted">…还有 ${playerRemoves.length - 8} 个</li>` : '') + '</ul>') : ''}
      <p class="muted small" style="margin-top:12px">
        动手前会自动备份，出问题可以一键回滚。存档和个人设置不受影响。</p>
    `, '开始更新');
    if (!ok) return;
  }

  overlay.open('正在更新', { indeterminate: true, msg: '正在建立连接…' });
  try {
    const r = await API.apply([...S.selected], force === true, (e) => {
      if (e.event === 'progress') overlay.progress(e.msg, e.pct, e.detail);
    });
    S.lastResult = r.result;
    renderDone(r.result);
    goStep(4);
  } catch (e) {
    if (e.code === 'MC_RUNNING') {
      const go = await confirm('游戏好像正在运行', `
        <p>${esc(e.message)}</p>
        <p class="muted small" style="margin-top:10px">
          如果你确定游戏已经关了（有时候进程会残留几秒），可以强制继续。
          但游戏真在运行时强制更新，多半会因为文件被占用而失败。</p>`,
        '强制继续', 'danger');
      if (go) return doApply(true);
    } else {
      // P1-9: 据 rolledBack 据实提示，避免回滚失败时误导玩家
      let tip;
      if (e.rolledBack === false) {
        tip = `<p style="margin-top:10px;color:var(--amber)">⚠ 回滚未能完全成功，部分文件可能已被改动。
               请检查游戏目录，必要时从「备份」文件夹手动恢复。</p>`;
      } else if (e.rolledBack === true) {
        tip = `<p class="muted small" style="margin-top:10px">已自动回滚到操作前的状态，你的文件是安全的。</p>`;
      } else {
        tip = `<p class="muted small" style="margin-top:10px">不确定是否已完整回滚，请检查游戏目录确认文件状态。</p>`;
      }
      await modal({ title: '更新失败', body: `<p>${esc(e.message)}</p>${tip}` });
    }
  } finally {
    overlay.close();
  }
}

/* ================= 步骤 4：完成 ================= */

function renderDone(r) {
  const c = r.counts || {};
  $('#done-card').innerHTML = `
    <div class="done-mark">✓</div>
    <h2>更新完成</h2>
    <p class="muted">${esc(S.target.name)} 已经和整合包保持一致，可以启动游戏了</p>
    <div class="done-stats">
      <div class="done-stat"><div class="n" style="color:var(--green)">${c.added || 0}</div><div class="l">新增</div></div>
      <div class="done-stat"><div class="n" style="color:var(--blue)">${c.updated || 0}</div><div class="l">更新</div></div>
      <div class="done-stat"><div class="n" style="color:var(--red)">${c.removed || 0}</div><div class="l">移除</div></div>
      <div class="done-stat"><div class="n">${((r.elapsedMs || 0) / 1000).toFixed(1)}s</div><div class="l">耗时</div></div>
    </div>
    ${r.failures && r.failures.length ? notice('warn', `${r.failures.length} 项没能处理`,
      '<ul>' + r.failures.slice(0, 6).map(f => `<li>${esc(f.name || f.displayName || f)}</li>`).join('') + '</ul>') : ''}
    ${r.backup ? `<p class="muted small">已备份到 <code>${esc(r.backup)}</code>，不满意可以随时回滚</p>` : ''}
    <div class="done-actions">
      <button class="btn btn-ghost" id="btn-open-dir">打开游戏文件夹</button>
      <button class="btn btn-ghost" id="btn-goto-backups">回滚</button>
      <button class="btn btn-primary" id="btn-restart">再更新一个</button>
    </div>`;

  $('#btn-open-dir').onclick = () => API.reveal(S.target.contentDir).catch(() => {});
  $('#btn-goto-backups').onclick = openBackups;
  $('#btn-restart').onclick = async () => {
    await API.reset().catch(() => {});
    S.pack = null; S.target = null; S.changes = []; S.selected.clear(); S.scope.clear();
    $('#input-pack-path').value = '';
    $('#pack-hint').textContent = '';
    goStep(1);
  };
}

/* ================= 备份抽屉 ================= */

async function openBackups() {
  const d = $('#drawer-backups');
  d.classList.remove('hidden');
  const body = $('#backup-body');
  body.innerHTML = '<p class="muted">正在读取…</p>';

  if (!S.target) { body.innerHTML = '<p class="muted">还没有选择目标版本。</p>'; return; }

  try {
    const r = await API.backups(S.target.contentDir);
    if (!r.backups.length) {
      body.innerHTML = '<p class="muted">还没有任何备份。第一次应用更新时会自动创建。</p>';
      return;
    }
    body.innerHTML = `<p class="muted small" style="margin-bottom:12px">
      只保留最近 3 份备份。回滚只会还原被管理的目录，不影响存档。</p>` +
      r.backups.map(b => `
        <div class="backup-item">
          <h4>${esc(b.createdAt ? new Date(b.createdAt).toLocaleString('zh-CN') : b.name)}</h4>
          <div class="backup-meta">
            ${esc(b.packName ? '更新前：' + b.packName + (b.packVersion ? ' ' + b.packVersion : '') : b.name)}
            ${b.categories && b.categories.length ? ' · ' + b.categories.map(k => esc(CAT_LABEL[k] || k)).join('、') : ''}
            ${b.files ? ' · ' + b.files + ' 个文件' : ''}
            ${b.sizeText ? ' · ' + esc(b.sizeText) : ''}
          </div>
          <button class="btn btn-ghost" data-rollback="${esc(b.name)}">回滚到这里</button>
        </div>`).join('');

    body.querySelectorAll('[data-rollback]').forEach(btn => {
      btn.onclick = async () => {
        const name = btn.dataset.rollback;
        const ok = await confirm('确认回滚？',
          `<p>会把被管理的目录还原到 <code>${esc(name)}</code> 时的状态，之后的改动会丢失。</p>
           <p class="muted small" style="margin-top:8px">存档、截图、地图等个人数据不受影响。</p>`,
          '回滚', 'danger');
        if (!ok) return;

        d.classList.add('hidden');
        overlay.open('正在回滚', { indeterminate: true, msg: '正在建立连接…' });
        try {
          await API.rollback(name, S.target.contentDir, (e) => {
            if (e.event === 'progress') overlay.progress(e.msg, e.pct);
          });
          toast('回滚完成', 'ok');
          S.changes = []; S.selected.clear();
          goStep(2);
        } catch (e) {
          toast(e.message, 'err', 6000);
        } finally {
          overlay.close();
        }
      };
    });
  } catch (e) {
    body.innerHTML = `<p class="muted">${esc(e.message)}</p>`;
  }
}

/* ================= 日志抽屉 ================= */

function initLogs() {
  const body = $('#log-body');
  const LV = { info: '信息', success: '成功', warn: '警告', error: '错误', step: '步骤' };
  API.subscribeLogs((d) => {
    const line = document.createElement('div');
    line.className = 'log-line ' + d.level;
    line.innerHTML = `<span class="log-time">${esc(d.time)}</span>
      <span class="log-lv">${LV[d.level] || d.level}</span>
      <span class="log-msg">${esc(d.message)}</span>`;
    body.appendChild(line);
    const stick = body.scrollTop + body.clientHeight >= body.scrollHeight - 40;
    while (body.children.length > 600) body.removeChild(body.firstChild);
    if (stick) body.scrollTop = body.scrollHeight;
  });
}

/* ================= 启动 ================= */

async function init() {
  UI.init();
  initLogs();
  bindStep1();
  bindStep2();
  bindStep3();

  $('#btn-logs').onclick = () => $('#drawer-logs').classList.toggle('hidden');
  $('#btn-close-logs').onclick = () => $('#drawer-logs').classList.add('hidden');
  $('#btn-backups').onclick = openBackups;
  $('#btn-close-backups').onclick = () => $('#drawer-backups').classList.add('hidden');

  try {
    const st = await API.state();
    S.app = st.app;
    S.categories = st.categories;
    st.categories.forEach(c => { CAT_LABEL[c.key] = c.label; });
    document.title = `${st.app.name} v${st.app.version}`;

    // 后端还留着上次的会话（比如刷新了页面），直接恢复
    if (st.pack) {
      S.pack = st.pack;
      S.target = st.target;
      renderStep2(!!st.target);
      goStep(2);
    }
  } catch (e) {
    toast('连不上本地服务：' + e.message, 'err', 8000);
  }

  // 后台预热探测，用户还在看第一屏时就把版本列表准备好
  API.env().catch(() => {});
}

document.addEventListener('DOMContentLoaded', init);
})();
