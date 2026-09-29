# dsh-launcher 清算：本地磁盘清理台账

生成时刻（本机）：2026-09-29 21:06:56 +0800　执行提交：77669489
范围：用户 2026-09-29 批准的 A 档——只删 sandbox 下的可再生产物与截图，保留工作树、.git、源码、docs、以及 sandbox 里的脚本与日志。
被删件的文字类凭据（脚本/日志）已先上传到 orphan 分支 archive/sandbox-drills。

## 单件（逐字节登记）

| 件 | 字节 | sha256 | mtime |
|---|---|---|---|
| sandbox/issue-verify/x86-probe.msi | 70541312 | 617ddeead029a8e75904698eb083f1ba0d41a86f4d738c25ab8b5e6041a0c35b | 2026-09-21 11:12 |
| sandbox/issue-verify/aot-checker.msi | 3469312 | 46cfe49eb7ba96d47b3eafeb062b74ff7ed9ef5ce29695837f5c4408116aa701 | 2026-09-21 11:12 |
| sandbox/issue-verify/old-checker.msi | 1576960 | 2b8c7265bf758b1c33e49373af64971310183992df4a2ad8b1fd42bdacfc1a80 | 2026-09-21 10:52 |
| sandbox/issue-verify/new-checker.msi | 1560576 | 7e00cc57db1471a7f61f48a3004e25087ab9a3fac236a48d6598b47f01f4f8a9 | 2026-09-21 10:05 |
| sandbox/issue-verify/validate.msi | 1576960 | 8a6a3c4fb8519c71888f716789b308954071c8122483e411d0f2cd0ff10420e7 | 2026-09-21 09:19 |
| sandbox/issue-verify/validate2.msi | 1576960 | 3bafa498b9e111393624b163ae524e9e689080b2f2f846e7a8ff6e349d7f6294 | 2026-09-21 09:54 |
| sandbox/issue-verify/new-checker.wixpdb | 502810 | aa0f335c689b38a3f6c6e5e538c5876c1fcd7bf9d2d38925cf36bd89f68be3b0 | 2026-09-21 10:05 |
| sandbox/issue-verify/validate.wixpdb | 502788 | f05ea5b4083da7e783d30cc8341eddd0fc68272e30ed90bfdb449ed47a7f642c | 2026-09-21 09:19 |
| sandbox/issue-verify/validate2.wixpdb | 502797 | 565b36199f124fda413403a997b2acf67b2678bf85c34d9bcc155a0030e38bfe | 2026-09-21 09:54 |
| sandbox/issue-verify/issue28-shot1.png | 2045224 | 9e71cd05919634987df7635890c7b91c697a15b157f953dbfeddfda6cefa84b2 | 2026-09-20 21:46 |

## 目录（件数与字节合计；内容由演练脚本重建）

