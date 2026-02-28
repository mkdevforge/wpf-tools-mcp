# Manual verification: `highlight_element`

Goal: make sure `highlight_element` works reliably (incl. multi-monitor), and that UIA element handles can be highlighted via the in-proc WPF agent when available.

## Preconditions

- Windows 10/11
- A WPF app to test against (recommended: any of the `src/WpfPilot.TestApp.*` projects, or your own app)
- An MCP client wired up to the `wpfpilot` MCP server (e.g., Codex/Claude Desktop/etc.)

## 1) Baseline: UIA overlay highlight (no agent)

1. Launch/attach to the app:
   - `launch_app` or `attach_to_app` → capture `sessionId`
2. Take a screenshot to pick a stable coordinate:
   - `take_screenshot` (client or window)
3. Pick an element via UIA:
   - `pick_element_at_point` with `backend=Uia` at a point inside a visible control
   - copy `element.elementId`
4. Highlight it:
   - `highlight_element` with `elementId=<uia elementId>`
   - set `preferInProcHighlight=false`

Expected:
- `Highlighted=true`
- `MethodUsed="win32_overlay"`

## 2) UIA elementId → WPF agent highlight mapping

1. Ensure the agent is available:
   - `inject_agent` with the same `sessionId`
2. Re-run highlight on the same UIA elementId:
   - `highlight_element` with `elementId=<uia elementId>`
   - set `preferInProcHighlight=true` (default)

Expected:
- `Highlighted=true`
- `MethodUsed="wpf_agent_mapped"` (meaning: UIA bounds were mapped to a WPF visual via `wpf/pick_element_at_point`)

## 3) Screenshot guarantee (annotated)

1. Call:
   - `highlight_element` with `returnScreenshot=true`

Expected:
- `Screenshot.Path` exists on disk and opens as a valid image
- The returned image has a rectangle annotation around the target bounds (even if any on-screen overlay isn’t captured by the screenshot pipeline)

## 4) Multi-monitor / negative coordinates

1. Move the window to each display (including negative X on a left-side monitor):
   - `set_window_bounds` (set `x` / `y` explicitly; keep `clampToVirtualScreen=true`)
2. Repeat:
   - `pick_element_at_point`
   - `highlight_element` (with mapping enabled)
   - `highlight_element(returnScreenshot=true)`

Expected:
- No failures due to negative coordinates
- `MethodUsed` remains consistent (`wpf_agent_mapped` when agent is connected; `win32_overlay` otherwise)

