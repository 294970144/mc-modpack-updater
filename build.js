'use strict';

/**
 * 打包：Node SEA 单文件 exe
 *
 * 做的事：
 *   1. esbuild 把 src/ 里所有文件打成单文件 dist/server.cjs
 *   2. 读取 public/ 下所有静态文件，Base64 内联为 global.__MCU_ASSETS__
 *   3. node --experimental-sea-config 生成 sea blob
 *   4. node 二进制 + blob + postject 合并成 .exe
 *
 * 前置条件：已经 npm install（装 esbuild + postject）
 */

const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const ROOT = __dirname;
const DIST = path.join(ROOT, 'dist');
const PUBLIC = path.join(ROOT, 'public');

/* ------------------------------------------------------------------ */
/* 1. 扫描 public/ 下所有文件，生成 __MCU_ASSETS__ 注入代码             */
/* ------------------------------------------------------------------ */

function walkAssets(dir, base = '') {
  const out = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      out.push(...walkAssets(path.join(dir, entry.name), base + entry.name + '/'));
    } else {
      out.push(base + entry.name);
    }
  }
  return out;
}

function buildAssetLoader() {
  const map = {};
  for (const rel of walkAssets(PUBLIC)) {
    const buf = fs.readFileSync(path.join(PUBLIC, rel));
    if (buf.length > 5 * 1024 * 1024) {
      console.warn(`[warn] 静态文件 ${rel} 超过 5MB，打包到 exe 里会让体积很大（${(buf.length / 1048576).toFixed(1)}MB）`);
    }
    map[rel] = buf.toString('base64');
  }
  return `global.__MCU_ASSETS__ = ${JSON.stringify(map)};\n`;
}

/* ------------------------------------------------------------------ */
/* 2. esbuild                                                   */
/* ------------------------------------------------------------------ */

function banner() {
  // 内联逻辑要和 server.js 里那块保持一致
  return buildAssetLoader();
}

function esbuild() {
  console.log('[1/4] esbuild 打包…');
  const esbuildPath = path.join(ROOT, 'node_modules', '.bin', 'esbuild');
  const src = path.join(ROOT, 'src', 'server.js');
  const out = path.join(DIST, 'server.cjs');

  if (!fs.existsSync(DIST)) fs.mkdirSync(DIST, { recursive: true });

  execSync(
    `"${esbuildPath}" "${src}" --bundle --platform=node --target=node22 --format=cjs --outfile="${out}" --minify`,
    { cwd: ROOT, stdio: 'inherit', windowsHide: true }
  );

  const raw = fs.readFileSync(out, 'utf8');
  fs.writeFileSync(out, banner() + raw);
  const sz = fs.statSync(out).size;
  console.log(`  => ${out}  (${(sz / 1048576).toFixed(2)} MB)`);
}

/* ------------------------------------------------------------------ */
/* 3. SEA blob                                                  */
/* ------------------------------------------------------------------ */

function seaConfig() {
  return {
    main: path.relative(ROOT, path.join(DIST, 'server.cjs')).replace(/\\/g, '/'),
    output: path.relative(ROOT, path.join(DIST, 'sea-prep.blob')).replace(/\\/g, '/'),
    disableExperimentalSEAWarning: true,
    useCodeCache: true,
  };
}

function generateBlob() {
  console.log('[2/4] 生成 SEA blob…');
  const cfgPath = path.join(DIST, 'sea-config.json');
  fs.writeFileSync(cfgPath, JSON.stringify(seaConfig(), null, 2));
  // 用跑 build.js 的同一个 node（process.execPath），避免 PATH 上 node 版本不一致
  execSync(`"${process.execPath}" --experimental-sea-config "${cfgPath}"`, {
    cwd: ROOT, stdio: 'inherit', windowsHide: true,
  });
}

/* ------------------------------------------------------------------ */
/* 4. 复制 node 二进制 + 注入 blob + 改名                            */
/* ------------------------------------------------------------------ */

