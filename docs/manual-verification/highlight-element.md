# Verify `highlight_element` manually

Use this check for the diagnostics-only picker and highlight paths. It covers the
Win32 overlay, optional UIA-to-WPF mapping, screenshot annotation, and negative
virtual-screen coordinates.

## Before you start

- Run Windows 10 or Windows 11.
- Build or install WPF Tools MCP with its agent and Snoop payload present.
- Start a WPF target. A project under `src/WpfToolsMcp.TestApp.*` is suitable.
- Configure the MCP server with `--tool-profile diagnostics`.

Keep the `sessionId` returned by `launch_app` or `attach_to_app`.

## Check the UIA overlay

1. Call `list_windows` and choose the target HWND.
2. Call `take_screenshot` for that window and choose a point inside a visible
   control.
3. Call `pick_element_at_point` with `backend=Uia` and keep the returned
   `element.elementId`.
4. Call `highlight_element` with that element ID and
   `preferInProcHighlight=false`.

Expected result:

- `highlighted` is `true`.
- `methodUsed` is `win32_overlay`.
- The overlay surrounds the selected control rather than the whole window.

## Check UIA-to-WPF mapping

1. Call `inject_agent` for the same session.
2. Call `highlight_element` again with the UIA element ID and
   `preferInProcHighlight=true`.

The preferred result is `methodUsed=wpf_agent_mapped`. This means the tool
mapped the UIA bounds to a WPF visual and used the in-process highlighter.

`methodUsed=win32_overlay` is a supported fallback. It shows that highlighting
worked but the chosen UIA element did not produce a usable WPF mapping. Agent
connectivity alone does not guarantee a mapping for every automation element.

## Check the screenshot

Call `highlight_element` with `returnScreenshot=true`.

Verify that:

- `screenshot.path` exists and is a valid image.
- The image contains a rectangle around the returned target bounds.

The image annotation is the durable evidence. A temporary on-screen overlay
does not have to appear in the captured pixels.

## Check multiple displays

1. Call `list_displays` and note the virtual-screen and per-display bounds.
2. Move the target with `set_window_bounds`. Keep
   `clampToVirtualScreen=true`.
3. Repeat the picker, mapped highlight, overlay highlight, and screenshot checks
   on each display.
4. Include a display with negative X or Y coordinates when the monitor layout
   has one.

Expected result:

- Picking and highlighting use the same virtual-screen coordinates.
- Negative coordinates do not cause clipping or selection errors.
- WPF mapping returns `wpf_agent_mapped` when a suitable visual is found and
  otherwise falls back to `win32_overlay`.

Record the date, WPF target, display layout, result from each section, and paths
to annotated screenshots with the related issue or pull request. This file is a
procedure, not evidence that the check has already passed on a particular build.
