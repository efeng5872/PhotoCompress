# PhotoCompress

一个基于 **.NET 8 + WPF + Magick.NET** 的 Windows 原生图片压缩工具。

默认遵循“**保持原格式**、**不自动转格式**、**输出大小不超过目标值**”原则，并提供可视化预估与结果展示。

![PhotoCompress Icon](src/PhotoCompress.App/Assets/AppIcon.png)

---

## 功能特性

- 单图压缩：输入 1 张图片，输出 1 张压缩结果
- 目标大小约束：以 `<= 目标大小(KB)` 为达标标准
- 实时预估：参数变化后自动刷新预估（大小、尺寸、达标状态）
- 缩放建议：根据目标大小给出建议缩放比例，用户可手工覆盖
- 输出格式选择：
  - 自动（保持原格式）
  - JPEG / PNG / WEBP / BMP / GIF / TIFF
- 后缀联动：输出格式切换时，输出文件后缀自动联动
- 结果可读性：预估/结果使用成功（绿色）与失败（红色）状态提示
- 全局日志：应用启动、异常、压缩过程有日志记录（24小时制时间）

---

## 支持格式

- 输入格式：`jpg/jpeg/png/webp/bmp/gif/tif/tiff`
- 输出格式：`jpg/jpeg/png/webp/bmp/gif/tif/tiff`

---

## 运行环境

- Windows 10/11
- .NET SDK 8.0+

> 说明：项目使用 `net8.0-windows`（WPF），请在 Windows 环境构建与运行。

---

## 快速开始

```bash
git clone https://github.com/efeng5872/PhotoCompress.git
cd PhotoCompress

dotnet restore PhotoCompress.sln
dotnet build PhotoCompress.sln -c Release

dotnet run --project src/PhotoCompress.App/PhotoCompress.App.csproj -c Release
```

---

## 使用说明

1. 点击 **浏览...** 选择输入图片
2. 点击 **另存为...** 选择输出路径（默认会生成 `_compressed` 文件名）
3. 选择 **输出格式**：
   - 自动：保持输入格式
   - 其他格式：按所选格式预估与压缩
4. 设置 **目标大小 (KB)** 与 **缩放比例 (%)**（支持滑块和手工输入）
5. 在“**预估**”标签查看可达性与建议
6. 点击 **开始压缩**，自动切换到“**结果**”标签查看最终结果

---

## 输出格式与后缀联动规则

- 当选择显式格式（如 JPEG/PNG/WEBP）时：
  - 输出文件名后缀自动改为对应格式后缀
- 当切回“自动（保持原格式）”时：
  - 输出文件名后缀自动恢复为输入文件后缀
- 自动模式下，如果你手工选择了与输入不同的输出后缀：
  - 压缩前会弹出确认，避免无意格式转换

---

## 压缩策略（核心逻辑）

- 有损格式（JPEG / WEBP）：
  - 质量区间二分搜索（1~100）
  - 选择“满足目标大小的最高质量”
- PNG：
  - 尝试不同压缩级别（0~9）
- TIFF：
  - 尝试多种压缩方法（Zip/LZW/RLE/None）
- BMP：
  - 使用 RLE 尝试压缩
- 无法达标时：
  - 返回可执行建议（建议缩放、提高目标大小、可换格式）

---

## 日志说明

- 日志目录：`%LocalAppData%\PhotoCompress\logs\`
- 日志文件：`app-*.log`（按天滚动）
- 时间格式：`yyyy-MM-dd HH:mm:ss.fff`（24小时制）

---

## 项目结构

```text
PhotoCompress.sln
src/
  PhotoCompress.App/      # WPF UI 层
  PhotoCompress.Core/     # 压缩核心与模型
```

---

## 当前版本范围（v1）

- ✅ 单图压缩可用
- ✅ 预估/结果双 Tab 交互
- ✅ 输出格式可选与后缀联动
- ⏳ 批量处理（UI 已预留，后续版本）

---

## 常见问题

### 1) 为什么设置了很小的目标大小还是无法达标？
图片内容复杂、格式限制或当前缩放比例过高时，可能无法继续压缩。可尝试：
- 降低缩放比例
- 提高目标大小
- 选择 JPEG/WEBP 再重新预估

### 2) 预估和最终结果会完全一致吗？
预估基于同一套核心策略，通常接近最终结果；最终输出以实际压缩结果为准。

---

## License

当前仓库未附带明确 License 文件。若需开源分发，建议补充 `LICENSE`（如 MIT/Apache-2.0）。