| 目录 | 件数 | 字节 |
|---|---|---|
| sandbox/issue-verify/scene-T1 | 192 | 17984549 |
| sandbox/issue-verify/scene-T2 | 170 | 10193354 |
| sandbox/issue-verify/scene-T3 | 25673 | 243820880 |
| sandbox/issue-verify/scene-T3R | 25682 | 255388051 |
| sandbox/issue-verify/scene-T4 | 167 | 10227920 |
| sandbox/issue-verify/scene-T567 | 200 | 19091658 |
| sandbox/issue-verify/scene-T8 | 153 | 9297636 |
| sandbox/issue-verify/scene-T9 | 193 | 17965426 |
| sandbox/issue-verify/scene-T10 | 2 | 196 |
| sandbox/issue-verify/scene-T11 | 195 | 18048590 |
| sandbox/issue-verify/scene-T12 | 191 | 17984734 |
| sandbox/issue-verify/scene-T13 | 193 | 17991504 |
| sandbox/issue-verify/scene-T14 | 238 | 24843478 |
| sandbox/issue-verify/scene-T15 | 227 | 21432138 |
| sandbox/issue-verify/shots | 52 | 110554979 |
| sandbox/issue-verify/prereq-x86 | 1 | 69076701 |
| sandbox/issue-verify/prereq-sc | 1 | 2068992 |
| sandbox/issue-verify/prereq-empty | 0 | 0 |
| sandbox/issue-verify/isolated | 2 | 2068607 |
| sandbox/issue-verify/zip-check | 8 | 1929841 |
| sandbox/issue-verify/issue24 | 7 | 16506 |
| sandbox/issue-verify/msi-v0.5.0 | 1 | 188 |
| sandbox/issue-verify/msi-v0.5.1 | 11 | 4547373 |
| sandbox/issue25-card/wv2-click | 171 | 10180979 |
| sandbox/issue25-card/wv2-dedupe | 176 | 10189792 |
| sandbox/issue25-card/wv2-default | 176 | 10189723 |
| sandbox/issue25-card/wv2-e2e | 186 | 19070510 |
| sandbox/issue25-card/wv2-forcetoast | 176 | 10181527 |
| sandbox/issue25-card/wv2-probe | 176 | 10189768 |
| sandbox/issue25-card/wv2-probe2 | 256 | 18581308 |
| sandbox/issue25-card/wv2-safemode | 176 | 10181393 |
| sandbox/issue25-card/wv2-size | 136 | 9992744 |
| sandbox/issue25-card/wv2-webnotify | 176 | 10215321 |
| sandbox/issue25-card/webview2 | 190 | 10193087 |
| sandbox/issue25-card/staging | 39 | 8005309 |
| sandbox/issue25-card/old | 276 | 6642093 |
| sandbox/issue25-card/home-e2e | 12 | 9672 |
| sandbox/issue25-card/home-probe2 | 3 | 7832 |

**合计可释放：目录 1018364359 + 单件 83855699 = 1102220058 字节（1051 MiB）**

## shots 目录逐件名录（这一档不可再生：截图没有上传，也没有删除前的副本）

