/**
 * mouse_controller — human-like control of the real mouse pointer on Windows.
 *
 * The tool drives a persistent native helper (native/MouseControl.exe) over a
 * JSON-line stdio protocol. The helper owns everything that must live outside
 * the Harness process:
 *   - the "dsh鼠标接管中" overlay pinned to the top-right corner of the display,
 *   - a low-level keyboard hook that stops the takeover when Ctrl+D+S+H is held,
 *   - SendInput based movement with Bezier curves, ease-in/ease-out velocity,
 *     decaying tremor, overshoot-and-correct, and random step pacing.
 *
 * The helper keeps the overlay up for the whole takeover. A hotkey stop latches
 * a stopped state: further actions are refused until `start` is called again
 * after the user explicitly asks. Screenshots run in their own process, so the
 * vision side and the pointer side can work at the same time.
 */
import { spawn } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const MOUSE_EXE = fileURLToPath(new URL('./native/MouseControl.exe', import.meta.url))

export const name = 'mouse-controller'
export const inject = ['tools']

const READY_TIMEOUT_MS = 10_000
const COMMAND_TIMEOUT_MS = 120_000
const HOTKEY_RESTART_COOLDOWN_MS = 10_000

const ACTIONS = ['move', 'click', 'drag', 'scroll', 'start', 'step', 'done', 'stop', 'status']
const POINTER_ACTIONS = ['move', 'click', 'drag', 'scroll']
const SCREENSHOT_MAX_AGE_MS = 60_000
const COORDINATION_KEY = Symbol.for('dsh.pc-operator.coordination')

/**
 * Coordination record shared with the screen-viewer plugin through `globalThis`.
 * screen-viewer writes `captures`; this plugin writes `lastActions`. Both maps
 * are keyed by the calling Agent, so a screenshot only satisfies the same
 * agent's pointer actions.
 */
function coordinationStore() {
  let store = globalThis[COORDINATION_KEY]
  if (!store) {
    store = { captures: new WeakMap(), lastActions: new WeakMap(), anonymousCapture: null, anonymousAction: 0 }
    globalThis[COORDINATION_KEY] = store
  }
  if (store.captures === undefined) store.captures = new WeakMap()
  if (store.lastActions === undefined) store.lastActions = new WeakMap()
  return store
}

function captureFor(agent) {
  const store = coordinationStore()
  return agent !== undefined && agent !== null ? store.captures.get(agent) : store.anonymousCapture
}

function lastActionFor(agent) {
  const store = coordinationStore()
  return agent !== undefined && agent !== null ? (store.lastActions.get(agent) ?? 0) : store.anonymousAction
}

function recordPointerAction(agent) {
  const store = coordinationStore()
  const at = Date.now()
  if (agent !== undefined && agent !== null) store.lastActions.set(agent, at)
  else store.anonymousAction = at
}

function requireFreshScreenshot(agent, action) {
  const capture = captureFor(agent)
  if (!capture) {
    throw new Error(`action "${action}" was rejected: no screenshot check is on record. Call screen_viewer first and verify the target coordinates; every pointer action requires a screenshot taken after the previous pointer action.`)
  }
  if (capture.at <= lastActionFor(agent)) {
    throw new Error(`action "${action}" was rejected: the last screenshot (${Math.max(0, Math.round((Date.now() - capture.at) / 1000))}s ago) predates the previous pointer action. Call screen_viewer again to check the current coordinates.`)
  }
  const age = Date.now() - capture.at
  if (age > SCREENSHOT_MAX_AGE_MS) {
    throw new Error(`action "${action}" was rejected: the last screenshot is ${Math.round(age / 1000)}s old (limit ${Math.round(SCREENSHOT_MAX_AGE_MS / 1000)}s). Call screen_viewer again to check the current coordinates.`)
  }
  return capture
}

