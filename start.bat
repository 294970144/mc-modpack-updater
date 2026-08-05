@echo off
chcp 65001 >nul
title 我的世界更新助手

echo.
echo   ┌──────────────────────────────────────────┐
echo   │          我的世界更新助手 v1.0.0          │
echo   │     只更新变化的部分，不动存档和设置       │
echo   └──────────────────────────────────────────┘
echo.

if not exist "src\server.js" (
    echo   [错误] 没有找到 src\server.js，请先解压完整文件
    pause >nul
    exit /b 1
)

echo   正在启动…
node src\server.js --open

if %errorlevel% neq 0 (
    echo.
    echo   [错误] 启动失败，请检查是否安装了 Node.js
    echo   下载地址：https://nodejs.org/zh-cn/download
    pause >nul
)
