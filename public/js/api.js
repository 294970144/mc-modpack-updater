/* ================================================================
   与后端通信
   普通接口用 JSON，耗时操作（对比 / 应用 / 回滚）走 SSE 流。
   EventSource 只能发 GET，所以这里用 fetch + ReadableStream 自己解析 SSE，
   这样 POST 也能拿到实时进度。
   ================================================================ */

const API = (() => {

  async function json(method, url, body) {
    const res = await fetch(url, {
      method,
      headers: body ? { 'Content-Type': 'application/json' } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    });
    let data;
    try { data = await res.json(); }
    catch (_) { throw new Error(`服务返回了非法内容（HTTP ${res.status}）`); }
    if (!data.success) throw new Error((data.error && data.error.message) || '请求失败');
    return data.data;
  }

  const get  = (url)       => json('GET', url);
  const post = (url, body) => json('POST', url, body);

  /**
   * 发起一个 SSE 流式请求
   * @param {string} url
   * @param {object} body
   * @param {(evt:object)=>void} onEvent  每收到一帧调用一次
   * @returns {Promise<object>} 解析为 done 帧，error 帧则 reject
   */
  async function stream(url, body, onEvent) {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body || {}),
    });

    // 后端在前置校验失败时会直接回 JSON 而不是 SSE
    const ct = res.headers.get('Content-Type') || '';
    if (!ct.includes('text/event-stream')) {
      const data = await res.json().catch(() => null);
      const msg = (data && data.error && data.error.message) || `请求失败（HTTP ${res.status}）`;
      throw new Error(msg);
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder('utf-8');
    let buf = '';
    let result = null;
    let failure = null;

    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      buf += decoder.decode(value, { stream: true });

      // SSE 以空行分帧
      let idx;
      while ((idx = buf.indexOf('\n\n')) !== -1) {
        const frame = buf.slice(0, idx);
        buf = buf.slice(idx + 2);

        for (const line of frame.split('\n')) {
          if (!line.startsWith('data:')) continue;   // 注释帧 / 心跳，跳过
          let evt;
          try { evt = JSON.parse(line.slice(5).trim()); }
          catch (_) { continue; }

          if (evt.event === 'done')  result = evt;
          if (evt.event === 'error') failure = evt;
          try { onEvent && onEvent(evt); } catch (_) { /* 回调自己的错不影响流 */ }
        }
      }
    }

    if (failure) {
      const e = new Error(failure.message || '操作失败');
      e.code = failure.code;
      e.detail = failure.detail;
      e.rolledBack = failure.rolledBack;
      throw e;
    }
    if (!result) throw new Error('连接中断，操作可能没有完成');
    return result;
  }

  /** 订阅后端日志（长连接，断了自动重连） */
  function subscribeLogs(onLog) {
    let es = null;
    let closed = false;

    const connect = () => {
      if (closed) return;
      es = new EventSource('/api/logs');
      es.onmessage = (e) => {
        try {
          const d = JSON.parse(e.data);
          if (d.event === 'log') onLog(d);
        } catch (_) { /* ignore */ }
      };
      es.onerror = () => {
        es.close();
        if (!closed) setTimeout(connect, 2000);
      };
    };
    connect();

    return () => { closed = true; if (es) es.close(); };
  }

  /**
   * 上传整合包文件（拖放 / 原生对话框选的 File）
   * @param {File} file
   * @param {(loaded:number, total:number)=>void} onProgress
   * @returns {Promise<{path:string, fileName:string, size:number}>}
   */
  function uploadPack(file, onProgress) {
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      const url = '/api/upload-pack?name=' + encodeURIComponent(file.name);
      xhr.open('POST', url);

      xhr.upload.onprogress = (e) => {
        if (e.lengthComputable && onProgress) onProgress(e.loaded, e.total);
      };

      xhr.onload = () => {
        let data;
        try { data = JSON.parse(xhr.responseText); }
        catch (_) { reject(new Error(`服务返回了非法内容（HTTP ${xhr.status}）`)); return; }
        if (!data.success) {
          reject(new Error((data.error && data.error.message) || '上传失败'));
          return;
        }
        resolve(data.data);
      };

      xhr.onerror = () => reject(new Error('网络错误，上传中断'));
      xhr.onabort = () => reject(new Error('上传已取消'));

      // raw body = 文件字节，Content-Type 用 octet-stream
      xhr.setRequestHeader('Content-Type', 'application/octet-stream');
      xhr.send(file);
    });
  }

  return {
    state:       ()            => get('/api/state'),
    env:         (refresh)     => get('/api/env' + (refresh ? '?refresh=1' : '')),
    pickPack:    ()            => post('/api/pick/pack'),
    pickFolder:  (initial)     => post('/api/pick/folder', { initial }),
    reveal:      (p)           => post('/api/reveal', { path: p }),
    openPack:    (p)           => post('/api/pack', { path: p }),
    uploadPack:  (file, cb)    => uploadPack(file, cb),
    setTarget:   (payload)     => post('/api/target', payload),
    backups:     (dir)         => get('/api/backups' + (dir ? '?dir=' + encodeURIComponent(dir) : '')),
    reset:       ()            => post('/api/reset'),

    diff:     (categories, onEvent)  => stream('/api/diff', { categories }, onEvent),
    apply:    (keys, force, onEvent) => stream('/api/apply', { keys, force }, onEvent),
    rollback: (name, dir, onEvent)   => stream('/api/rollback', { name, dir }, onEvent),

    subscribeLogs,
  };
})();
