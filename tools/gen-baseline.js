'use strict';
/**
 * Node 基准生成器：把原版函数的输出打印出来，供 C# 测试对拍。
 * 用法：node tools/gen-baseline.js
 */
const { parseSetupIni, detectContentRoots } = require('../src/core/modpack-reader');
const { compareVersion, scanLocalMods } = require('../src/core/diff-engine');

const out = {};

// ---- parseSetupIni ----
out.setupIni = [
  parseSetupIni('; 注释行\r\n# 井号注释\r\n\r\nVersionVanillaName: 1.21.11\r\n  VersionFabric : 0.16.10 \r\nVersionArgumentIndie: 1\r\n没有冒号的行\r\n: 只有冒号\r\n'),
  parseSetupIni(''),
  parseSetupIni('裸CR\r行:值\r'),
];

// ---- compareVersion ----
const cmpCases = [
  ['1.0.0', '1.0.0'], ['1.0.1', '1.0.0'], ['1.0.0', '1.0.1'],
  ['0.8.12', '0.8.13'], ['0.8.13', '0.8.12'],
  ['1.0.0', '2.0.0'], ['2.0.0', '1.0.0'],
  // parseInt 陷阱：'12abc' -> 12，不是 NaN
  ['1.0.12abc', '1.0.12'], ['1.0.12', '1.0.12abc'],
  ['1.0.0-beta', '1.0.0'], ['1.0.0', '1.0.0-beta'],
  ['fabric-0.16.10', '0.16.10'],
  ['1.0.0+build.7', '1.0.0'], ['1.0.0', '1.0.0+build.7'],
  ['1.0.0_alpha', '1.0.0'], ['1.0.0', '1.0.0_alpha'],
  ['1.20.1', '1.21.11'], ['1.21.11', '1.20.1'],
  ['', ''], ['', '1.0.0'], ['1.0.0', ''],
  ['v1.2.3', '1.2.3'], ['1.2.3', 'v1.2.3'],
  ['1.0.0.0.0.1', '1.0.0.0.0.2'],
  ['1.0.0', '1.0.0.0.1'],       // 越界段 = NaN = -1
  ['1.0.0.0.0.1', '1.0.0'],
  ['12', '12.0'], ['12.0', '12'],
];
out.compareVersion = cmpCases.map(([a, b]) => ({ a, b, r: compareVersion(a, b) }));

console.log(JSON.stringify(out, null, 2));