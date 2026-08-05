@echo off
title Minecraft Update Assistant

echo.
echo  ==========================================
echo   Minecraft Update Assistant  v1.1.0
echo   Updates only changed files, keeps your saves
echo  ==========================================
echo.

if not exist "src\server.js" (
    echo   [Error] src\server.js not found. Please extract the full package.
    pause >nul
    exit /b 1
)

echo   Starting...
node src\server.js --open

if %errorlevel% neq 0 (
    echo.
    echo   [Error] Failed to start. Please check that Node.js is installed.
    echo   Download: https://nodejs.org/zh-cn/download
    pause >nul
)
