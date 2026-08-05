/* ================================================================
   UI 小工具：DOM 选择、转义、吐司、弹窗、遮罩进度
   ================================================================ */

const UI = (() => {

  const $  = (sel, root = document) => root.querySelector(sel);
  const $$ = (sel, root = document) => Array.from(root.querySelectorAll(sel));

  /** 变更列表里全是用户数据（mod 名、文件名），必须转义 */
  function esc(s) {
    return String(s == null ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function bytes(n) {
    if (!n && n !== 0) return '—';
    const u = ['B', 'KB', 'MB', 'GB'];
    let i = 0, v = n;
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
    return (i === 0 ? v : v.toFixed(v < 10 ? 1 : 0)) + ' ' + u[i];
  }

  /** 路径太长时中间省略，头尾都保留（尾巴是版本名，最有用） */
  function shortPath(p, max = 58) {
    p = String(p || '');
    if (p.length <= max) return p;
    const keepTail = Math.floor(max * 0.6);
    return p.slice(0, max - keepTail - 1) + '…' + p.slice(-keepTail);
  }

  /* ---------------- 吐司 ---------------- */

  function toast(msg, kind = '', ms = 3200) {
    const box = $('#toasts');
    const el = document.createElement('div');
    el.className = 'toast ' + kind;
    el.textContent = msg;
    box.appendChild(el);
    setTimeout(() => {
      el.style.transition = 'opacity .25s, transform .25s';
      el.style.opacity = '0';
      el.style.transform = 'translateX(16px)';
      setTimeout(() => el.remove(), 260);
    }, ms);
  }

  /* ---------------- 遮罩进度 ---------------- */

  const overlay = {
    open(title, opts) {
      opts = opts || {};
      const ov = $('#overlay');
      $('#ov-title').textContent = title || '处理中…';
      $('#ov-msg').textContent = opts.msg || '请稍候…';
      $('#ov-bar').style.width = '0%';
      $('#ov-pct').textContent = '0%';
      $('#ov-feed').innerHTML = '';
      ov.classList.remove('hidden');
      // 拿不到真实细分进度时（如普通 POST 的选包），用持续滑动的不确定进度，
      // 比卡在一个假百分比更诚实、更不让人焦虑
      ov.classList.toggle('is-loading', !!opts.indeterminate);
    },
    progress(msg, pct, detail) {
      // 收到真实进度帧 → 退出不确定模式，切回百分比
      $('#overlay').classList.remove('is-loading');
      if (msg) $('#ov-msg').textContent = msg;
      if (typeof pct === 'number') {
        const clamped = Math.max(0, Math.min(100, pct));
        $('#ov-bar').style.width = clamped + '%';
        $('#ov-pct').textContent = Math.round(clamped) + '%';
      }
      if (detail && detail.name) {
        const feed = $('#ov-feed');
        const li = document.createElement('li');
        li.className = detail.type || '';
        const verb = { added: '＋', updated: '↻', removed: '－' }[detail.type] || '·';
        li.textContent = `${verb} ${detail.name}`;
        feed.appendChild(li);
        feed.scrollTop = feed.scrollHeight;
        // 别让 DOM 无限长，只留最近 200 条
        while (feed.children.length > 200) feed.removeChild(feed.firstChild);
      }
    },
    /** 显式切换不确定进度（开 overlay 时也可通过 open(...,{indeterminate:true}) 开启） */
    indeterminate(on) {
      $('#overlay').classList.toggle('is-loading', !!on);
    },
    close() {
      const ov = $('#overlay');
      ov.classList.add('hidden');
      ov.classList.remove('is-loading');
    },
  };

  /* ---------------- 弹窗 ---------------- */

  let modalResolve = null;

  function closeModal(value) {
    $('#modal').classList.add('hidden');
    if (modalResolve) { const r = modalResolve; modalResolve = null; r(value); }
  }

  /**
   * 通用弹窗
   * @param {object} o {title, body(html), actions:[{label,value,kind,primary}], dismissable}
   * @returns {Promise<any>} 点击的按钮 value，点遮罩关闭为 null
   */
  function modal(o) {
    $('#modal-title').textContent = o.title || '';
    $('#modal-body').innerHTML = o.body || '';
    const bar = $('#modal-actions');
    bar.innerHTML = '';
    for (const a of (o.actions || [{ label: '知道了', value: true, primary: true }])) {
      const b = document.createElement('button');
      b.className = 'btn ' + (a.kind ? 'btn-' + a.kind : (a.primary ? 'btn-primary' : 'btn-ghost'));
      b.textContent = a.label;
      b.onclick = () => closeModal(a.value);
      bar.appendChild(b);
    }
    $('#modal').classList.remove('hidden');
    return new Promise((res) => { modalResolve = res; });
  }

  function confirm(title, bodyHtml, okLabel = '确定', kind = 'primary') {
    return modal({
      title,
      body: bodyHtml,
      actions: [
        { label: '取消', value: false },
        { label: okLabel, value: true, kind: kind === 'danger' ? 'danger' : 'primary' },
      ],
    });
  }

  /* ---------------- 提示条 ---------------- */

  function notice(kind, title, html) {
    const icon = { warn: '⚠', danger: '⛔', info: 'ℹ' }[kind] || 'ℹ';
    return `<div class="notice notice-${kind}">
      <div class="notice-icon">${icon}</div>
      <div class="notice-body"><strong>${esc(title)}</strong>${html || ''}</div>
    </div>`;
  }

  /* ---------------- 初始化全局交互 ---------------- */

  function init() {
    // 点遮罩关闭弹窗
    $('#modal').addEventListener('click', (e) => {
      if (e.target.id === 'modal') closeModal(null);
    });
    document.addEventListener('keydown', (e) => {
      if (e.key !== 'Escape') return;
      if (!$('#modal').classList.contains('hidden')) closeModal(null);
      else $$('.drawer').forEach(d => d.classList.add('hidden'));
    });
  }

  return { $, $$, esc, bytes, shortPath, toast, overlay, modal, confirm, closeModal, notice, init };
})();
