# InfinityView 使用说明

适用于 Windows 11 x64，使用系统自带的 .NET Framework 4.8。

## 安装与运行

- 安装版：运行 `InfinityView-1.0.0-win-x64-setup.exe`，选择安装目录。安装前关闭程序窗口，设置与整理预设会保留。
- 便携版：完整解压 `InfinityView-1.0.0-win-x64-portable.zip`，运行其中的 `InfinityView.exe`。

## 数据与设置

安装版设置位于 `%LOCALAPPDATA%/InfinityView`，便携版设置位于程序旁的 `UserData`。可通过 `INFINITYVIEW_DATA_DIR` 指定独立数据目录。

设置中可调整外观、图片详情、查看操作、快捷键、整理目标与默认应用。图片详情支持条目显隐和排序，默认应用由 Windows 设置管理。

## 常用操作

| 操作 | 默认键位 |
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

对比模式中，非活动窗格首次单击用于激活。拖动中间分隔线可调整窗格大小。两侧直方图可独立开关。

## 图片与文件整理

支持 JPG、JPEG、PNG、BMP、WebP。动画WebP显示合成首帧。图片经过ICC色彩转换，详情可读取Nikon滤镜名称。

收藏与废片目标默认留空，请在设置中配置。复制和移动保持原文件字节，同名文件按所选策略处理。Ctrl+Z用于撤销本会话的复制/移动；Delete将焦点图片移入Windows回收站，恢复时使用Windows回收站。

## 许可

代码及文档正文采用MIT许可。图标和标志版权归WuQiong-Infinity所有，按 `LICENSE-BRANDING.md` 使用。第三方组件遵循各自许可证。
