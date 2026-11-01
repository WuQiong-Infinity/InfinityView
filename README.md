# InfinityView

<img src="docs/assets/InfinityView.png" alt="InfinityView" width="112" />

面向 Windows 11 x64 的轻量图片查看器。原名 LightView，当前源码版本 **1.0.0**，包含用户自制图标与目录迁移修订 r3。

## 下载

普通用户无需编译，直接下载 Windows 11 x64 程序：

- **[下载安装版](https://github.com/WuQiong-Infinity/InfinityView/releases/download/v1.0.0/InfinityView-1.0.0-win-x64-setup.exe)**：双击安装，更新旧版前先关闭窗口。
- **[下载免安装 ZIP](https://github.com/WuQiong-Infinity/InfinityView/releases/download/v1.0.0/InfinityView-1.0.0-win-x64-portable.zip)**：完整解压，运行其中的 `InfinityView.exe`。
- [发行页面、使用说明和校验值](https://github.com/WuQiong-Infinity/InfinityView/releases/tag/v1.0.0)

当前为未签名的**预发行版（Pre-release）**。GitHub的“Source code”压缩包是源码，普通使用请选上面的程序包。运行时使用Windows自带.NET Framework 4.8，无需另装.NET10运行时。

## 功能

- JPG / JPEG / PNG / BMP / WebP；动画 WebP 显示首帧。
- 指针中心缩放、适应窗口、100% 像素查看、平移和全屏。
- 同窗口双图对比、独立焦点、可拖动分隔线、可选联动缩放与位置。
- 每侧独立的明度与 RGB 直方图，可自定义条目和顺序的图片详情。
- Nikon Picture Control 滤镜名称读取。
- 可配置收藏复制、废片移动、会话内撤销与恢复日志；Delete 移入回收站。
- ICC 色彩转换：Windows WIC / libwebp 解码，Little CMS 转换。
- 设置与快捷键自定义、单实例打开、独立便携数据。

## 构建与运行

需要 Windows 11 x64、.NET Framework 4.8（系统组件）及 [global.json](global.json) 指定的 .NET SDK。

```powershell
pwsh -File scripts/Build-Source.ps1
& ./src/v0.4/LightView.App/bin/InfinityView/Release/net48/InfinityView.exe
```

原生 x64 依赖 DLL 随源码附带，对应源码、编译脚本和许可证也包含在仓库中。原生库重建、定向测试与安装器说明见 [构建文档](docs/BUILDING.md)。

## 默认操作

| 操作 | 键位 |
| --- | --- |
| 前后切图 | ← / →；当前窗格左右半区左键单击 |
| 平移 / 全屏 | 右键拖动 / 鼠标中键 |
| 指针中心100% / 适应 | 鼠标侧键1 / 侧键2 |
| 左右焦点 | A / D |
| 图片对比 / 联动 | C / X |
| 图片详情 / 焦点侧直方图 | Tab / 反引号 |
| 打开 / 设置 | F / Home |
| 在资源管理器中显示 / 复制拍摄信息 | R / Ctrl+C |
| 收藏复制 / 废片移动 / 回收站删除 | E / Q / Delete |

整理目标默认留空，由用户配置。复制/移动保持文件原字节，不覆盖同名文件。开发测试应使用隔离目录和人工样本。

## 项目布局

- `src/v0.4/`：当前 .NET Framework / WPF 产品实现；历史目录名沿用，不代表版本仍为0.4。
- `tests/v0.4/`：自带可执行测试工具，不是 `dotnet test` 测试项目。
- `native/`：Little CMS、libwebp 原生依赖源码、许可及编译脚本。
- `installer/`：Inno Setup 安装器集成与翻译许可。
- `docs/`：公开构建、使用说明与图标。

## 许可与发布范围

自有代码和文档正文继续按 [MIT License](LICENSE.txt) 发布，可商用、修改和再分发，须保留版权及许可声明。**InfinityView 图标和标志由 WuQiong-Infinity 保留版权，不在 MIT 授权范围内**，详见 [图标许可](LICENSE-BRANDING.md) 和 [版权范围](COPYRIGHT.md)。图标仅有限授权用于原版 InfinityView 的构建、展示和原样分发；修改版请使用其他图标或另行取得许可。第三方组件继续遵守各自许可证，见 [第三方声明](THIRD_PARTY_NOTICES.md)。

本仓库按上述分离许可重新建立，当前下载包会附带独立图标许可。删除重建不追溯撤销此前已有效授出的历史授权。

本仓库从1.0.0/r3创建源码公开基线，不包含开发过程的私有记录、机器配置和用户数据。旧 LightView 下载仓库与历史附件保持原样，旧附件不会因本次源码开源被静默替换。

当前安装包未签名，版本号不表示完整干净机器、Explorer、DPI、地区及真实IME矩阵均已通过。使用与迁移说明见 [用户说明](docs/InfinityView-v1.0-使用与更新.md)。

## 贡献者

见 [CONTRIBUTORS.md](CONTRIBUTORS.md)。