/** The last pointer action has no screenshot taken after it yet. */
function unverifiedActionFor(agent) {
  const lastAction = lastActionFor(agent)
  if (!lastAction) return null
  const capture = captureFor(agent)
  if (capture !== undefined && capture !== null && capture.at > lastAction) return null
  return { at: lastAction, ageSeconds: Math.max(0, Math.round((Date.now() - lastAction) / 1000)) }
}

function failureMessage(result) {
  if (result && result.stopped) {
    return 'The user stopped mouse takeover with Ctrl+D+S+H. The task was aborted and the top-right overlay shows 未操控; '
      + 'do not call start again until the user explicitly asks you to resume.'
  }
  if (result && typeof result.message === 'string' && result.message.length > 0) return result.message
  if (result && typeof result.error === 'string' && result.error.length > 0) return result.error
  return 'mouse action failed'
}

function taskProgress(task) {
  if (!task || !task.active) return ''
  const prefix = task.total > 0 && task.index > 0 ? ` (step ${task.index}/${task.total})` : ''
  return `${prefix}: ${task.step}`
}

function createMouseController() {
  let daemon = null
  let chain = Promise.resolve()

  function enqueue(task) {
    const run = chain.then(task)
    chain = run.then(() => undefined, () => undefined)
    return run
  }

  function rejectPending(state, error) {
    for (const handler of state.pending.values()) handler({ ok: false, error: 'helper-exited', message: error.message })
    state.pending.clear()
  }

  function handleLine(state, line) {
    let message
    try { message = JSON.parse(line) } catch { return }
    if (message.event === 'ready') {
      state.ready = true
      state.hookOk = message.hook !== false
      if (state.readyTimer !== null) { clearTimeout(state.readyTimer); state.readyTimer = null }
      if (state.resolveReady) state.resolveReady()
      return
    }
    if (message.event === 'takeover-stopped') {
      state.stopped = true
      state.stoppedAt = Date.now()
      return
    }
    if (typeof message.id === 'number' && state.pending.has(message.id)) {
      const handler = state.pending.get(message.id)
      state.pending.delete(message.id)
      handler(message.result)
    }
  }

  function startDaemon() {
    const state = {
      child: null,
      seq: 0,
      buffer: '',
      pending: new Map(),
      ready: false,
      readyTimer: null,
      resolveReady: null,
      rejectReady: null,
      readyPromise: null,
      stopped: false,
      stoppedAt: 0,
      hookOk: true,
      closed: false,
    }
    state.readyPromise = new Promise((resolve, reject) => {
      state.resolveReady = resolve
      state.rejectReady = reject
    })
    const child = spawn(MOUSE_EXE, [], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true })
    state.child = child
    state.readyTimer = setTimeout(() => {
      if (state.ready) return
      state.rejectReady?.(new Error('mouse helper did not report ready in time'))
      try { child.kill() } catch { /* already gone */ }
    }, READY_TIMEOUT_MS)
    child.stdout.setEncoding('utf8')
    child.stdout.on('data', (chunk) => {
      state.buffer += chunk
      let newline
      while ((newline = state.buffer.indexOf('\n')) >= 0) {
        const line = state.buffer.slice(0, newline).trim()
        state.buffer = state.buffer.slice(newline + 1)
        if (line) handleLine(state, line)
      }
    })
    child.stderr.on('data', () => { /* helper diagnostics are not model-facing */ })
    child.on('error', (error) => {
      state.closed = true
      if (state.readyTimer !== null) { clearTimeout(state.readyTimer); state.readyTimer = null }
      state.rejectReady?.(new Error(`cannot start the mouse helper: ${error.message}`))
      rejectPending(state, new Error(`mouse helper failed: ${error.message}`))
      if (daemon === state) daemon = null
    })
    child.on('exit', (code, signal) => {
      state.closed = true
      if (state.readyTimer !== null) { clearTimeout(state.readyTimer); state.readyTimer = null }
      if (!state.ready) state.rejectReady?.(new Error(`mouse helper exited before ready (code ${code}, signal ${signal})`))
      rejectPending(state, new Error(`mouse helper exited (code ${code}, signal ${signal})`))
      if (daemon === state) daemon = null
    })
    return state
  }

  async function ensureDaemon() {
    if (daemon !== null && !daemon.closed) {
      await daemon.readyPromise
      return daemon
    }
    daemon = startDaemon()
    const state = daemon
    await state.readyPromise
    return state
  }

  function send(state, command, signal) {
    const id = ++state.seq
    return new Promise((resolve, reject) => {
      if (state.closed) {
        reject(new Error('the mouse helper is not running'))
        return
      }
      const timer = setTimeout(() => {
        state.pending.delete(id)
        reject(new Error('the mouse helper did not answer in time'))
      }, COMMAND_TIMEOUT_MS)
      state.pending.set(id, (result) => { clearTimeout(timer); resolve(result) })
      let aborted = false
      const onAbort = () => {
        if (aborted) return
        aborted = true
        clearTimeout(timer)
        state.pending.delete(id)
        try { state.child.stdin.write(JSON.stringify({ id: ++state.seq, cmd: 'stop' }) + '\n') } catch { /* helper gone */ }
        reject(new Error('mouse action was cancelled'))
      }
      if (signal !== undefined) {
        if (signal.aborted) { onAbort(); return }
        signal.addEventListener('abort', onAbort, { once: true })
      }
      try {
        state.child.stdin.write(JSON.stringify({ id, ...command }) + '\n')
      } catch (error) {
        clearTimeout(timer)
        state.pending.delete(id)
        reject(new Error(`cannot reach the mouse helper: ${error instanceof Error ? error.message : String(error)}`))
      }
    })
  }

  function checkResult(result) {
    if (result && result.stopped) {
      // Remember the latch at plugin level too, in case the helper process is restarted.
      if (daemon !== null) {
        daemon.stopped = true
        daemon.stoppedAt = Date.now()
      }
      throw new Error(failureMessage(result))
    }
    if (!result || result.ok !== true) throw new Error(failureMessage(result))
    return result
  }

  function baseValue(action, result, extra) {
    return {
      action,
      ok: true,
      takeover: action !== 'stop',
      overlay: result.overlay === undefined ? action !== 'stop' : result.overlay === true,
      task: result.task ?? null,
      cursor: result.cursor ?? null,
      ...extra,
    }
  }

  function requireXY(args, prefix) {
    if (args.x === undefined || args.y === undefined) {
      throw new Error(`action "${args.action}" needs ${prefix ?? ''}x and ${prefix ?? ''}y screen coordinates`)
    }
  }

  async function perform(args, exec) {
    const action = args.action
    if (!ACTIONS.includes(action)) throw new Error(`unknown action "${action}"`)
    if (exec.signal.aborted && !['status', 'start', 'stop', 'step', 'done'].includes(action)) {
      throw new Error('mouse action was cancelled')
    }
    if (action !== 'status') {
      const unverified = unverifiedActionFor(exec.agent ?? null)
      if (unverified) {
        throw new Error(`action "${action}" was rejected: the previous pointer action (${unverified.ageSeconds}s ago) has not been verified. Call screen_viewer now and check the result (cursor position, window state, effect) before continuing.`)
      }
    }
    const state = await ensureDaemon()
    if (state.stopped && !['status', 'start', 'stop', 'step', 'done'].includes(action)) {
      throw new Error(failureMessage({ stopped: true }))
    }

    if (action === 'status') {
      const result = checkResult(await send(state, { cmd: 'status' }, exec.signal))
      if (result.stopped === true) { state.stopped = true; state.stoppedAt = Date.now() }
      return {
        action,
        ok: true,
        takeover: result.takeover === true,
        stopped: result.stopped === true,
        overlay: result.overlay === true,
        task: result.task ?? null,
        cursor: result.cursor ?? null,
      }
    }

    if (action === 'start') {
      if (state.stopped && Date.now() - state.stoppedAt < HOTKEY_RESTART_COOLDOWN_MS) {
        throw new Error(failureMessage({ stopped: true }))
      }
      const instruction = typeof args.instruction === 'string' && args.instruction.trim().length > 0
        ? args.instruction.trim() : '鼠标操控任务'
      const step = typeof args.step === 'string' && args.step.trim().length > 0
        ? args.step.trim() : '准备开始'
      const result = checkResult(await send(state, {
        cmd: 'start',
        instruction,
        step,
        index: isFastInteger(args.step_index) ? args.step_index : 0,
        total: isFastInteger(args.step_total) ? args.step_total : 0,
      }, exec.signal))
      if (result.hook !== true) {
        throw new Error('the mouse helper could not install the Ctrl+D+S+H stop hook; the takeover was refused')
      }
      state.stopped = false
      return { action, ok: true, takeover: true, overlay: result.overlay !== false, task: result.task ?? null, cursor: result.cursor ?? null }
    }

    if (action === 'step') {
      if (typeof args.step !== 'string' || args.step.trim().length === 0) {
        throw new Error('step needs a non-empty step description')
      }
      const result = checkResult(await send(state, {
        cmd: 'step',
        step: args.step.trim(),
        index: isFastInteger(args.step_index) ? args.step_index : 0,
        total: isFastInteger(args.step_total) ? args.step_total : 0,
      }, exec.signal))
      return { action, ok: true, takeover: true, overlay: result.overlay !== false, task: result.task ?? null, cursor: result.cursor ?? null }
    }

    if (action === 'done') {
      const result = checkResult(await send(state, { cmd: 'done' }, exec.signal))
      return { action, ok: true, takeover: false, overlay: result.overlay !== false, task: result.task ?? null, cursor: result.cursor ?? null }
    }

    if (action === 'stop') {
      const capture = captureFor(exec.agent ?? null)
      const age = capture ? Date.now() - capture.at : Infinity
      if (!capture || age > SCREENSHOT_MAX_AGE_MS) {
        throw new Error('action "stop" was rejected: take a screenshot with screen_viewer first, so the final state is captured and the progress report reflects a fresh view of the screen.')
      }
      const result = checkResult(await send(state, { cmd: 'stop' }, exec.signal))
      return { action, ok: true, takeover: false, overlay: result.overlay !== false, task: result.task ?? null, cursor: result.cursor ?? null }
    }

    if (state.stopped) throw new Error(failureMessage({ stopped: true }))

    if (POINTER_ACTIONS.includes(action)) requireFreshScreenshot(exec.agent ?? null, action)

    if (action === 'move') {
      requireXY(args)
      const result = checkResult(await send(state, {
        cmd: 'move', x: Math.round(args.x), y: Math.round(args.y),
        duration: isFastInteger(args.duration_ms) ? args.duration_ms : 0,
      }, exec.signal))
      const value = baseValue(action, result)
      recordPointerAction(exec.agent ?? null)
      return value
    }

    if (action === 'click') {
      const hasX = args.x !== undefined
      const hasY = args.y !== undefined
      if (hasX !== hasY) throw new Error('click needs both x and y, or neither to click the current position')
      const command = { cmd: 'click', button: args.button ?? 'left', count: Number.isInteger(args.count) ? Math.min(3, Math.max(1, args.count)) : 1 }
      if (hasX) { command.x = Math.round(args.x); command.y = Math.round(args.y) }
      if (args.duration_ms !== undefined) command.duration = args.duration_ms
      const result = checkResult(await send(state, command, exec.signal))
      const value = baseValue(action, result, { clicked: command.count, button: command.button })
      recordPointerAction(exec.agent ?? null)
      return value
    }

    if (action === 'drag') {
      if (args.from_x === undefined || args.from_y === undefined || args.to_x === undefined || args.to_y === undefined) {
        throw new Error('drag needs from_x, from_y, to_x and to_y screen coordinates')
      }
      const result = checkResult(await send(state, {
        cmd: 'drag',
        fromX: Math.round(args.from_x), fromY: Math.round(args.from_y),
        toX: Math.round(args.to_x), toY: Math.round(args.to_y),
        button: args.button ?? 'left',
        duration: Number.isInteger(args.duration_ms) ? args.duration_ms : 900,
      }, exec.signal))
      const value = baseValue(action, result)
      recordPointerAction(exec.agent ?? null)
      return value
    }

    if (action === 'scroll') {
      if (!Number.isInteger(args.delta) || args.delta === 0) throw new Error('scroll needs a non-zero integer delta (positive scrolls up)')
      const result = checkResult(await send(state, {
        cmd: 'scroll', delta: args.delta,
        times: Number.isInteger(args.times) ? Math.min(10, Math.max(1, args.times)) : 1,
      }, exec.signal))
      const value = baseValue(action, result)
      recordPointerAction(exec.agent ?? null)
      return value
    }

    throw new Error(`unknown action "${action}"`)
  }

  function stopDaemon() {
    if (daemon === null) return
    const state = daemon
    daemon = null
    try { state.child.stdin.end() } catch { /* already closed */ }
    try { state.child.kill() } catch { /* already gone */ }
  }

  return {
    run(args, exec) {
      return enqueue(() => perform(args, exec))
    },
    stop: stopDaemon,
  }
}

