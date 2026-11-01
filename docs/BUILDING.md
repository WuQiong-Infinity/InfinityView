# 从源码构建 InfinityView

## 环境

- Windows 11 x64；运行时使用系统 .NET Framework 4.8 或更高版本。
- 安装 `global.json` 指定的 .NET SDK（当前10.0.402，允许同特性带最新补丁）。
- 推荐 PowerShell 7。依赖由锁文件固定，NuGet源为公开nuget.org。
- 不需要原开发者的磁盘路径、work目录、构建环境JSON或访问令牌。

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

两个原生x64 DLL及其对应上游源码/许可均已保留，不依赖开发机隐藏文件，不会自动下载或安装外部软件。

## 重建原生库（可选）

使用x86_64 MinGW-w64 GCC，将gcc与objdump所在目录加入PATH：

```powershell
pwsh -File native/lcms2/build.ps1
pwsh -File native/libwebp/build.ps1
```

来源与版本见各目录 `source.json`，完整许可见 `LICENSE` / `COPYING` / `PATENTS`。编译回执只在本地生成。

## 定向测试

```powershell
pwsh -File scripts/Build-Source.ps1 -IncludeTests
& ./tests/v0.4/bin/Release/net48/LightView.Tests.exe --infinity-v1 ./work/source-check "InfinityView data paths"
```

这是自带可执行测试工具，不能用 `dotnet test` 代替。按修改选择对应入口和filter，不必每次运行全部历史测试。部分历史性能/显示色彩用例需要单独准备外部ICC夹具或指定路径。文件操作测试必须使用隔离目录和人工文件，不能用于真实照片。构建成功本身不代表完整桌面或环境矩阵通过。

## 便携副本

把应用输出目录整体复制到一个可写的新目录，创建名为 `portable` 的空文件。程序使用旁置 `UserData`。不要只复制EXE，也不要把便携标记加到日常安装目录。分发时保留根MIT许可、`LICENSE-BRANDING.md`，以及实际依赖的完整许可证和第三方声明；所需文件的位置见 `THIRD_PARTY_NOTICES.md`。代码按MIT开源，图标单独保留权利；分发修改版时需换用其他有权使用的图标或另行取得许可。

## 安装器

`installer/InfinityView-v1.iss` 是当前生产安装器集成，需Inno Setup6.7.3、完整安装载荷、版本、受信安装身份、归属清单和完整性输入。原开发发行脚本依赖本地验收证据，未将私人证据/机器配置复制到这个公开基线。不要绕过归属与回滚检查来修改真实安装，初次源码贡献可先使用隔离便携副本。公开可重复的发行流水线可后续独立完善。

## 兼容身份

当前产品为InfinityView1.0.0/r3，`src/v0.4`和部分LightView内部标识为历史兼容保留。默认安装数据为LocalAppData/InfinityView，便携为旁置UserData；`INFINITYVIEW_DATA_DIR`可显式隔离，旧`LIGHTVIEW_DATA_DIR`仍兼容。安装AppId/ProgId保留以便旧版升级，不应全局替换。
