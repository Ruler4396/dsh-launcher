# sandbox/tray-render（本地实验沙盒，不进库）

## 来源 / 目的

issue #28 托盘右键菜单两轮像素取证的台子：
- **#28-1**（"退 出"两字之间一道明显空档）：测量/绘制内边距被叠加进字距；
- **#28-3**（复测："托盘右键图标依旧异常"）：字距已修，但高 DPI 屏上文字按 s² 放大。

用反射加载已构建的 `DshWeb.dll`，调用生产代码 `DshWeb.Windows.TrayMenuForm.Draw(Graphics)`
渲染成 PNG——不复制任何排版实现，保证看到的像素就是用户看到的渲染。

## 用法

```powershell
dotnet build -c Release
$exe = "bin\Release\net10.0-windows\TrayRender.exe"
& $exe "E:\dsh-launcher\src\DshShell\bin\Debug\net10.0-windows\DshWeb.dll" 96 144 192
```

不给 dpi 参数 = 按宿主 DPI 渲染一张；给了则逐个渲染，产物落
`bin\Release\net10.0-windows\out\tray-<dpi>.png`（沙盒铁律：不写仓库外目录）。
控制台同时打印窗体尺寸、实际选中字体（含单位）与两种测量模式的字宽。

**在 100% 屏的机器上也能出 200% 的图**：缩放现在是 `TrayMenuForm(action, deviceDpi)` 的构造参数，
不再从宿主 DC 采样——这正是 #28-3 的修复点之一。

## 实测结论

### #28-1（本机 1x，2026-09 首轮）

- 字体解析到 `Noto Sans SC 10pt Regular`；`TextRenderer.MeasureText` 默认 flags 每字 **23px**，
  `NoPadding` 每字 **14px** → 每侧 ~4.5px 内边距。
- 旧实现（默认测量 + 首字矩形 `+4*s`）：两字墨迹空档 **12px**（设计意图 2px）；
  修复后（统一 `NoPadding` + `ShellLogic.TrayMenuLayout.PlaceExitRow`）空档回落到 ~4px。
- 已固化：`tests/DshShell.Tests/Regression_Issue28_TrayExitRow.RealOs.cs`（旧实现必红）。

### #28-3（复测轮，教训：**只在 1x 取证 = 没取证**）

首轮修复与回归测试全部在 1x 屏验证通过，报告人 200% 屏上依旧异常。逐像素量其截图：
卡片 225×73（s≈1.9）、电源图标墨迹宽 25px（比例正常）、"退"墨迹宽 **48px = 设计值 12·s 的
2.04 倍** → 文字按 s² 放大、卡片与图标按 s 放大。

根因：字号是 `GraphicsUnit.Point` 且已乘过 `_s`，绘制 DC 自带的 DPI 又折算一次
（全仓唯一"点数 × DPI 系数"的写法；`Chrome/CustomTitleBar.cs` 用的是正确的 `GraphicsUnit.Pixel`）。

修复后：字号折算只发生在纯函数 `ShellLogic.TrayMenuLayout.ComputeGeometry(deviceDpi)` 里一次，
渲染侧一律像素；画布分辨率显式钉 96。已固化：
- `tests/DshShell.Tests/TrayMenuLayoutContractTests.cs`（几何线性缩放 + 溢出钳制）；
- `tests/DshShell.Tests/Regression_Issue28_TrayMenuHighDpi.RealOs.cs`（真实渲染：
  同一菜单在 96/192 分辨率画布上墨迹宽度必须一致；墨迹宽必须随目标 DPI 线性增长；
  电源图标存在且不越出白卡片）。**这些断言在 96 DPI 的 CI runner 上同样有效**，
  因为目标 DPI 是构造参数、画布分辨率由测试指定，不依赖宿主显示器。

## 仍未覆盖

真机 200% 屏的**上屏**观感（层窗 UpdateLayeredWindow 之后的实际显示）只能在高分屏机器上
用本工具或实机右键托盘对照——CI 无法模拟虚拟显示器（既有口径见 `.github/workflows/e2e-multimon.yml`：
多屏/缩放边界回归全部迁移为 Headless 纯函数）。