// Small helper kept local so the plugin has no runtime dependencies.
function isFastInteger(value) {
  return typeof value === 'number' && Number.isInteger(value)
}

function progressReport(task) {
  if (!task) return 'no task summary is available'
  const parts = []
  parts.push(`instruction "${task.instruction || '鼠标操控任务'}"`)
  if (task.total > 0 && task.index > 0) parts.push(`step ${task.index}/${task.total} "${task.step}"`)
  else if (task.step) parts.push(`current step "${task.step}"`)
  parts.push(`${task.actions ?? 0} pointer action(s)`)
  parts.push(`${task.elapsedSeconds ?? 0}s elapsed`)
  return parts.join('; ')
}

function detailText(value) {
  if (!value) return ''
  const progress = taskProgress(value.task)
  if (value.action === 'status') {
    if (value.task && value.task.active) {
      return `Mouse task is in progress${progress}. Overall instruction: ${value.task.instruction}. `
        + `${value.task.actions ?? 0} pointer action(s) so far, ${value.task.elapsedSeconds ?? 0}s elapsed. `
        + 'The top-right overlay shows dsh鼠标接管中 with the task and current step.'
    }
    return 'Mouse takeover is off; the top-right overlay shows 未操控.'
      + (value.stopped ? ' The user stopped the takeover with Ctrl+D+S+H.' : '')
  }
  if (value.action === 'start') {
    return `Mouse task started${progress}. Overall instruction: ${value.task?.instruction ?? '鼠标操控任务'}. `
      + 'The top-right overlay shows dsh鼠标接管中 with the task and current step.'
  }
  if (value.action === 'step') return `Mouse task step updated${progress}. The overlay shows dsh鼠标接管中.`
  if (value.action === 'done') {
    return 'Mouse task completed; the top-right overlay switches to 未操控. '
      + `Progress report — ${progressReport(value.task)}. Report this progress to the user in the conversation.`
  }
  if (value.action === 'stop') {
    return 'Mouse task aborted; the top-right overlay switches to 未操控. '
      + `Progress report — ${progressReport(value.task)}. Report this progress to the user in the conversation.`
  }
  const cursor = value.cursor ? ` The cursor is now at (${value.cursor.x}, ${value.cursor.y}).` : ''
  if (value.action === 'move') return `Moved the pointer with human-like acceleration, tremor and settling.${cursor}`
  if (value.action === 'click') return `Clicked ${value.button ?? 'left'} ${value.clicked ?? 1} time(s).${cursor}`
  if (value.action === 'drag') return `Dragged from the start point to the target with a natural speed profile.${cursor}`
  if (value.action === 'scroll') return `Scrolled the wheel.${cursor}`
  return cursor
}

