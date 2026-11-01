# 从源码构建 InfinityView

## 环境

- Windows 11 x64；运行时使用系统 .NET Framework 4.8 或更高版本。
- 安装 `global.json` 指定的 .NET SDK（当前10.0.402，允许同特性带最新补丁）。
- 推荐 PowerShell 7。依赖由锁文件固定，NuGet源为公开nuget.org。

## 托管应用

在仓库根目录运行：

```powershell
pwsh -File scripts/Build-Source.ps1
& ./src/v0.4/LightView.App/bin/InfinityView/Release/net48/InfinityView.exe
```

等价手动命令：

```powershell
dotnet restore src/v0.4/LightView.App/LightView.App.csproj --locked-mode --configfile NuGet.config
dotnet build src/v0.4/LightView.App/LightView.App.csproj -c Release --no-restore
```

仓库包含两个原生x64 DLL，以及对应源码和许可证。

## 重建原生库（可选）

使用x86_64 MinGW-w64 GCC，将gcc与objdump所在目录加入PATH：

```powershell
pwsh -File native/lcms2/build.ps1
pwsh -File native/libwebp/build.ps1
```

来源与版本见各目录 `source.json`，完整许可见 `LICENSE` / `COPYING` / `PATENTS`。

## 测试

```powershell
pwsh -File scripts/Build-Source.ps1 -IncludeTests
& ./tests/v0.4/bin/Release/net48/LightView.Tests.exe --infinity-v1 ./work/source-check "InfinityView data paths"
```

测试通过可执行工具运行，使用入口和 filter 选择用例。部分色彩测试需准备外部ICC夹具。文件操作测试请使用隔离目录和人工样本。

## 便携副本

把应用输出目录完整复制到一个可写的新目录，创建名为 `portable` 的空文件。程序使用旁置 `UserData` 保存数据。

分发时保留根 MIT 许可、`LICENSE-BRANDING.md`，以及实际依赖的完整许可证和第三方声明；所需文件的位置见 `THIRD_PARTY_NOTICES.md`。代码按 MIT 开源，图标按独立许可使用。

## 安装器

`installer/InfinityView-v1.iss` 使用Inno Setup6.7.3，编译时需提供完整载荷、版本、安装身份、归属清单和完整性输入。

## 数据目录

安装版使用 `%LOCALAPPDATA%/InfinityView`，便携版使用旁置 `UserData`。可用 `INFINITYVIEW_DATA_DIR` 指定独立数据目录。
