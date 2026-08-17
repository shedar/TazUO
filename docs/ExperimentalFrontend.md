# Experimental frontend boundary

This branch keeps TazUO's network client, packet handling, world model, Legion runtime, scene updates, UI state, and input dispatch in one client process while making presentation injectable. It does not turn the client into a server. `GameController` owns an `IFrontendAdapter`; the normal local client, a detached client, and a loopback WebSocket viewer are implementations of that boundary.

The default remains `local`, so a launch without frontend arguments follows the existing native path.

## Modes and owned resources

- `-frontend local` enables native FNA rendering, audio, voice recognition, window input, and native presentation.
- `-frontend null` keeps client updates running but disables the draw loop, audio, voice recognition, native text input, and presentation. It does not construct the sprite-batch command pipeline or background texture.
- `-frontend websocket` keeps the same client state and render traversal, serves a viewer at `http://127.0.0.1:19870/` by default, and accepts pointer, wheel, resize, keyboard, and text input through the existing client input dispatch. Audio and voice recognition stay disabled. Native input/presentation are enabled only when the native window is not hidden.

The listener binds only to loopback and accepts one active viewer. A newly attached viewer replaces the previous one. With no viewer attached, no remote frames are rendered or captured; client updates continue. A one-frame mailbox replaces an unsent stale frame instead of accumulating latency.

Example semantic viewer launch:

```sh
./TazUO \
  -frontend websocket \
  -frontend-port 19870 \
  -frontend-fps 15 \
  -frontend-hide-window true \
  -frontend-frame-format display-list
```

Open `http://127.0.0.1:19870/` and click the canvas to focus it. `GET /health` returns `waiting` or `attached`.

## Framebuffer and semantic modes

The PNG and raw-RGBA modes are video-like framebuffer transport: the client composes a finished frame and transfers pixels to the viewer.

- `-frontend-frame-format rgba` sends raw RGBA pixels. It minimizes encoding work but consumes approximately `width * height * 4 * fps` bytes per second.
- `-frontend-frame-format png` encodes each composed frame as PNG. It lowers bandwidth but adds a synchronous GPU readback and PNG encoding cost.
- `-frontend-frame-format display-list` (aliases `displaylist`, `commands`, and `draw`) is remote drawing rather than a video stream. It serializes batched draw commands, render-target transitions, clears, render state, transformed vertices, and the texture regions those commands use. The browser replays the commands with WebGL 2.

Display-list capture sits at `UltimaBatcher2D`'s texture-batch flush boundary, not at individual triangle calls. Atlas data is sent as sparse 128-by-128 RGBA patches. The client retains one canonical latest patch per atlas tile, so reconnect can rebuild viewer state without retaining an unbounded update log. Disposed textures produce delete records. Render-target depth/stencil capabilities and FNA discard clears are preserved in the command stream.

Display-list payloads use zlib compression when it makes the payload smaller. Repetitive vertex and state data compresses substantially; the browser uses `DecompressionStream` before replay. Resource sequence acknowledgements prevent unchanged texture data from being resent to an attached viewer.

## Outer wire header

Every binary message uses a 32-byte little-endian header followed by its payload:

| Offset | Size | Meaning |
| ---: | ---: | --- |
| 0 | 4 | ASCII `TUOF` |
| 4 | 1 | protocol version (`1`) |
| 5 | 1 | payload type (`1` PNG, `2` raw RGBA, `3` display list) |
| 6 | 1 | flags (`1` means zlib-compressed; valid for display lists only) |
| 7 | 1 | reserved, zero |
| 8 | 8 | frame ID |
| 16 | 4 | client timestamp |
| 20 | 4 | width |
| 24 | 4 | height |
| 28 | 4 | transmitted payload length |

Input messages are bounded camel-case JSON objects. They include the most recently displayed frame ID so a later protocol can reject or reconcile stale interaction without changing the basic transport shape.

## Instrumentation

Pass a report path to enable session profiling:

```sh
./TazUO \
  -frontend websocket \
  -frontend-frame-format display-list \
  -frontend-instrumentation ./frontend-report.json
```

The client enables its hierarchical profiler and atomically checkpoints a version-2 JSON report. A normal shutdown writes `completed: true`; an interrupted process still leaves the most recent checkpoint.

The report includes:

- startup costs for the graphics device, adapter, render pipeline, UO data/graphics, UI assets, audio, plugins, and login scene;
- update, draw, and capture average/peak timings;
- encoded and uncompressed bytes, compression ratio, command bytes, resource bytes, command counts, and resource-record counts;
- queued and dropped frame counts;
- sprites, batch flushes, texture switches, and rendered-object peaks;
- ranked profiler contexts;
- aggregate draw costs by exact gump type and world-object type.

This supports the intended incremental extraction: keep low-level rendering behind the adapter first, identify an expensive coarse operation, pass all of its required state as parameters, and move that operation across the boundary without changing the Legion/world API available to the client process.

## Current boundary and limitations

`null` is detached and draw-free, but it is not yet a CPU-only executable. TazUO still creates SDL/FNA, a graphics device, render-backed UO asset loaders, and UI objects because login, scene, gump, and asset ownership are intertwined. The adapter policies and startup measurements make those dependencies explicit; splitting `UltimaOnline.Load` into data/world and visual-resource phases is the next large ownership seam.

The semantic viewer covers the built-in `UltimaBatcher2D` pipeline, including the standard UO hue/light shader behavior. Direct `GraphicsDevice` draws from plugins and arbitrary custom effects are not yet portable commands, so they can differ or be absent in display-list mode. PNG/RGBA remain useful as parity references for those cases.

The transport intentionally has no authentication because it is loopback-only. Exposing it outside a process, container, or microVM boundary requires an authenticated proxy, origin policy, encryption, and explicit memory/bandwidth limits.
