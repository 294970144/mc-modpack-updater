'use strict';
const { detectContentRoots, parseSetupIni } = require('../src/core/modpack-reader');

// 合成一批条目（只需 name 字段，detectContentRoots 只看 e.name）
const names = [
  // 真实 PCL2 结构：overrides 是内容根
  'overrides/mods/a.jar', 'overrides/config/b.toml', 'overrides/shaderpacks/c.zip',
  // 干扰：.minecraft/versions/1.21.11 也有全套
  '.minecraft/versions/1.21.11/mods/x.jar',
  '.minecraft/versions/1.21.11/config/y.toml',
  // 干扰：versions/1.21.11 只有 mods
  'versions/1.21.11/mods/z.jar',
  // 干扰：另一个 overrides 层
  'nested/overrides/mods/n.jar',
  // 只有 shaderpacks（无 mods，应低分）
  'weird/shaderpacks/s.zip',
  // 顶层散落
  'mods/top.jar',
  'config/top.toml',
  // 深度遍历
  'a/b/c/mods/deep.jar',
  'a/b/mods/mid.jar',
  // 只有文件没有目录层级
  'lonely.jar',
  'a/b/c/d.txt',
  // managed 目录出现在中间位置
  'overrides/config/sub/config/inner.toml',
];

const entries = names.map(n => ({ name: n }));
const r = detectContentRoots(entries);
console.log(JSON.stringify(r, null, 2));
