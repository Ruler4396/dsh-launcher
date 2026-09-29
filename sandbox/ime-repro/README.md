# sandbox/ime-repro（本地实验沙盒，不进库）

## 来源 / 目的

2026-09 issue #28-2（点击标题栏版本徽标 → 启动器闪退）的**机理实验台**。
线上证据：事件日志 Application Error 1000（`DshWeb.exe`，异常码 `0xc0000005`，故障模块
`coreclr.dll`）+ .NET Runtime 1026，两条托管栈均以 `ImmSetOpenStatus` 结尾，且都经过
`Program.ShowVersionInfoDialog ← CustomTitleBar.OnMouseDown`。

本沙盒用最小 WinForms 程序复跑"模态窗 + 可聚焦 Label/LinkLabel + IME"的各组合，
用于确认触发面（是否只有 Label 类控件、是否只在 IME 处于 open 时崩）。

## 环境

- .NET SDK 10.0.401 / TFM `net10.0-windows`
- 本机输入法含第三方 **PalmInput（手心输入法）3.2.9**（`PalmInputService.exe`），
  正是返回不可用 HIMC 的那个 IME

## 用法

```powershell
dotnet build -c Release
$exe = "bin\Release\net10.0-windows\ImeRepro.exe"
& $exe modal-linklabel  D:\Temp\ime-1.log   # 模态窗 + LinkLabel + Button
& $exe modal-button     D:\Temp\ime-2.log   # 只 Button
& $exe modal-empty      D:\Temp\ime-3.log   # 只普通 Label（不可聚焦）
& $exe modal-linklabel-imeopen D:\Temp\ime-4.log  # 先强制 ImmSetOpenStatus(true) 再开关窗
```

退出码 `0` = 未崩溃；`-1073741819`（`0xC0000005`）= 访问违规。

## 实测结论（2026-09，本机）

- 五种组合**均未复现 native AV**：本机 `ImmSetOpenStatus(true/false)` 在"显式关联的上下文"上
  可正常返回，说明该崩溃依赖第三方 IME 内部上下文状态（用户使用中输入法处于活跃组合态），
  无法用最小程序稳定复现。
- 但探针证实了**危险态**存在：未挂护栏的窗口 `ImeContext.GetImeMode(hwnd)` 返回 `Close`
  （中文输入法表的 `ImeClosed` 项），即 WinForms 会走
  `WmImeKillFocus → SetImeStatus(Close) → SetOpenStatus(false) → ImmSetOpenStatus`。
- 因此修复采取"让该路径不可达"的护栏方案（生产代码 `src/DshShell/Win32/ImeContextGuard.cs`），
  回归测试见 `tests/DshShell.Tests/Regression_Issue28_ImeContextCrash.RealOs.cs`
  （真实 imm32 断言护栏后 `ImmGetContext == NULL` 且 `GetImeMode == Disable`）。
