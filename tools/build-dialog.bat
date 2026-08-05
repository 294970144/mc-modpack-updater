@echo off
REM ====================================================================
REM  编译 McDialog.exe —— 原生文件选择对话框小工具
REM
REM  作用：让"浏览文件"从 PowerShell 的 ~2s 降到 ~0.3s，且对话框总在最上层。
REM  依赖：Windows 自带的 .NET Framework（csc.exe），无需额外安装。
REM  运行：双击本 bat，或在命令行执行。编译产物：dist\McDialog.exe
REM
REM  编译完之后，主程序启动时会自动检测到 dist\McDialog.exe 并改用它，
REM  无需改任何代码。删掉 dist\McDialog.exe 即回退到 PowerShell。
REM ====================================================================
setlocal
cd /d "%~dp0"

echo [McDialog] 正在查找 csc.exe ...
set "CSC="
for /f "delims=" %%i in ('where /r "%SystemRoot%\Microsoft.NET\Framework64" csc.exe 2^>nul') do if not defined CSC set "CSC=%%i"
if not defined CSC for /f "delims=" %%i in ('where /r "%SystemRoot%\Microsoft.NET\Framework" csc.exe 2^>nul') do if not defined CSC set "CSC=%%i"
if not defined CSC (
  echo [McDialog] 错误：未找到 csc.exe。请确认本机已安装 .NET Framework（Win7+ 自带）。
  exit /b 1
)
echo [McDialog] 使用编译器：%CSC%

if not exist "..\dist" mkdir "..\dist"

echo [McDialog] 正在编译 ...
"%CSC%" /nologo /target:exe /platform:anycpu /out:"..\dist\McDialog.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll McDialog.cs
if errorlevel 1 (
  echo [McDialog] 错误：编译失败。
  exit /b 1
)

echo.
echo [McDialog] 完成 -^> dist\McDialog.exe
echo 下次启动主程序，"浏览文件"会自动改用 McDialog.exe（~0.3s 瞬开 + 总在最上层）。
echo.
pause