export function apply(ctx) {
  const controller = createMouseController()

  ctx.effect(() => {
    const onExit = () => controller.stop()
    process.once('exit', onExit)
    return () => {
      process.removeListener('exit', onExit)
      controller.stop()
    }
  })

  ctx.effect(() => ctx.tools.register({
    name: 'mouse_controller',
    description: 'Control the real mouse pointer on this Windows desktop with human-like motion '
      + '(curved paths, acceleration/deceleration, tremor, overshoot and settling). '
      + 'Coordinate check requirement: before every pointer action (move, click, drag, scroll) call screen_viewer and verify the target coordinates in the returned screenshot. '
      + 'A pointer action is rejected unless a screenshot was taken after the previous pointer action, by the same agent, and within the last 60 seconds. '
      + 'Post-action verification requirement: after every pointer action completes, call screen_viewer again to check the effect. Until that screenshot is taken, further pointer actions, start, step, done and stop are rejected (status remains available). '
      + 'Task protocol: call start with the overall instruction (instruction) and optionally the first step, update progress with step, and call done when the task is complete. '
      + 'Actively stopping a task (stop) also requires a fresh screenshot first; both stop and done return a progress report (overall instruction, current step N/M, pointer action count, elapsed seconds) that you must report to the user in the conversation. '
      + 'While the task is in progress the top-right overlay shows "dsh鼠标接管中" together with the overall instruction and the current step; only done, an aborted stop, or the user\'s Ctrl+D+S+H switches it to "未操控". '
      + 'Pointer actions without an active task start an implicit task. '
      + 'After a Ctrl+D+S+H stop, actions fail and you must not call start again unless the user explicitly asks.',
    parameters: {
      type: 'object',
      properties: {
        action: {
          type: 'string',
          enum: ACTIONS,
          description: 'Pointer gesture or task control. start begins a visible task; step updates its current step; done completes it; stop aborts it (stop needs a fresh screenshot first); status reports task and takeover state. move needs x/y; click optionally x/y; drag needs from_x/from_y/to_x/to_y; scroll needs delta. Every move/click/drag/scroll requires a fresh screen_viewer capture taken after the previous pointer action, and step/done/stop stay rejected until the action\'s effect is verified by a screenshot.',
        },
        x: { type: 'integer', description: 'Target screen x coordinate in pixels.' },
        y: { type: 'integer', description: 'Target screen y coordinate in pixels.' },
        from_x: { type: 'integer', description: 'Drag start screen x coordinate.' },
        from_y: { type: 'integer', description: 'Drag start screen y coordinate.' },
        to_x: { type: 'integer', description: 'Drag end screen x coordinate.' },
        to_y: { type: 'integer', description: 'Drag end screen y coordinate.' },
        button: {
          type: 'string',
          enum: ['left', 'right', 'middle'],
          description: 'Mouse button for click and drag. Defaults to left.',
        },
        count: { type: 'integer', description: 'Click count from 1 to 3. Defaults to 1.' },
        delta: { type: 'integer', description: 'Wheel delta per scroll step; positive scrolls up. Use negative values to scroll down.' },
        times: { type: 'integer', description: 'Number of wheel steps from 1 to 10. Defaults to 1.' },
        duration_ms: { type: 'integer', description: 'Optional gesture length in milliseconds; omit for a natural distance-based duration.' },
        instruction: { type: 'string', description: 'Overall instruction of the mouse task, shown on the overlay as the "任务" line. Set with start; when omitted the task is named 鼠标操控任务.' },
        step: { type: 'string', description: 'Current step description, shown on the overlay as the "步骤" line. Set with start or updated with step.' },
        step_index: { type: 'integer', description: 'One-based current step number, shown as N/M when step_total is also set.' },
        step_total: { type: 'integer', description: 'Total number of steps in the task, paired with step_index.' },
      },
      required: ['action'],
      additionalProperties: false,
    },
    output: {
      schema: { type: 'object', additionalProperties: true },
      render: (_args, value) => [{ type: 'text', text: detailText(value) }],
    },
    isConcurrencySafe: () => false,
    async execute(args, exec) {
      return controller.run(args, exec)
    },
  }))
}
