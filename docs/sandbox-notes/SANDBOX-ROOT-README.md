# 统一沙盒（SINGLE SANDBOX ROOT）— 铁律

> 本目录是 **唯一** 的本地持久沙盒根。规则见 `docs/00-ARCHITECTURE-GUARDRAILS-MANDATORY.md` 核心约束六。
> `.gitignore` L42-44 已忽略本目录（含整棵 node_modules，**绝不可入库**——2026-08-22 `git add -A` 误扫事件教训）。

## 目录约定

```
sandbox/
  README.md            ← 本文件（总纲）
  <场景名>/            ← 一个场景一个子目录，如 dsh-alpha2/
    global/            ← 隔离的 npm --prefix 全局安装（node_modules、cmd shim）
    home/              ← 该场景的 DSH_HOME（profile/会话/数据，可整体重建）
    staging/           ← 下载产物、日志、烟雾测试输出
    README.txt         ← 场景说明：安装来源/版本/验证命令/复现步骤
```

## 硬性约束

1. **只维护这一个沙盒根**（仓库根 `sandbox/`）。禁止在仓库外另建沙盒目录
   （如 `D:\dsh-sandbox`、`C:\dsh-sandbox`）——都是历史的歧路，已合并回本目录。
2. 一切本地测试安装/验证环境必须落在 `sandbox/<场景名>/` 子目录下；
   同一场景拆 `global`（安装根）/ `home`（DSH_HOME）/ `staging`（产物与日志）。
3. **`%TEMP%` 瞬态隔离不是沙盒**：`DshSandbox`（%TEMP%\dsh-sandbox-*）、
   `negative-test.ps1`（%TEMP%\dsh-neg）、`update-drill.ps1`（%TEMP%\dsh-drill）、
   `test.ps1` -CleanData 用例（%TEMP% 内）是**脚本内一次性隔离**，按各自既有铁律保持，
   不得与持久沙盒混为一谈，也不得削弱其 %TEMP% 围栏断言。
4. **junction 警告**：pnpm 会在 node_modules 里建 junction 链接，
   `Move-Item` **跨盘**搬迁会中途失败（2026-08-31 dsh-alpha2 home 实测事故）。
   搬迁整棵目录树用 `robocopy /MIR`（junction 感知）或同盘 rename；避免跨盘拖拽。
5. DSH_HOME 类目录（home/）视为**可整体重建的基线**：`dsh web` 首次使用会从
   随附模板自动初始化；被污染/损坏时直接删掉重建，不要手工修补。
6. `sandbox/` 之下不执行 `git add -A`；CI 可整体删除重建本目录。

## dsh 场景速查（以 dsh-alpha2 为例）

```powershell
$env:DSH_HOME = 'E:\dsh-launcher\sandbox\dsh-alpha2\home'
$env:DSH_TELEMETRY_DISABLED = '1'        # 0.1.2 预览线出厂 FEEDBACK_ONLY 遥测，必须显式关
& 'E:\dsh-launcher\sandbox\dsh-alpha2\global\dsh.ps1' --version
# 启动 web UI（起在非 3080 端口，避免与 GUI 冲突）：
& 'E:\dsh-launcher\sandbox\dsh-alpha2\global\dsh.ps1' web --host 127.0.0.1 --port 3099 --no-open
```