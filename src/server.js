'use strict';

/**
 * 本地 HTTP 服务
 *
 * 只监听 127.0.0.1，不对外暴露。启动后自动开浏览器。
 * 打包成单文件 exe 时，public/ 下的静态资源会被 build.js 内联进来
 * （挂在 global.__MCU_ASSETS__），所以这里两种取法都要支持。
 */

const http = require('http');
const fs = require('fs');
const path = require('path');
const { execFile } = require('child_process');

const config = require('./config');
const logger = require('./utils/logger');
const { sendJson, error } = require('./utils/response');
const { handleApi, closePack } = require('./router');

/* ------------------------------------------------------------------ */
/* 静态资源                                                            */
/* ------------------------------------------------------------------ */

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
  '.ttf': 'font/ttf',
};

/** 打包后由 build.js 注入：{ 'index.html': '<base64>', ... } */
const BUNDLED = (typeof global !== 'undefined' && global.__MCU_ASSETS__) || null;

function readAsset(rel) {
  if (BUNDLED) {
    const hit = BUNDLED[rel];
    return hit === undefined ? null : Buffer.from(hit, 'base64');
  }
  const full = path.join(config.PUBLIC_DIR, rel);
  // 别让 ../ 跑出 public 目录（startsWith 必须带分隔符，否则 public-evil 也会命中 public 前缀）
  const root = path.resolve(config.PUBLIC_DIR);
  const resolved = path.resolve(full);
  if (resolved !== root && !resolved.startsWith(root + path.sep)) return null;
  try {
    if (!fs.existsSync(full) || fs.statSync(full).isDirectory()) return null;
    return fs.readFileSync(full);
  } catch (_) {
    return null;
  }
}

function serveStatic(req, res, pathname) {
  let rel = decodeURIComponent(pathname.replace(/^\/+/, ''));
  if (rel === '' || rel.endsWith('/')) rel += 'index.html';

  const buf = readAsset(rel);
  if (buf === null) {
    res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
    res.end('404 找不到: ' + rel);
    return;
  }

  res.writeHead(200, {
    'Content-Type': MIME[path.extname(rel).toLowerCase()] || 'application/octet-stream',
    'Content-Length': buf.length,
    // 本地工具，改了代码刷新就要生效，别缓存
    'Cache-Control': 'no-store',
  });
  res.end(buf);
}

/* ------------------------------------------------------------------ */
/* 浏览器                                                              */
/* ------------------------------------------------------------------ */

function openBrowser(url) {
  // start 是 cmd 内建命令；第一个引号参数会被当成窗口标题，所以要留个空的
  execFile('cmd', ['/c', 'start', '', url], { windowsHide: true }, (err) => {
    if (err) logger.warn(`没能自动打开浏览器，请手动访问 ${url}`);
  });
}

/* ------------------------------------------------------------------ */
/* 启动                                                                */
/* ------------------------------------------------------------------ */

function createServer() {
  return http.createServer(async (req, res) => {
    let url;
    try {
      url = new URL(req.url, `http://${config.HOST}`);
    } catch (_) {
      res.writeHead(400); res.end('bad url'); return;
    }

    // 只服务本机来的请求
    const ra = req.socket.remoteAddress || '';
    if (!ra.includes('127.0.0.1') && !ra.includes('::1')) {
      res.writeHead(403); res.end('forbidden'); return;
    }

    // P0-3 CSRF 防御：只接受本机来源的 API 请求。
    // 恶意网页（https://evil.com）发起的 fetch 到 127.0.0.1，TCP 来源仍是本机，
    // 单靠 remoteAddress 挡不住；必须校验 Origin/Referer。
    if (url.pathname.startsWith('/api/')) {
      const isLocalOrigin = (o) => !o || o === 'null'
        || /^https?:\/\/(127\.0\.0\.1|localhost|\[::1\])(:\d+)?(\/|$)/i.test(o);
      const origin = req.headers.origin || '';
      const referer = req.headers.referer || '';
      if (!isLocalOrigin(origin) || (referer && !isLocalOrigin(referer))) {
        res.writeHead(403); res.end('forbidden origin'); return;
      }
    }

    if (req.method === 'OPTIONS') {
      // 不再返回 ACAO:* —— 同源请求不需要 CORS，跨源请求一律拒
      res.writeHead(204, {
        'Access-Control-Allow-Methods': 'GET,POST,OPTIONS',
        'Access-Control-Allow-Headers': 'Content-Type',
      });
      res.end();
      return;
    }

    if (url.pathname.startsWith('/api/')) {
      try {
        await handleApi(req, res, url);
      } catch (e) {
        if (!res.headersSent) sendJson(res, 500, error('INTERNAL', e.message));
        else if (!res.writableEnded) res.end();
      }
      return;
    }

    serveStatic(req, res, url.pathname);
  });
}

/** 端口被占就往后顺延，最多试 20 个 */
function listen(server, port, host, tries = 20) {
  return new Promise((resolve, reject) => {
    const attempt = (p, left) => {
      const onError = (e) => {
        server.removeListener('error', onError);
        if (e.code === 'EADDRINUSE' && left > 0) {
          logger.warn(`端口 ${p} 被占用，换 ${p + 1} 试试`);
          attempt(p + 1, left - 1);
        } else {
          reject(e);
        }
      };
      server.once('error', onError);
      server.listen(p, host, () => {
        server.removeListener('error', onError);
        resolve(p);
      });
    };
    attempt(port, tries);
  });
}

async function main() {
  const args = process.argv.slice(2);
  // 双击启动（无参数）默认开浏览器；--no-open / -n 抑制
  const shouldOpen = !args.includes('--no-open') && !args.includes('-n');
  const portArg = args.find(a => a.startsWith('--port='));
  const basePort = portArg ? parseInt(portArg.split('=')[1], 10) : config.PORT;

  const server = createServer();
  // SSE 长连接不能被超时干掉
  server.keepAliveTimeout = 0;
  server.headersTimeout = 0;
  server.requestTimeout = 0;

  let port;
  try {
    port = await listen(server, basePort, config.HOST);
  } catch (e) {
    logger.error(`服务启动失败: ${e.message}`);
    process.exit(1);
  }

  const url = `http://${config.HOST}:${port}/`;
  console.log('');
  console.log(`  ${config.APP_NAME} v${config.APP_VERSION}`);
  console.log(`  已启动 → ${url}`);
  console.log(`  关掉这个黑窗口就能退出程序`);
  console.log('');

  if (shouldOpen) setTimeout(() => openBrowser(url), 300);

  const bye = (sig) => {
    logger.info(`收到 ${sig}，正在退出…`);
    closePack();                       // 释放整合包的文件句柄和临时文件
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 1500).unref();
  };
  process.on('SIGINT', () => bye('SIGINT'));
  process.on('SIGTERM', () => bye('SIGTERM'));
  process.on('uncaughtException', (e) => {
    logger.error(`未捕获异常: ${e.stack || e.message}`);
  });
  process.on('unhandledRejection', (e) => {
    logger.error(`未处理的 Promise 异常: ${(e && e.stack) || e}`);
  });

  return { server, port, url };
}

if (require.main === module) {
  main();
}

module.exports = { main, createServer, openBrowser };
