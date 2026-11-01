# InfinityView

<img src="docs/assets/InfinityView.png" alt="InfinityView" width="112" />

面向 Windows 11 x64 的轻量图片查看器。当前版本 **1.0.0**。

## 下载

普通用户无需编译，直接下载 Windows 11 x64 程序：

- **[下载安装版](https://github.com/WuQiong-Infinity/InfinityView/releases/download/v1.0.0/InfinityView-1.0.0-win-x64-setup.exe)**：双击安装，安装前先关闭程序窗口。
- **[下载免安装 ZIP](https://github.com/WuQiong-Infinity/InfinityView/releases/download/v1.0.0/InfinityView-1.0.0-win-x64-portable.zip)**：完整解压，运行其中的 `InfinityView.exe`。
- [发行页面、使用说明和校验值](https://github.com/WuQiong-Infinity/InfinityView/releases/tag/v1.0.0)

当前为**预发行版（Pre-release）**。GitHub的“Source code”压缩包是源码，普通使用请选上面的程序包。运行时使用Windows自带.NET Framework 4.8，无需另装.NET10运行时。

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

整理目标默认留空，由用户配置。复制/移动保持文件原字节，同名文件按所选策略处理。

## 项目布局

- `src/v0.4/`：应用程序和核心逻辑。
- `tests/v0.4/`：自带可执行测试工具，不是 `dotnet test` 测试项目。
- `native/`：Little CMS、libwebp 原生依赖源码、许可及编译脚本。
- `installer/`：Inno Setup 安装器集成与翻译许可。
- `docs/`：公开构建、使用说明与图标。

## 许可

代码和文档正文采用 [MIT License](LICENSE.txt)。**图标和标志版权归 WuQiong-Infinity 所有**，按 [图标许可](LICENSE-BRANDING.md) 提供。完整范围见 [COPYRIGHT.md](COPYRIGHT.md)，第三方组件许可见 [第三方声明](THIRD_PARTY_NOTICES.md)。

使用说明见 [用户指南](docs/InfinityView-v1.0-使用与更新.md)。

## 贡献者

见 [CONTRIBUTORS.md](CONTRIBUTORS.md)。