| 件 | 字节 | mtime |
|---|---|---|
| sandbox/issue-verify/issue28-shot1.png | 2045224 | 2026-09-20 21:46 |
| sandbox/issue-verify/shots/T1-main.png | 215051 | 2026-09-19 22:11 |
| sandbox/issue-verify/shots/T11-1-before-drag.png | 7079445 | 2026-09-20 11:04 |
| sandbox/issue-verify/shots/T11-2-after-drag.png | 3109155 | 2026-09-20 11:04 |
| sandbox/issue-verify/shots/T11-3-card-on-secondary.png | 3119216 | 2026-09-20 11:04 |
| sandbox/issue-verify/shots/T11-3-card-stuck.png | 7062608 | 2026-09-20 10:54 |
| sandbox/issue-verify/shots/T11-4-maximized-secondary.png | 640683 | 2026-09-20 11:05 |
| sandbox/issue-verify/shots/T11-5-back-on-primary.png | 6845959 | 2026-09-20 11:05 |
| sandbox/issue-verify/shots/T12-1-after-doubleclick.png | 6976879 | 2026-09-20 10:27 |
| sandbox/issue-verify/shots/T12-2-after-max-button.png | 7180165 | 2026-09-20 09:54 |
| sandbox/issue-verify/shots/T13-0-probe.png | 1667468 | 2026-09-20 12:34 |
| sandbox/issue-verify/shots/T14-1-safe-ask.png | 638525 | 2026-09-20 19:57 |
| sandbox/issue-verify/shots/T14-2-in-safe-mode.png | 607280 | 2026-09-20 19:57 |
| sandbox/issue-verify/shots/T14-3-sticky-card.png | 639249 | 2026-09-20 19:57 |
| sandbox/issue-verify/shots/T14-4-back-to-normal.png | 2395056 | 2026-09-20 19:58 |
| sandbox/issue-verify/shots/T15-1-startup-ask.png | 1090854 | 2026-09-20 19:55 |
| sandbox/issue-verify/shots/T15-2-in-safe-mode.png | 2239006 | 2026-09-20 19:55 |
| sandbox/issue-verify/shots/T15-3-after-body-click.png | 2022356 | 2026-09-20 19:56 |
| sandbox/issue-verify/shots/T15-3-back-to-normal.png | 2096920 | 2026-09-20 19:56 |
| sandbox/issue-verify/shots/T15-3-in-safe-mode.png | 377752 | 2026-09-20 17:32 |
| sandbox/issue-verify/shots/T15-4-back-to-normal.png | 433243 | 2026-09-20 17:32 |
| sandbox/issue-verify/shots/T2-1-shown.png | 159149 | 2026-09-19 22:12 |
| sandbox/issue-verify/shots/T2-2-hovering.png | 198355 | 2026-09-19 22:13 |
| sandbox/issue-verify/shots/T2-3-rotated.png | 197972 | 2026-09-19 22:13 |
| sandbox/issue-verify/shots/T3-1-update-card.png | 232841 | 2026-09-20 11:48 |
| sandbox/issue-verify/shots/T3-2-ask-dialog.png | 222550 | 2026-09-20 11:48 |
| sandbox/issue-verify/shots/T3-3-after-build.png | 1274632 | 2026-09-20 11:49 |
| sandbox/issue-verify/shots/T3-5-applied.png | 603303 | 2026-09-20 11:50 |
| sandbox/issue-verify/shots/T3R-1-ask.png | 443144 | 2026-09-20 12:12 |
| sandbox/issue-verify/shots/T3R-2-after-real-apply.png | 241503 | 2026-09-20 12:14 |
| sandbox/issue-verify/shots/T3R-3-after-badge.png | 568145 | 2026-09-20 12:24 |
| sandbox/issue-verify/shots/T4-1-ask-dialog.png | 210123 | 2026-09-19 22:28 |
| sandbox/issue-verify/shots/T4-2-after-accept.png | 183125 | 2026-09-19 22:28 |
| sandbox/issue-verify/shots/T567-1-sticky.png | 224319 | 2026-09-19 22:30 |
| sandbox/issue-verify/shots/T567-2-after-60s.png | 224339 | 2026-09-19 22:31 |
| sandbox/issue-verify/shots/T567-3-after-exit.png | 214550 | 2026-09-19 22:32 |
| sandbox/issue-verify/shots/T8-1-version-dialog.png | 165308 | 2026-09-19 22:36 |
| sandbox/issue-verify/shots/T9-0-after-close.png | 7541457 | 2026-09-20 10:27 |
| sandbox/issue-verify/shots/T9-1-tray-menu.png | 7238788 | 2026-09-20 10:27 |
| sandbox/issue-verify/shots/T9-2-after-exit.png | 7538510 | 2026-09-20 10:27 |
| sandbox/issue-verify/shots/card-square-demo.png | 4630 | 2026-09-20 19:03 |
| sandbox/issue-verify/shots/crop-T11f-primary.png | 133087 | 2026-09-20 11:11 |
| sandbox/issue-verify/shots/crop-T11f-secondary.png | 676005 | 2026-09-20 11:11 |
| sandbox/issue-verify/shots/d-T14-2-in-safe-mode.png | 177911 | 2026-09-20 16:01 |
| sandbox/issue-verify/shots/d-T14-3-sticky-card.png | 157078 | 2026-09-20 16:01 |
| sandbox/issue-verify/shots/d-T14-4-back-to-normal.png | 146293 | 2026-09-20 16:01 |
| sandbox/issue-verify/shots/demo-1-before.png | 125415 | 2026-09-20 12:19 |
| sandbox/issue-verify/shots/demo-2-after.png | 120048 | 2026-09-20 12:24 |
