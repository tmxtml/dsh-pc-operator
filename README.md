# dsh-pc-operator

面向 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 的 Windows 桌面操控插件集：让模型直接「看屏幕」并「动手操作鼠标」。

包含两个独立的 DSH bundle：

| 插件 | 工具 | 作用 |
|---|---|---|
| [`screen-viewer`](screen-viewer) | `screen_viewer` | 截取当前显示器，把 PNG 作为图片内容直接返回给模型（可画出鼠标指针） |
| [`mouse-controller`](mouse-controller) | `mouse_controller` | 以接近真人的轨迹移动/点击/拖拽/滚轮真实鼠标，并在屏幕右上角常驻任务状态浮窗 |

## 环境要求

- Windows 10 / 11（x64）
- DeepSeek Harness 0.2.2-rc.3（已在 Desktop 配置验证）
- 仅在重新编译原生 helper 时需要 .NET Framework 4.x 的 `csc.exe`；仓库已附带编译好的 exe

## 安装

本仓库的每个子目录都是一个可安装的 DSH bundle（`package.json` 里声明了 `dsh.bundle.patch`）。

1. 克隆或下载本仓库，例如放到 `D:\code\dsh-plugins`。
2. 在 DSH Desktop 的 **插件 → 添加插件** 中选择 `screen-viewer` 和 `mouse-controller` 两个目录并启用；
   或者让 agent 执行 `plugin_manager` 的 `install_bundle`，目标为这两个目录的绝对路径。
3. 新会话中即可看到 `screen_viewer` 与 `mouse_controller` 工具（Profile 内所有会话共享）。

两个 bundle 可以单独安装：只装 `screen-viewer` 就只能截图，只装 `mouse-controller` 就只能操作鼠标。

## 使用

### screen_viewer

```jsonc
{ "display": "primary", "max_width": 1600, "include_cursor": true }
```

- `display`：`primary`（默认）/ `cursor`（指针所在显示器）/ `all`（整个虚拟桌面）
- `monitor`：从 0 开始的显示器序号，优先于 `display`
- `max_width`：480–3840，默认 1600，超宽会等比缩小
- `include_cursor`：默认 `true`，把真实指针画进截图

每次调用都会新截一张图（独立进程），可与鼠标操作真正并行。截图同时会登记为该 agent 的“坐标检查”记录，用来满足 `mouse_controller` 的操作前置检查。

### mouse_controller

任务协议（让浮窗显示当前任务与进度）：

```jsonc
{ "action": "start", "instruction": "把聊天窗口移到左上角并截图确认", "step": "移动到窗口标题栏", "step_index": 1, "step_total": 3 }
{ "action": "move",  "x": 400, "y": 120 }
{ "action": "step",  "step": "按住标题栏拖到左上角", "step_index": 2, "step_total": 3 }
{ "action": "click", "x": 400, "y": 120, "button": "left", "count": 1 }
{ "action": "drag",  "from_x": 400, "from_y": 120, "to_x": 30, "to_y": 30 }
{ "action": "scroll","delta": -120, "times": 2 }
{ "action": "done" }
```

- `start` 开始任务，浮窗显示「dsh鼠标接管中」+ 总指令 + 当前步骤；`step` 更新步骤；`done` 任务完成。
- 任务进行中执行任何 `move`/`click`/`drag`/`scroll` 都不会让浮窗变回未操控；只有 `done`（或中止 `stop` / 热键）才显示「未操控」。
- 未调用 `start` 时直接操作鼠标会自动生成一个隐式任务。
- `status` 返回任务、接管、浮窗和指针状态；`stop` 中止任务。

### 截图要求（操作前 + 操作后，强制）

**每次指针操作（move / click / drag / scroll）之前必须先用 `screen_viewer` 截图确认坐标，操作完成后必须再次截图确认效果。** 同一 agent 的成功截图才算数；别的会话/子代理的截图不算。

操作前，以下情况会被拒绝：

- 该 agent 还没有任何截图记录；
- 最近一次截图发生在**上一次指针操作之前**（即操作后没有重新截图）；
- 最近一次截图超过 **60 秒**。

操作成功后，在再次调用 `screen_viewer` 确认效果之前：

- 继续执行 move / click / drag / scroll 会被拒绝；
- `start`、`step`、`done` 会被拒绝；
- 仅 `status` 和 `stop`（中止）仍可用。

因此正常流程被强制为：`screen_viewer` → 定坐标 → `mouse_controller` 操作 → `screen_viewer` 确认效果（同时作为下一次操作的坐标检查）→ 下一次操作 → … → `screen_viewer` → `done`。如果没安装 `screen-viewer`，鼠标操作将全部被拒绝。

### 紧急停止

任务进行中，按住 **Ctrl + D + S + H** 会立即停止接管：浮窗切回「未操控」、当前手势中止、键盘钩子释放，之后工具拒绝操作，冷却 10 秒内不允许重新 `start`。

## 仓库结构

```
.
├── screen-viewer/            # 截图插件 bundle
│   ├── index.js              # Host plugin：注册 screen_viewer
│   ├── cordis.patch.yml
│   ├── package.json
│   ├── locale/{en,zh}.json
│   ├── icon.svg
│   ├── docs/DETAIL.md
│   └── native/ScreenCapture.cs + build.bat + ScreenCapture.exe
└── mouse-controller/         # 鼠标插件 bundle
    ├── index.js              # Host plugin：注册 mouse_controller
    ├── cordis.patch.yml
    ├── package.json
    ├── locale/{en,zh}.json
    ├── icon.svg
    ├── docs/DETAIL.md
    └── native/MouseControl.cs + build.bat + MouseControl.exe
```

- `index.js` 是纯 ESM Host 插件，只依赖 Node 内置模块，通过 `ctx.tools.register` 注册工具。
- 原生 helper 用 C#（.NET Framework / WinForms / Win32）实现：GDI 截图与光标绘制、`SendInput` 拟人轨迹、置顶浮窗、低级键盘钩子。
- JSON-line stdio 协议连接 Node 插件与原生 helper。

## 重新编译原生 helper

```bat
cd screen-viewer\native
build.bat

cd ..\..\mouse-controller\native
build.bat
```

脚本会使用系统自带的 .NET Framework 编译器（`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`），无需安装 SDK。

## 安全与限制

- `mouse_controller` 操作的是**真实鼠标**；浮窗常驻右上角（空闲时显示「未操控」），点击穿透、不抢焦点。
- Ctrl+D+S+H 是真实低级键盘钩子，仅在任务进行中安装，停止后立即释放，不会吞掉按键。
- 受 UIPI 限制，非提权进程无法点击提权窗口。
- 截图对受 DRM 保护的画面可能全黑。
- 任务状态保存在原生 helper 进程内，helper 崩溃/重启后浮窗回到「未操控」，需要重新 `start`。
- 仅支持 Windows；多显示器下浮窗固定显示在主屏右上角。

## License

[MIT](LICENSE)
