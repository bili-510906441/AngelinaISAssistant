@echo off
setlocal EnableExtensions
title AngelinaISAssistant 依赖库安装
cd /d "%~dp0"

echo ============================================================
echo   安洁莉娜集成战略小助手 - 依赖库安装
echo ============================================================
echo.

rem ---------- 管理员权限 ----------
net session >nul 2>&1
if errorlevel 1 (
    echo 正在请求管理员权限...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

rem ---------- 系统版本检查 ----------
for /f "delims=" %%v in ('ver') do set "WINVER=%%v"
echo 当前系统: %WINVER%
echo.

rem ---------- Visual C++ 运行库 ----------
set "VCKEY=HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64"
set "VCINSTALLED="
for /f "tokens=2,*" %%a in ('reg query "%VCKEY%" /v Installed 2^>nul ^| findstr /i "Installed"') do set "VCINSTALLED=%%b"
if /i "%VCINSTALLED%"=="0x0" set "VCINSTALLED="

if defined VCINSTALLED (
    echo [跳过] 已安装 Visual C++ 2015-2022 x64 运行库
    goto :dotnet_check
)

echo [安装] Visual C++ 2015-2022 x64 运行库...
set "VCURL=https://aka.ms/vs/17/release/vc_redist.x64.exe"
set "VCEXE=%TEMP%\vc_redist.x64.exe"

if exist "%VCEXE%" del /f /q "%VCEXE%" >nul 2>&1

where curl.exe >nul 2>&1
if not errorlevel 1 (
    curl.exe -L --fail --retry 3 --connect-timeout 20 -o "%VCEXE%" "%VCURL%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
      "$ProgressPreference='SilentlyContinue'; Invoke-WebRequest -Uri '%VCURL%' -OutFile '%VCEXE%' -UseBasicParsing"
)

if not exist "%VCEXE%" (
    echo [失败] 下载 vc_redist.x64.exe 失败，请检查网络后重试。
    goto :done
)

"%VCEXE%" /install /quiet /norestart
set "VCCODE=%errorlevel%"

if "%VCCODE%"=="0" (
    echo [完成] Visual C++ 运行库安装成功。
) else if "%VCCODE%"=="1638" (
    echo [完成] 已安装更新版本的 Visual C++ 运行库。
) else if "%VCCODE%"=="3010" (
    echo [完成] Visual C++ 运行库安装成功，需要重启系统后生效。
) else (
    echo [警告] 安装程序返回代码 %VCCODE%，可尝试手动运行 %VCEXE%
)

del /f /q "%VCEXE%" >nul 2>&1

:dotnet_check
echo.
echo [检查] .NET 运行时...
rem 自包含版本已内置 .NET 运行时，无需额外安装；这里只做提示
if exist "%~dp0AngelinaISAssistant.exe" (
    echo        本版本为自包含发布，已内置 .NET 运行时，无需安装 .NET。
) else (
    echo        [提示] 未在当前目录找到 AngelinaISAssistant.exe，请把本文件放在程序目录下运行。
)

:done
echo.
echo ============================================================
echo   依赖安装流程结束
echo   现在可以双击 AngelinaISAssistant.exe 启动程序
echo ============================================================
echo.
pause
endlocal
exit /b 0
