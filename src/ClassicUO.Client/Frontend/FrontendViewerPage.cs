namespace ClassicUO.Frontend;

internal static class FrontendViewerPage
{
    public const string Content = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>TazUO remote frontend</title>
          <style>
            :root { color-scheme: dark; }
            * { box-sizing: border-box; }
            html, body { width: 100%; height: 100%; margin: 0; overflow: hidden; background: #080a0d; }
            body { display: grid; place-items: center; font: 13px ui-monospace, SFMono-Regular, Menlo, monospace; }
            canvas { max-width: 100vw; max-height: 100vh; outline: none; image-rendering: pixelated; box-shadow: 0 0 40px #000; }
            #status { position: fixed; left: 10px; top: 10px; padding: 6px 8px; background: #000b; border: 1px solid #ffffff26; border-radius: 4px; pointer-events: none; }
            #hint { position: fixed; left: 10px; bottom: 10px; color: #aaa; pointer-events: none; }
          </style>
        </head>
        <body>
          <canvas id="screen" tabindex="0" aria-label="TazUO remote screen"></canvas>
          <div id="status">connecting…</div>
          <div id="hint">click to focus · input is sent to the live client</div>
          <script>
            const HEADER_SIZE = 32;
            const canvas = document.querySelector('#screen');
            const context = canvas.getContext('2d', { alpha: false });
            const status = document.querySelector('#status');
            let socket;
            let frameId = 0;
            let latestPendingFrame = null;
            let drawing = false;
            let receivedBytes = 0;
            let receivedFrames = 0;
            let statsStarted = performance.now();

            function connect() {
              const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
              socket = new WebSocket(`${scheme}://${location.host}/ws`);
              socket.binaryType = 'arraybuffer';
              socket.onopen = () => { status.textContent = 'connected · waiting for frame'; canvas.focus(); };
              socket.onclose = () => { status.textContent = 'disconnected · reconnecting…'; setTimeout(connect, 1000); };
              socket.onerror = () => socket.close();
              socket.onmessage = event => {
                if (!(event.data instanceof ArrayBuffer) || event.data.byteLength < HEADER_SIZE) return;
                const view = new DataView(event.data);
                if (view.getUint8(0) !== 0x54 || view.getUint8(1) !== 0x55 || view.getUint8(2) !== 0x4f || view.getUint8(3) !== 0x46) return;
                const messageType = view.getUint8(5);
                if (view.getUint8(4) !== 1 || (messageType !== 1 && messageType !== 2)) return;
                const nextFrame = {
                  messageType,
                  id: Number(view.getBigInt64(8, true)),
                  width: view.getInt32(20, true),
                  height: view.getInt32(24, true),
                  payloadLength: view.getInt32(28, true),
                  bytes: event.data
                };
                if (nextFrame.payloadLength !== event.data.byteLength - HEADER_SIZE || nextFrame.width <= 0 || nextFrame.height <= 0) return;
                latestPendingFrame = nextFrame;
                receivedBytes += event.data.byteLength;
                receivedFrames++;
                drawLatest();
              };
            }

            async function drawLatest() {
              if (drawing) return;
              drawing = true;
              try {
                while (latestPendingFrame) {
                  const frame = latestPendingFrame;
                  latestPendingFrame = null;
                  if (canvas.width !== frame.width || canvas.height !== frame.height) {
                    canvas.width = frame.width;
                    canvas.height = frame.height;
                  }
                  if (frame.messageType === 1) {
                    const bitmap = await createImageBitmap(new Blob([frame.bytes.slice(HEADER_SIZE)], { type: 'image/png' }));
                    context.drawImage(bitmap, 0, 0);
                    bitmap.close();
                  } else {
                    const pixels = new Uint8ClampedArray(frame.bytes, HEADER_SIZE, frame.payloadLength);
                    context.putImageData(new ImageData(pixels, frame.width, frame.height), 0, 0);
                  }
                  frameId = frame.id;
                  const elapsed = Math.max(0.001, (performance.now() - statsStarted) / 1000);
                  status.textContent = `frame ${frameId} · ${frame.width}×${frame.height} · ${(receivedFrames / elapsed).toFixed(1)} fps · ${(receivedBytes / elapsed / 1024).toFixed(0)} KiB/s`;
                }
              } finally {
                drawing = false;
              }
            }

            function send(message) {
              if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ ...message, frameId }));
            }

            function point(event) {
              const bounds = canvas.getBoundingClientRect();
              return {
                x: (event.clientX - bounds.left) * canvas.width / Math.max(1, bounds.width),
                y: (event.clientY - bounds.top) * canvas.height / Math.max(1, bounds.height)
              };
            }

            canvas.addEventListener('pointermove', event => send({ type: 'pointerMove', ...point(event) }));
            canvas.addEventListener('pointerdown', event => {
              canvas.focus();
              canvas.setPointerCapture(event.pointerId);
              send({ type: 'pointerDown', button: event.button, ...point(event) });
              event.preventDefault();
            });
            canvas.addEventListener('pointerup', event => {
              send({ type: 'pointerUp', button: event.button, ...point(event) });
              if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
              event.preventDefault();
            });
            canvas.addEventListener('wheel', event => {
              send({ type: 'wheel', deltaY: event.deltaY, ...point(event) });
              event.preventDefault();
            }, { passive: false });
            canvas.addEventListener('contextmenu', event => event.preventDefault());

            function keyboardMessage(type, event) {
              send({
                type,
                code: event.code,
                key: event.key,
                altKey: event.altKey,
                ctrlKey: event.ctrlKey,
                shiftKey: event.shiftKey,
                metaKey: event.metaKey
              });
            }

            canvas.addEventListener('keydown', event => {
              keyboardMessage('keyDown', event);
              if (!event.ctrlKey && !event.altKey && !event.metaKey && event.key.length === 1) {
                send({ type: 'text', text: event.key });
              }
              event.preventDefault();
            });
            canvas.addEventListener('keyup', event => { keyboardMessage('keyUp', event); event.preventDefault(); });
            connect();
          </script>
        </body>
        </html>
        """;
}
