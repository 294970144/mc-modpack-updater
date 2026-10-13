'use strict';
/**
 * 把 tools/real-baseline.json 里的 mods 顺序基准，
 * 自动誊进 RealDiffTests.cs 的 ExpectedOrder 数组。
 *
 * 【为什么要自动生成而不是手抄】106 条基准手抄必然出错（本次已实测：
 * 手写时把 7.3.2 写成 7.3.3、给 entityculling 多加了 -fabric 后缀）。
 * 这类错误会让测试变成"验证错误期望"，比没有测试更糟。
 *
 * 用法：node tools/sync-real-baseline.js
 */
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const baseline = JSON.parse(fs.readFileSync(path.join(ROOT, 'tools', 'real-baseline.json'), 'utf8'));
const testFile = path.join(ROOT, 'winui', 'tests', 'McUpdater.Tests', 'RealDiffTests.cs');

/** 转成合法C# 字符串字面量（只需处理反斜杠与双引号） */
function csString(s) {
  return '"' + s.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"';
}

const order = baseline.mods.order;
const lines = order.map((x, i) => '        ' + csString(x) + (i < order.length - 1 ? ',' : ''));

const arr = [
  '    private static readonly string[] ExpectedOrder =',
  '    [',
  ...lines,
  '    ];',
].join('\n');

let src = fs.readFileSync(testFile, 'utf8');
const start = src.indexOf('    private static readonly string[] ExpectedOrder =');
if (start < 0) {
  process.stderr.write('定位 ExpectedOrder 失败\n');
  process.exit(1);
}
const endMark = '    ];';
const endIdx = src.indexOf(endMark, start);
if (endIdx < 0) {
  process.stderr.write('定位数组结尾失败\n');
  process.exit(1);
}
const end = endIdx + endMark.length;

src = src.slice(0, start) + arr + src.slice(end);
fs.writeFileSync(testFile, src);
process.stderr.write(`已写入 ${order.length} 条基准到 RealDiffTests.cs\n`);