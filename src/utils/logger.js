'use strict';

const C = {
  reset: '\x1b[0m', red: '\x1b[31m', green: '\x1b[32m',
  yellow: '\x1b[33m', blue: '\x1b[34m', gray: '\x1b[90m', cyan: '\x1b[36m',
};

const ts = () => new Date().toLocaleTimeString('zh-CN', { hour12: false });

/** 订阅者：用于把后端日志实时推到前端 SSE */
const subscribers = new Set();

function emit(level, msg) {
  for (const fn of subscribers) {
    try { fn({ level, message: msg, time: ts() }); } catch (_) { /* ignore */ }
  }
}

module.exports = {
  info(msg) { console.log(`${C.gray}[${ts()}]${C.reset} ${C.blue}[信息]${C.reset} ${msg}`); emit('info', msg); },
  success(msg) { console.log(`${C.gray}[${ts()}]${C.reset} ${C.green}[成功]${C.reset} ${msg}`); emit('success', msg); },
  warn(msg) { console.log(`${C.gray}[${ts()}]${C.reset} ${C.yellow}[警告]${C.reset} ${msg}`); emit('warn', msg); },
  error(msg) { console.log(`${C.gray}[${ts()}]${C.reset} ${C.red}[错误]${C.reset} ${msg}`); emit('error', msg); },
  step(msg) { console.log(`${C.gray}[${ts()}]${C.reset} ${C.cyan}[步骤]${C.reset} ${msg}`); emit('step', msg); },

  subscribe(fn) { subscribers.add(fn); return () => subscribers.delete(fn); },
};
