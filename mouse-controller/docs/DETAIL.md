# Mouse controller

`mouse_controller` drives the real Windows mouse pointer the way a person would, and shows the user which task the agent is currently executing.

## What it does

- `move` / `click` / `drag` / `scroll` actions are sent to a small persistent native helper (`native/MouseControl.exe`) that drives the pointer with `SendInput`.
- Motion is human-like: a bowed Bezier path, ease-in/ease-out velocity, distance-based duration, small decaying hand tremor, occasional overshoot followed by a settle correction, random step pacing, and a short hover before pressing a button.
- A task-aware overlay is pinned to the top-right corner of the primary display. While a mouse task is in progress it shows **`dsh鼠标接管中`** (red dot) together with the task's overall instruction (`任务：…`) and the current step (`步骤 N/M：…`), plus the Ctrl+D+S+H hint. It switches to **`未操控`** (green dot, "鼠标未被接管") only when the task is completed (`done`), aborted (`stop`), or stopped by the user's hotkey — never between individual gestures. The overlay is click-through and never takes focus.
- Task lifecycle: `start` begins a task with the overall `instruction`, the first `step`, and optional `step_index`/`step_total`; `step` updates the current step as work proceeds; `done` marks the task complete. Pointer actions taken without an active task start an implicit task so the overlay still reports what is happening.
- Holding **Ctrl+D+S+H** at any time stops the takeover immediately: the overlay switches to `未操控`, any in-progress gesture aborts, the task is cancelled, and the tool refuses further pointer actions. A later `start` is refused for a short cooldown so a stopped task is not instantly re-armed; after that, `start` may only be used when the user explicitly asks to resume.

## Screenshot requirements (before and after every action)

Every pointer action (`move`, `click`, `drag`, `scroll`) must be surrounded by screenshots from the **same agent**:

**Before the action** — call `screen_viewer` and read the returned image before choosing coordinates. `mouse_controller` rejects a pointer action when:

- no screenshot is on record for the calling agent,
- the last screenshot was taken **before** the previous pointer action, or
- the last screenshot is older than **60 seconds**.

**After the action** — once a pointer action succeeds, call `screen_viewer` again to verify the effect. Until that verification capture arrives:

- further pointer actions are rejected,
- `start`, `step`, `done` and `stop` are rejected,
- only `status` remains available.

So the enforced flow is: screenshot → action → screenshot (verify effect + check the next target) → action → … → screenshot → `done`/`stop`. The same capture serves as the verification of the previous action and the coordinate check for the next one.

**Terminating a task** — `stop` (active abort) additionally requires a screenshot no older than 60 seconds, even when the task performed no pointer action, so the final state is captured. `done` and `stop` both return a **progress report**:

- overall instruction,
- current step (`N/M` when `step_index`/`step_total` are set),
- number of pointer actions performed in the task,
- elapsed task time in seconds.

The rendered tool result includes this report, and the agent is instructed to report the same progress to the user in the conversation.

- A successful `screen_viewer` capture by the same agent satisfies the check; captures taken by another agent or a parallel conversation do not.
- If `screen-viewer` is not installed, pointer actions are refused with an instruction to install or call it.

The rejection is deliberate: it prevents blind coordinate guessing and unverified actions. The check is coordinated in-process through a shared `globalThis` record, so it works across the two separate bundles without extra configuration.

## Actions

| Action | Inputs | Result |
|---|---|---|
| `start` | `instruction`, optional `step`, `step_index`, `step_total` | Begin a visible mouse task; the overlay shows `dsh鼠标接管中` plus task and step. |
| `step` | `step`, optional `step_index`, `step_total` | Update the current step line of the running task. |
| `done` | — | Complete the task; the overlay switches to `未操控`; the result includes the progress report. |
| `move` | `x`, `y` | Move the pointer to a screen position. |
| `click` | optional `x`, `y`; `button` (`left`/`right`/`middle`); `count` 1–3 | Move if coordinates are given, then click. |
| `drag` | `from_x`, `from_y`, `to_x`, `to_y`; `button` | Press, drag with a slower natural profile, release. |
| `scroll` | `delta` (positive = up), `times` | Wheel steps with natural pauses. |
| `stop` | — | Abort the task after a fresh screenshot; the overlay switches back to `未操控`; the result includes the progress report. |
| `status` | — | Report task (instruction, step, progress, action count, elapsed time), takeover, overlay and cursor state. |

Coordinates are absolute screen pixels. Use `screen_viewer` from the screen-viewer bundle to see the screen and the cursor first.

## Overlay details

- Active size is 500×122 px, top-right of the primary display's working area; it shrinks to 330×62 px in the `未操控` state.
- The instruction line and the step line are single-line with an ellipsis for long text; the step line also shows `N/M` when `step_index`/`step_total` are supplied.
- The overlay appears when the helper starts (first `mouse_controller` call of the Harness session) and stays visible, showing `未操控` while no task runs, until the Harness or helper exits.

## Limitations

- Windows only: the helper uses Win32 `SendInput`, `SetWindowsHookEx` and WinForms.
- Task state lives in the helper process; if it crashes or is killed, the task display resets to `未操控` and the agent must call `start` again.
- The instruction and step are plain text; long instructions are ellipsized rather than wrapped.
- The Ctrl+D+S+H hook is installed only while a task is active and released as soon as the task ends, so no global keyboard listener stays behind when the mouse is idle. It reacts only when all four keys are down together and debounces repeats; a failed hook installation makes the tool refuse to take over rather than run without a kill switch.
- The hotkey is a real low-level keyboard hook, so it works no matter which window has focus, and it never swallows the keystrokes.
- UIPI/secure-desktop boundaries still apply: the agent cannot click into an elevated window from a non-elevated Harness process.
- The cooldown after a hotkey stop is 10 seconds.
