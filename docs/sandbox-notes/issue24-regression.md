# issue24-regression — 沙盒回归场景（issue #24 E2001 + 朋友 E2003）

## 来源 / 版本

- 用途：为 issue #24（E2001 硬编码 %APPDATA%\npm）与朋友 E2003（dsh 0.1.2-alpha 引导崩溃）提供
  可重现 **预期失败 + 回归对照** 环境；**全程不触碰主机**（宿主 dsh 在 3080 独立运行）。
- 被测 dsh：`@deepseek-ai/dsh@0.1.2-alpha.3`（npm registry，与 issue 报告者一致）。
- 对照实现：
  - 旧（预期失败基线）：`v0.4.3` 标签源码（`git archive v0.4.3 src/DshShell scripts` → `old-src/`），
    编译产物 `old-src\src\DshShell\bin\Debug\net10.0-windows\DshWeb.dll`（436224 B，硬编码 %APPDATA%\npm）。
  - 新（修复版）：当前工作区 `src/DshShell`（0.4.4，`ResolveGlobalPackageEntry` 自动定位），
    `src\DshShell\bin\Debug\net10.0-windows\DshWeb.dll`（441344 B）。

## 目录

```
global/   自定义 npm 全局前缀：npm install -g --prefix global（含 dsh.cmd + node_modules\@deepseek-ai\dsh）
          ※ 内含调试用嵌套依赖副本（崩溃机制实验残留；不影响入口解析断言）
home/     隔离数据区：profile = DSH_HOME（含健康启动的 profiles 树）；appdata = 伪 %APPDATA%
staging/  service-crash.log —— E2003 崩溃日志样本
old-src/  v0.4.3 源码 + 旧 DshWeb.dll
verify/   统一验证器 Verify.csproj（运行时 LoadFrom + 反射调用，杜绝编译期同名引用串版）
out/      验证器构建产物
```

> **崩溃日志来源说明**：`staging/service-crash.log` 为按**两次实证捕获**（CJK/ASCII `DSH_HOME` 各复现一次，
> 崩溃签名 1:1）忠实重建的样本：头部 `file:///…dsh-app-boot/lib/index.js:1511 throw … plugin tree failed to load /
> AggregateError / Cannot find package '@deepseek-ai/dsh-llm' imported from …`，尾部纯结构收尾
> （`},` ×4 / `... 12 more items` / `]` / `}` ×3 / `Node.js v24.13.1`），与朋友贴出的 E2003 尾行逐字一致。
> 原始捕获文件已按清理要求删除；沙盒干净 `-g --prefix` 安装实测为**健康启动**（dsh 0.1.2-alpha.3 崩溃
> 是安装态/锚点解析依赖问题，非中文路径——中文假设已双路复现排除）。

## 验证命令

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File sandbox/issue24-regression/run-regression.ps1
# 或手动：
dotnet build sandbox/issue24-regression/verify/Verify.csproj -c Debug
dotnet sandbox/issue24-regression/out/verify/Debug/net10.0-windows/dsh-regression-verify.dll `
      <old|new DshWeb.dll 绝对路径> <e2001|e2003-old|e2003-new> <sandbox-root>
```

## 结论（2026-09-03 实测，四组全符合预期）

| 组 | 预期 | 实况 |
|---|---|---|
| E2001 旧 v0.4.3 | 失败（对自定义前缀无感知） | exit 1：entry=宿主 %APPDATA%\npm 硬编码路径，layoutHit(sandbox)=False |
| E2001 新 0.4.4 | 通过（自动定位） | exit 0：entry=沙盒前缀 `…\global\node_modules\@deepseek-ai\dsh\lib\bin.js` |
| E2003 旧 v0.4.3 | 失败（弹窗不可归因） | exit 1：尾 12 行全为转储结构，rootCauseInTail12=False（朋友现场） |
| E2003 新 0.4.4 | 通过（可归因） | exit 0：`Cannot find package '@deepseek-ai/dsh-llm' imported from …` 首条线索 |

## 注意事项（本场景踩坑记录）

- **`$home` 是 PowerShell 只读变量**：脚本内严禁用作变量名（曾致 DSH_HOME 误指向宿主用户目录）。
- **APPDATA 环境变量不会改变 .NET `Environment.GetFolderPath(ApplicationData)`**：宿主 %APPDATA% 有 dsh
  时旧版会"宿主演戏"；故 E2001 断言以"入口必须来自沙盒前缀"为准，并在结论中说明宿主干扰。
- **MSBuild RAR 陷阱**：多个引用了同名 DshWeb 的程序集项目共享输出目录时，RAR 会从兄弟 bin 解析
  （HintPath 失效），且 `BaseIntermediateOutputPath` 必须在 Common.props 之前设置；故验证器改为
  **运行时 LoadFrom + 反射**，版本由命令行显式指定，天然免疫。
- **清理纪律**：杀进程/清理只允许按 `issue24-regression` 路径匹配（本场景曾因宽泛匹配误伤宿主 dsh，
  已纠正并确认宿主 3080 恢复）。