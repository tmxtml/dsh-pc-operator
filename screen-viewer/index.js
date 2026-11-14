/**
 * screen_viewer — capture the current display as an image the model can inspect.
 *
 * The tool runs a tiny native helper (native/ScreenCapture.exe) that grabs the
 * requested monitor with GDI, optionally draws the cursor over it, scales the
 * capture down to a bounded width and writes a PNG. The plugin stores that PNG
 * in the attachment service and returns it as an image content block, so the
 * model receives the pixels directly (no extra read_image round trip).
 *
 * A fresh process is spawned for each call, so screenshots never block or
 * serialize with the mouse controller helper running in another process.
 */
import { execFile } from 'node:child_process'
import { readFile, unlink } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { promisify } from 'node:util'

const execFileAsync = promisify(execFile)
const SCREEN_CAPTURE_EXE = fileURLToPath(new URL('./native/ScreenCapture.exe', import.meta.url))

export const name = 'screen-viewer'
export const inject = ['tools', 'attachments']

const DISPLAY_VALUES = ['primary', 'cursor', 'all']

/**
 * Coordination record shared with the mouse-controller plugin through
 * `globalThis`, so a capture can satisfy that plugin's rule that every pointer
 * action must be preceded by a screenshot taken by the same Agent.
 */
const COORDINATION_KEY = Symbol.for('dsh.pc-operator.coordination')

function coordinationStore() {
  let store = globalThis[COORDINATION_KEY]
  if (!store) {
    store = { captures: new WeakMap(), anonymousCapture: null }
    globalThis[COORDINATION_KEY] = store
  }
  return store
}

function recordCapture(agent, capture) {
  const store = coordinationStore()
  if (agent !== undefined && agent !== null) store.captures.set(agent, capture)
  else store.anonymousCapture = capture
}

function clampInt(value, min, max, fallback) {
  const number = typeof value === 'number' ? value : Number(value)
  if (!Number.isFinite(number)) return fallback
  return Math.min(max, Math.max(min, Math.round(number)))
}

async function removeQuietly(path) {
  try { await unlink(path) } catch { /* already gone */ }
}

function imageBlockOf(value) {
  return {
    type: 'image',
    attachment: {
      attachmentId: value.attachment.attachmentId,
      mediaType: value.attachment.mediaType,
      bytes: value.attachment.bytes,
      width: value.attachment.width,
      height: value.attachment.height,
    },
  }
}

function summaryText(value) {
  const display = value.display === 'primary'
    ? 'the primary display'
    : value.display === 'all'
      ? 'the whole virtual desktop'
      : value.display === 'cursor'
        ? 'the display under the cursor'
        : `display ${value.display}`
  const cursor = value.cursorX >= 0 && value.cursorY >= 0
    ? ` The cursor is at screen (${value.cursorX}, ${value.cursorY}).`
    : ' The cursor position is unavailable.'
  return `Captured ${display}: ${value.captureWidth}x${value.captureHeight} px`
    + ` (screen origin ${value.screenX},${value.screenY}; ${value.bytes} bytes PNG).${cursor}`
}

export function apply(ctx) {
  ctx.effect(() => ctx.tools.register({
    name: 'screen_viewer',
    description: 'Capture the current display and return it as an image you can see. '
      + 'Each call takes a fresh screenshot, so call it again after the screen changes. '
      + 'The mouse cursor is drawn into the capture by default. '
      + 'A successful capture also satisfies mouse_controller\'s requirement that every pointer action be preceded by a coordinate check screenshot from the same agent.',
    parameters: {
      type: 'object',
      properties: {
        display: {
          type: 'string',
          enum: DISPLAY_VALUES,
          description: 'Which display to capture: the primary display (default), the display under the cursor, or the whole virtual desktop.',
        },
        monitor: {
          type: 'integer',
          description: 'Zero-based monitor index. When set it overrides display and selects that monitor.',
        },
        max_width: {
          type: 'integer',
          description: 'Maximum capture width in pixels, 480 through 3840. Defaults to 1600; larger captures are scaled down proportionally.',
        },
        include_cursor: {
          type: 'boolean',
          description: 'Draw the mouse cursor into the capture. Defaults to true; set false to hide it.',
        },
      },
      additionalProperties: false,
    },
    output: {
      schema: { type: 'object', additionalProperties: true },
      render: (_args, value) => [
        { type: 'text', text: summaryText(value) },
        imageBlockOf(value),
      ],
    },
    isConcurrencySafe: () => true,
    async execute(args, exec) {
      const display = args.monitor !== undefined ? String(clampInt(args.monitor, 0, 15, 0)) : (args.display ?? 'primary')
      const maxWidth = clampInt(args.max_width, 480, 3840, 1600)
      const includeCursor = args.include_cursor !== false
      const target = join(tmpdir(), `dsh-screen-${process.pid}-${Date.now()}-${Math.random().toString(16).slice(2)}.png`)
      const argv = ['--out', target, '--display', display, '--max-width', String(maxWidth)]
      if (!includeCursor) argv.push('--no-cursor')
      try {
        let stdout
        try {
          const result = await execFileAsync(SCREEN_CAPTURE_EXE, argv, {
            windowsHide: true,
            timeout: 30_000,
            maxBuffer: 1024 * 1024,
            signal: exec.signal,
          })
          stdout = result.stdout
        } catch (error) {
          throw new Error(`screen capture helper failed: ${error instanceof Error ? error.message : String(error)}`)
        }
        let info
        try {
          info = JSON.parse(stdout)
        } catch {
          throw new Error(`screen capture helper returned malformed output: ${String(stdout).slice(0, 200)}`)
        }
        if (!info || info.ok !== true) {
          throw new Error(`screen capture failed: ${info?.error ?? 'unknown error'}`)
        }
        const data = await readFile(target)
        const ref = await ctx.attachments.saveImage({ data, mediaType: 'image/png' })
        recordCapture(exec.agent, {
          at: Date.now(),
          display: info.display,
          cursorX: info.cursorX,
          cursorY: info.cursorY,
          width: info.width,
          height: info.height,
        })
        return {
          display: info.display,
          screenX: info.screenX,
          screenY: info.screenY,
          width: info.width,
          height: info.height,
          captureWidth: ref.width,
          captureHeight: ref.height,
          bytes: ref.bytes,
          cursorX: info.cursorX,
          cursorY: info.cursorY,
          attachment: {
            attachmentId: ref.attachmentId,
            mediaType: ref.mediaType,
            bytes: ref.bytes,
            width: ref.width,
            height: ref.height,
          },
        }
      } finally {
        await removeQuietly(target)
      }
    },
  }))
}
