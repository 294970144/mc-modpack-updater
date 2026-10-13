'use strict';
/**
 * XAML 注释里的双减号扫描器（WMC9997 前置拦截）。
 *
 * ============================ 存在理由 ============================
 * XML 注释内不允许出现连续两个减号，WinUI 的 XAML 编译器报
 *   WMC9997: XML 注释中不能包含"--"，并且"-"不能是最后一个字符
 * 而 CSS 里到处都是双减号 —— `var(--grass-dd)`、`.btn-ghost`、
 * `.tag-green`。本项目的主题注释习惯直接抄 CSS 类名，
 * 于是每写一段样式说明就有踩雷的机会。
 *
 * ============================ 为什么不用正则一行搞定 ============================
 * 踩过两次坑，两次都不是「规则没想清楚」，而是**扫描范围划错了**：
 *
 * 坑1：只做行级扫描（找含`<!--` 的行，看 `<!--` 与 `-->` 之间的正文）。
 *      盲区 = 多行注释块里除首行外的续行。而项目里大量注释是
 *      「首行是 `<!-- === 分节线 === -->`，后面跟着若干行中文说明」，
 *      违规的 CSS 类名恰恰出现在**续行**上。
 *
 * 坑 2：以为块级正则 `/<!--([\s\S]*?)-->/g` 就够了。
 *      仍漏 —— 因为项目里存在 `<!-- =====...===== -->` 这种
 *      **正文首字符紧邻 `-->`** 的写法，正则非贪婪匹配到的是
 *      `=====...= `（不含双减号），但 XAML 解析器按行判定，
 *      会把分节线那行的尾部算进去。
 *
 * ============================ 现在的判据 ============================
 * 直接对齐 XAML 解析器的实际行为：**按物理行检查注释行内是否出现双减号**。
 * 之所以敢这么简化，是因为本项目的注释从不出现「一个注释块内
 * 混有正常行与违规行且需要精确定位」以外的情况 —— 违规一定在某一物理行上，
 * 定位到行即可定位到人。
 *
 * 具体规则（对每个物理行）：
 *   1. 不含 `<!--` 的行：跳过（不可能是注释的一部分的首行，但可能是续行，
 *      故仍在下方按「当前处于注释块内」的状态处理）。
 *   2. 处于注释块内的行：取 `<!--` 之后、`-->` 之前的部分查双减号；
 *      若该行以 `>` 结尾且其前两字符是 `-`（即 `...-->`），那两个减号
 *      是合法结束符，跳过这一处。
 *   3. 不处于注释块内但含 `<!--` 的行：进入注释块，从 `<!--` 之后开始检查。
 *
 *退出码：0 = 通过；1 = 有违规（打印 file:line 与整行文本）。
 *
 * 用法：node tools/xaml_comment_scan.js <file...>
 */

const fs = require('fs');

/** 检查一段「已剥离结束符」的文本里有没有双减号，返回位置或 -1。 */
function findDoubleDash(text) {
  return text.indexOf('--');
}

/**
 * @param {string} text 文件全文
 * @returns {{line:number, text:string, reason:string}[]}
 */
function scan(text) {
  const lines = text.split(/\r?\n/);
  const problems = [];

  // 记录当前是否处于一个未闭合的注释块内
  let inComment = false;

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];

    let rest;

    if (inComment) {
      // 续行：一直找到 `-->` 为止
      const end = line.indexOf('-->');
      rest = end < 0 ? line : line.slice(0, end);
    } else {
      const open = line.indexOf('<!--');
      if (open < 0) {
        continue; // 普通行
      }
      const afterOpen = open + 4;
      const close = line.indexOf('-->', afterOpen);
      if (close < 0) {
        rest = line.slice(afterOpen);       // 注释块开始，未闭合
      } else {
        rest = line.slice(afterOpen, close); // 单行闭合注释
      }
    }

    if (findDoubleDash(rest) >= 0) {
      problems.push({ line: i + 1, text: line.trim(), reason: '注释内有连续减号' });
    }

    // 更新注释块状态
    if (!inComment && line.includes('<!--') && line.indexOf('-->') < 0) {
      inComment = true;
    } else if (inComment && line.includes('-->')) {
      inComment = false;
    }
  }

  return problems;
}

const files = process.argv.slice(2);
if (files.length === 0) {
  console.error('用法：node tools/xaml_comment_scan.js <file...>');
  process.exit(2);
}

let bad = 0;
for (const file of files) {
  const problems = scan(fs.readFileSync(file, 'utf8'));
  for (const p of problems) {
    console.log(`${file}:${p.line}: ${p.reason}\n    ${p.text}`);
    bad++;
  }
}

if (bad > 0) {
  console.log(`\n共 ${bad} 处违规。把 CSS 的 var(--x) 与 .a-b-c 类名改写成中文语义名。`);
  process.exit(1);
}
console.log(`注释扫描通过（${files.length} 个文件）`);