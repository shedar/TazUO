# Experimental frontend boundary

This branch keeps TazUO's network, world model, Legion runtime, scene updates, UI state, and input dispatch in one client process while making presentation optional. `GameController` owns an `IFrontendAdapter`; the normal local client, a detached client, and the loopback WebSocket viewer are implementations of that boundary.

The default remains `local`, so running TazUO without any new arguments follows the existing window and input path.

## Modes

- `-frontend local` uses the native FNA window and input. This is the compatibility path.
- `-frontend null` runs client updates but suppresses the draw traversal. It can hide the native window with `-frontend-hide-window true`.
- `-frontend websocket` runs the same client and exposes a viewer at `http://127.0.0.1:19870/` by default. The viewer receives completed frames and sends pointer, wheel, keyboard, and text input back through the same SDL-facing dispatch used by the local client.

The WebSocket listener binds only to loopback and accepts one active viewer. A newly attached viewer replaces the previous one. When there is no viewer, no frames are rendered or captured; client updates continue. A one-frame mailbox drops an unsent stale frame instead of building latency.

Example:

```sh
./TazUO \
  -frontend websocket \
  -frontend-port 19870 \
  -frontend-fps 15 \
  -frontend-hide-window true
```

Open `http://127.0.0.1:19870/` and click the canvas to focus it. `GET /health` returns `waiting` or `attached`.

## Frame formats

`-frontend-frame-format rgba` is the default. It sends raw RGBA pixels and is intended for a local viewer or a colocated container boundary. It avoids image encoding latency but uses about `width * height * 4 * fps` bytes per second.

`-frontend-frame-format png` trades substantial CPU time for lower bandwidth. It is useful for comparison and as a simple remote prototype, not as the long-term streaming codec.

Both formats use a 32-byte little-endian binary header followed by the payload:

| Offset | Size | Meaning |
| ---: | ---: | --- |
| 0 | 4 | ASCII `TUOF` |
| 4 | 1 | protocol version (`1`) |
| 5 | 1 | payload type (`1` PNG, `2` raw RGBA) |
| 8 | 8 | frame ID |
| 16 | 4 | client timestamp |
| 20 | 4 | width |
| 24 | 4 | height |
| 28 | 4 | payload length |

Input messages are bounded camel-case JSON objects. They include the most recently displayed frame ID so later experiments can reject or reconcile stale interactions without changing the transport shape.

## Instrumentation

Pass an absolute or relative report path to enable session profiling:

```sh
./TazUO \
  -frontend websocket \
  -frontend-instrumentation ./frontend-report.json
```

The client enables its hierarchical profiler, suppresses profiler spike log spam for the instrumented session, and atomically checkpoints the JSON report. A normal shutdown writes `completed: true`; an interrupted process still leaves the most recent checkpoint.

The report includes:

- update, draw, and capture average/peak timings;
- encoded, queued, and dropped frame counts and bytes;
- sprites, batch flushes, texture switches, and rendered-object peaks;
- ranked built-in profiler contexts;
- opt-in aggregate draw costs by exact gump type and world-object type.

This makes the next boundary refactor evidence-driven. For example, the first live experiment showed that PNG encoding dominated the login frame, which led to the raw-RGBA fast path while preserving PNG as an option.

## Deliberate limitations

`null` currently means detached presentation, not a GPU-free executable. TazUO still constructs FNA/SDL, a graphics device, render-backed asset loaders, audio, and UI objects during `LoadContent`. Removing those dependencies is the next initializer/resource-adapter seam; claiming this branch already provides a CPU-only headless client would hide the hardest remaining coupling.

The WebSocket mode streams the composed framebuffer rather than serializing individual draw primitives. That keeps the first split behavior-preserving and makes manual takeover immediately usable. A future semantic renderer can be introduced behind the same adapter after instrumentation identifies worthwhile coarse rendering boundaries.

There is intentionally no authentication because the experimental listener is loopback-only. Binding it outside the process/container boundary requires an authenticated proxy, origin policy, encryption, and explicit resource limits.
