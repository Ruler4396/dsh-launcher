# archive/sandbox-drills — 沙盒演练脚本与真机日志归档

**这个分支是什么**：`dsh-launcher` 于 2026-09-29 停止更新（见 `master` 的 `CHANGELOG.md` 末版
v0.5.3 公告）。仓库主历史里只有产品代码、测试与文档；`sandbox/` 与 `dist-release/` 被
`.gitignore` 排除，里面躺着历次真机演练的**脚本**和**当时的原始读数**。这些件不进主历史（体积、
噪声、以及下面点名过的隐私原因），在这个 orphan 分支上单独留一份。

- 来源提交：`master` @ `77669489`（2026-09-29）
- 件数：344（334 件来自 `sandbox/`，7 件来自 `dist-release/` 与 `archive/`，外加本 README、
  `MANIFEST.tsv`、`CLEANUP-INVENTORY.md`）
- 体积：约 3.5 MB
- **无父提交**：本分支与 `master` 历史无关，不会污染主线的 diff / blame / 发布链

## 隐私遮蔽（本分支唯一的内容改写）

公开树里原本**一个文件都不含**维护者的本地账户名；而 `sandbox/` 的日志里有。所以归档副本做了
一次替换，规则只有一条、不碰任何数字或判词：

| 规则 | 出现形式 | 命中 |
|---|---|---|
| 本地账户名 → `<localUser>` | 维护者 Windows 登录名（9 字符小写，本文件不打印它） | 73 个文件、共 398 处 |

- 遮蔽后残余命中：**0**（对本提交内容检索）
- 密钥形状（`ghp_` / `github_pat_` / `sk-…` / `api_key` / `secret` / `password` / `bearer` 带值）：
  全 341 件扫描 **0 命中**
- **换行与字节数只对账户名这一段负责**：替换用 `perl -pe`，CRLF 原样保留（本机 `sed`/`awk` 会把
  `\r` 当行尾吃掉，已实测并弃用）。例：`sandbox/build.log` 原件 12,920 B / 60 个 CR →
  归档 12,932 B / 60 个 CR，差额正好是 6 处 × `+2` 字节。
- 逐件凭据在 **`MANIFEST.tsv`**：`path / bytes / accountNameHits / sha256_original / sha256_archived`
  （第 2、4 列量的是**工作树里的原件**，第 5 列量的是本分支这份）。想核对"只改了账户名"：把本分支
  某件里的 `<localUser>` 换回账户名再算哈希，应与第 4 列相等；未遮蔽的件两列相同。
- 本分支的 blob 是以 `git hash-object --no-filters` 写入的，所以 `git cat-file blob` 拿到的字节
  与第 5 列逐字相等，不受 `core.autocrlf` 影响。

## 刻意排除的件（不上传）

| 件 | 规模 | 排除理由 |
|---|---|---|
| `sandbox/issue-verify/real-web-package.json.bak` | 799 B（sha256 前缀 `4f09a587a102b1ec`） | 维护者**真实** dsh profile 的逐字节备份（含插件清单），属个人数据 |
| `archive/web-profile-backup-20260816/` | 5.0 KB | 同上，另一份真实 profile 备份 |
| `sandbox/issue-verify/issue24/zip/`（6 件） | 23 KB | **第三方报告人**诊断包解出的内容（含其机器上的用户名/环境）。原件已由其本人公开挂在 issue #24 的附件里，本分支不再复制第二份 |
| `scene-T*`（假 home，含整棵 `node_modules`） | ≈730 MB | 测试夹具，可再生 |
| `sandbox/issue-verify/shots/` | 106 MB | 屏幕截图，含维护者真实桌面内容 |
| `issue25-card/wv2-*`、`webview2*`、`home-e2e` 的 profile 目录 | ≈140 MB | WebView2 用户数据目录，可再生且含浏览痕迹 |
| `msi-v0.5.*/extracted/`、`prereq-x86/`、`x86-probe.msi` | 134 MB | MSI 解包产物与 32 位探针构建；正式发布资产在 GitHub Release 页 |
| 各 `bin/`、`obj/`、`dist/`、`.wix/`、`.neg-publish/`、根 `node_modules/` | ≈690 MB | 构建输出 |
| `dist-release/*.msi`、`*.zip`、`*.wixpdb` | 2.6 MB | v0.4.0 时代的**本地候选构建**，与 GitHub `v0.4.0` 那份资产字节不同（本地 msi 1,515,520 B / sha256 前缀 `f3ced627b04ad404`；GitHub 那份 1,437,696 B / `7cf7db7da58885ef`）。它的 `SHA256SUMS.txt` 与四份 `RELEASE-NOTES-v0.4.x.md` 已收入本分支留档 |
| `archive/*.tgz` | ≈90 KB | `npm pack` 产物，可由 tag 重建 |

