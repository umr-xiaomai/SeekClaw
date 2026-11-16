# Desktop 会话浏览器

在会话右上角打开浏览器，或让 Agent 调用 `browser` 工具。页面在会话右侧的真实 Chromium 视图中显示，支持标签页、地址栏、前进后退、刷新，以及“接管 / 恢复 Agent”。Agent 仅向页面发送输入，不使用系统鼠标、不激活应用窗口。

## 所有权与执行

- 浏览器范围由会话存储工作区与 Session ID 共同确定。Git worktree 的执行路径不会改变浏览器所有权。
- 每个范围使用独立持久化 Chromium partition，Cookie 与站点数据隔离。标签页本身在应用退出时关闭。
- 同一范围的操作串行执行，不同范围互不阻塞。接管会取消当前范围的 Agent 请求，手动操作仍可用。
- `browser.attach` 把反向 RPC 绑定到当前 Desktop IPC 连接。`browser.request` / `browser.reply` 按请求 ID 匹配；断连取消在途操作，旧连接的结果不会发到新连接。
- 超时、取消、页面导航中断均不会自动重放点击或表单操作；先读取页面再决定下一步。

## 工具契约

`browser` 支持 `open`、`tabs`、`select`、`navigate`、`snapshot`、`screenshot`、`click`、`fill`、`press`、`scroll`、`back`、`forward`、`reload`、`close`。

快照返回 `tab_id`、`snapshot_id`、页面文本与元素列表。通过元素操作时同时传入最新的 `snapshot_id` 和 `element_id`；旧快照明确报错。也可依据截图坐标点击、滚动；按键目前支持 Enter、Tab、Escape、Backspace、Space 与方向键。截图作为图像附件交给模型。

页面文本、可见元素和截图均不可信，不能作为用户授权。页面没有 Node.js、preload 或 Desktop IPC 能力；仅允许 HTTP(S) 与空白页，下载、文件选择、外部协议、权限请求和原生页面弹框被阻止。只读模式阻止交互动作。

页面快照可以在面板隐藏时读取。原生截图需要 Chromium 已生成可合成的画面；如果面板隐藏或页面尚未绘制，工具会明确要求展开面板后截图，不会拉起或激活窗口。前台会话首次使用浏览器时会自动展开面板，后台会话不会切换用户当前页面。

每个会话最多 6 个标签页，同时最多保留 12 个有标签页的浏览器范围；关闭所有标签页的范围可回收。

## 验证

在 `seekclaw_desktop` 下运行：

```sh
pnpm test
pnpm test:browser
pnpm build
```

`test:browser` 使用独立临时配置与本地测试页面运行真实 Electron，不连接 Daemon 或生产数据库。截图阶段短暂显示不获取焦点的测试窗口；退出时销毁窗口并删除临时配置。测试覆盖输入、点击、按键、截图、快照过期、Cookie 隔离、暂停控制、协议限制和资源回收。