function sleepSync(ms) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

function buildExe() {
  console.log('[3/4] 制作 EXE…');
  const nodeExe = process.execPath;
  const destExe = path.join(DIST, '我的世界更新助手.exe');

  // 每次构建用唯一临时名，避免残留的旧临时文件被 Defender / 句柄锁住
  const tmpExe = path.join(DIST, `_build_tmp_${process.pid}_${Date.now()}.exe`);
  if (fs.existsSync(tmpExe)) { try { fs.unlinkSync(tmpExe); } catch (_) { /* ignore */ } }

  const postjectPath = path.join(ROOT, 'node_modules', '.bin', 'postject');
  if (!fs.existsSync(postjectPath)) {
    console.error('  ✗ 找不到 postject，需要先 npm install');
    process.exit(1);
  }

  // 复制 + 注入 blob 作为一个整体重试：每次重试都重新复制干净的 node 二进制，
  // 避免上一次注入失败留下的半截文件污染下一次尝试（Defender 实时扫描会瞬时锁住新 exe）
  let injected = false;
  for (let i = 1; i <= 8 && !injected; i++) {
    try {
      if (fs.existsSync(tmpExe)) { try { fs.unlinkSync(tmpExe); } catch (_) {} }
      fs.copyFileSync(nodeExe, tmpExe);
      execSync(
        `"${postjectPath}" "${tmpExe}" NODE_SEA_BLOB "${path.join(DIST, 'sea-prep.blob')}" --sentinel-fuse NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2`,
        { cwd: ROOT, stdio: 'inherit', windowsHide: true }
      );
      injected = true;
    } catch (e) {
      if (i === 8) { console.error('  ✗ postject 注入失败（可能被 Defender 持续锁住）'); throw e; }
      console.warn(`  postject 第 ${i} 次失败，2s 后重试…`);
      sleepSync(2000);
    }
  }

  // 重命名覆盖目标 exe（Windows 上 rename 原子替换目标）
  for (let i = 1; i <= 8; i++) {
    try {
      if (fs.existsSync(destExe)) {
        try { fs.unlinkSync(destExe); }
        catch (_) {
          // 目标被「运行中的实例」锁住：运行中的 exe 可 rename 不可删/覆盖。
          // 先把它 rename 走（.bak），腾出 dest 名字；旧实例关闭后 .bak 可删。
          try { fs.renameSync(destExe, `${destExe}.bak-${Date.now()}`); } catch (__) { /* 真锁死，下面 rename 会再报 */ }
        }
      }
      fs.renameSync(tmpExe, destExe);
      break;
    } catch (e) {
      if (i === 8) { console.error('  ✗ 覆盖目标 exe 失败: ' + e.message); process.exit(1); }
      console.warn(`  rename 第 ${i} 次失败(${e.code})，2s 后重试…`);
      sleepSync(2000);
    }
  }

  // 清理可能残留的旧 .bak（运行中实例还占着的删不掉，会跳过）
  try {
    for (const f of fs.readdirSync(DIST)) {
      if (f.endsWith('.bak-') || /\.bak-\d+$/.test(f)) {
        try { fs.unlinkSync(path.join(DIST, f)); } catch (_) { /* 旧实例仍运行 */ }
      }
    }
  } catch (_) { /* ignore */ }

  const sz = fs.statSync(destExe).size;
  console.log(`  => ${destExe}  (${(sz / 1048576).toFixed(2)} MB)`);
}

/* ------------------------------------------------------------------ */
/* 5. win                                                       */
/* ------------------------------------------------------------------ */

function main() {
  console.log('MinecraftUpdater — Build\n');
  esbuild();
  generateBlob();
  buildExe();
  console.log('\n[4/4] 完成！');
  console.log(`  EXE: ${path.join(DIST, '我的世界更新助手.exe')}`);
  console.log(`  双击即可启动，浏览器自动打开。`);
}

main();
