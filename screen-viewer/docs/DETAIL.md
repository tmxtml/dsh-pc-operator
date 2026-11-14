# Screen viewer

`screen_viewer` captures the current display and returns the screenshot to the model as an image, so the model can look at the desktop before or after acting on it.

## What it does

- Captures the primary display by default; `display` can also select the display under the cursor or the whole virtual desktop, and `monitor` selects a zero-based monitor index.
- Draws the mouse cursor into the capture by default (`include_cursor: false` hides it), using the real cursor icon when Windows exposes it and a drawn arrow otherwise.
- Scales captures wider than `max_width` (480–3840, default 1600) down proportionally before encoding, keeping image tokens modest.
- Stores the PNG in the Harness attachment store and returns it as an `image` content block, so no temporary image file or extra `read_image` call is needed.
- Returns the screen origin, capture dimensions, byte size and cursor position as text beside the image.

## Typical use

1. Call `screen_viewer` to inspect the current screen.
2. Read the returned image and the cursor coordinates.
3. Use the `mouse_controller` tool from the mouse-controller bundle to act, then call `screen_viewer` again to see the result.

Each call runs a fresh native helper process, so screenshots do not serialize behind mouse gestures and can be taken while the mouse is moving.

The transient PNG is written under the current session workspace's `TMP/` directory (falling back to the OS temp directory) and is deleted immediately after it has been stored in the attachment service.

The capture is also recorded as the calling agent's latest coordinate check and effect verification: the `mouse-controller` bundle refuses every pointer action that is not preceded by a screenshot from the same agent taken after the previous pointer action (and no older than 60 seconds), and it refuses the next action, `step` or `done` until the previous pointer action's effect has been verified by a new screenshot. So the normal workflow is: `screen_viewer` → decide coordinates → `mouse_controller` action → `screen_viewer` again to verify the effect and check the next target → next action.

## Limitations

- Windows only: the native helper `native/ScreenCapture.exe` uses GDI and Win32 cursor APIs.
- Captures the desktop composited image; content that is protected from screen capture (some DRM video) appears black.
- The cursor is drawn by this tool; it is not part of the GDI desktop bitmap.
- The helper must be able to run in the user's interactive desktop session; running the Harness in a session without a desktop yields an empty or refused capture.
