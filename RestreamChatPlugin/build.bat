@echo off
setlocal

REM 用 vswhere 定位 Visual Studio 自带的 MSBuild（需已安装 VS 且勾选“.NET 桌面开发”）
set VSWHERE="%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist %VSWHERE% (
  echo 找不到 vswhere，请先安装 Visual Studio Community 2022 并勾选“.NET 桌面开发”工作负载。
  pause
  exit /b 1
)

for /f "usebackq tokens=*" %%i in (`%VSWHERE% -latest -requires Microsoft.Component.MSBuild -find MSBuild\Current\Bin\MSBuild.exe`) do set MSBUILD=%%i
if not defined MSBUILD (
  echo 找不到 MSBuild，请确认已安装 Visual Studio 且勾选“.NET 桌面开发”。
  pause
  exit /b 1
)

echo 使用 MSBuild: %MSBUILD%
"%MSBUILD%" RestreamChatPlugin.csproj /p:Configuration=Debug /p:Platform=AnyCPU
if errorlevel 1 (
  echo.
  echo 生成失败。请把上面窗口里的红色错误文字发给我，我来改代码。
  pause
  exit /b 1
)

echo.
echo 生成成功！DLL 位于 bin\Debug\RestreamChatPlugin.dll
echo 把生成的 RestreamChatPlugin.dll（单文件，已内嵌 Newtonsoft.Json）复制到弹幕姬的 Plugins 文件夹即可。
pause