## 这些被排除的件，2026-09-29 的下落

用户批准的清理范围是 A 档（只删 sandbox 下的可再生产物与截图，保留工作树）。逐件凭据见本分支
**`CLEANUP-INVENTORY.md`**（删除之前生成的：单件按字节 + SHA-256 + mtime 登记，目录按件数与字节合计登记，
`shots/` 另附逐件名录）。

| 已删（本机不再生有） | 仍在本机（本轮未批，随时可再处理） |
|---|---|
| `scene-T*` 假 home（≈755 MB）、`shots/`（106 MB，**不可再生**）、`prereq-x86/`（66 MB）、`x86-probe.msi` 等 10 件本地构建的 MSI/WIXPDB/PNG（≈80 MB）、`issue24/zip/`（原件仍公开挂在 issue #24）、`msi-v0.5.*/`、`isolated/`、`zip-check/`、`prereq-sc/`、`prereq-empty/`、`issue25-card/` 下的 `wv2-*`/`webview2`/`staging`/`old`/`home-*`（≈130 MB） | 两份真实 profile 备份（`real-web-package.json.bak`、`archive/web-profile-backup-20260816/`）；`dist-release/` 的 v0.4.0 候选构建；各 `bin/`、`obj/`、`dist/`、`.wix/`、`.neg-publish/`、根 `node_modules/`（合计 ≈690 MB）；`sandbox` 里剩下的脚本与日志（≈3.5 MB，与本分支内容同源） |

删除前的分类核对结论：**夹具与构建输出全部可再生**（演练脚本在本分支里，重跑即可重建）；
`issue24/zip/` 的原件仍公开挂在 issue #24，可随时重取。**唯一真正不可逆的是 `shots/`（48 件、106 MB
屏幕截图）**——它们没有上传（里面是维护者真实桌面内容），删除后本机不再有副本，`CLEANUP-INVENTORY.md`
里留下的是每件的文件名、字节与 mtime，不是图像本身。这一档是用户在 2026-09-29 明确批准删除的。

## 怎么读这些件（重要）

1. **它们是"当时那一帧"的读数，不是当前结论。** 里面的行数、测试数、G7 上限、版本号大多已被后续
   提交取代。要看现在的说法，读 `master` 的 `CHANGELOG.md`、`docs/ARCHITECTURE-DEBT-LEDGER.md`、
   `docs/reviews/`。
2. 有价值的三类：真机演练脚本（`sandbox/issue-verify/*.ps1`、`sandbox/update-rollback/*.ps1`、
   `sandbox/issue25-card/*.ps1`）、CI/发布留档（`ci-v050*.log`、`ci-aot-verify*.log`、`gate-*.log`）、
   缺陷复现原始日志（`demo-T*.log`、`T*.log`、`wpn-re*.log`、`probe-broken.log`）。
3. 注入/还原真实 profile 的那几个脚本（`inject-broken-plugin.ps1`、`restore-real-profile.ps1`、
   `real-safe-mode-reset.ps1`）**保留了硬前提**：写任何真实 profile 前先校验"备份哈希 == 当前文件哈希"。
   本分支不含那些备份，所以这些脚本在这里只能读、不能跑。
