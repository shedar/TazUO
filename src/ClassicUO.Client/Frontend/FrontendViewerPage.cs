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
            #viewport { display: grid; outline: none; box-shadow: 0 0 40px #000; }
            canvas { grid-area: 1 / 1; max-width: 100vw; max-height: 100vh; image-rendering: pixelated; }
            canvas[hidden] { display: none; }
            #status { position: fixed; left: 10px; top: 10px; padding: 6px 8px; background: #000b; border: 1px solid #ffffff26; border-radius: 4px; pointer-events: none; }
            #hint { position: fixed; left: 10px; bottom: 10px; color: #aaa; pointer-events: none; }
          </style>
        </head>
        <body>
          <div id="viewport" tabindex="0" aria-label="TazUO remote screen">
            <canvas id="pixels"></canvas>
            <canvas id="commands" hidden></canvas>
          </div>
          <div id="status">connecting…</div>
          <div id="hint">click to focus · input is sent to the live client</div>
          <script>
            const HEADER_SIZE = 32;
            const viewport = document.querySelector('#viewport');
            const pixelCanvas = document.querySelector('#pixels');
            const commandCanvas = document.querySelector('#commands');
            const pixelContext = pixelCanvas.getContext('2d', { alpha: false });
            const status = document.querySelector('#status');
            let activeCanvas = pixelCanvas;
            let commandRenderer = null;
            let socket;
            let frameId = 0;
            let latestPendingFrame = null;
            let drawing = false;
            let receivedBytes = 0;
            let receivedFrames = 0;
            let statsStarted = performance.now();
            let lastRenderInfo = '';

            class DisplayListRenderer {
              constructor(canvas) {
                this.canvas = canvas;
                this.gl = canvas.getContext('webgl2', {
                  alpha: false,
                  antialias: false,
                  depth: true,
                  stencil: true,
                  premultipliedAlpha: true,
                  preserveDrawingBuffer: true
                });
                if (!this.gl) throw new Error('WebGL 2 is required for display-list mode');
                this.resources = new Map();
                this.currentTarget = 0;
                this.targetWidth = 1;
                this.targetHeight = 1;
                this.targetFlip = 1;
                this.indexCapacity = 0;
                this.program = this.createProgram();
                this.vertexBuffer = this.gl.createBuffer();
                this.indexBuffer = this.gl.createBuffer();
                this.vertexArray = this.gl.createVertexArray();
                this.gl.bindVertexArray(this.vertexArray);
                this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.vertexBuffer);
                this.gl.bindBuffer(this.gl.ELEMENT_ARRAY_BUFFER, this.indexBuffer);
                const stride = 11 * 4;
                this.attribute(0, 3, stride, 0);
                this.attribute(1, 3, stride, 3 * 4);
                this.attribute(2, 2, stride, 6 * 4);
                this.attribute(3, 3, stride, 8 * 4);
                this.gl.bindVertexArray(null);
                this.uniforms = {
                  flipY: this.gl.getUniformLocation(this.program, 'uFlipY'),
                  brightlight: this.gl.getUniformLocation(this.program, 'uBrightlight'),
                  texelSize: this.gl.getUniformLocation(this.program, 'uTexelSize'),
                  drawSampler: this.gl.getUniformLocation(this.program, 'uDrawSampler'),
                  hueSampler: this.gl.getUniformLocation(this.program, 'uHueSampler'),
                  lightSampler: this.gl.getUniformLocation(this.program, 'uLightSampler')
                };
                this.fallbackTexture = this.createFallbackTexture();
                this.gl.disable(this.gl.CULL_FACE);
              }

              attribute(index, size, stride, offset) {
                this.gl.enableVertexAttribArray(index);
                this.gl.vertexAttribPointer(index, size, this.gl.FLOAT, false, stride, offset);
              }

              createProgram() {
                const vertex = `#version 300 es
                  precision highp float;
                  layout(location=0) in vec3 aPosition;
                  layout(location=1) in vec3 aNormal;
                  layout(location=2) in vec2 aTexCoord;
                  layout(location=3) in vec3 aHue;
                  uniform float uFlipY;
                  out vec2 vTexCoord;
                  out vec3 vNormal;
                  out vec3 vHue;
                  void main() {
                    gl_Position = vec4(aPosition.x, aPosition.y * uFlipY, aPosition.z, 1.0);
                    vTexCoord = aTexCoord;
                    vNormal = aNormal;
                    vHue = aHue;
                  }`;
                const fragment = `#version 300 es
                  precision highp float;
                  const int NONE = 0;
                  const int HUED = 1;
                  const int PARTIAL_HUED = 2;
                  const int HUE_TEXT_NO_BLACK = 3;
                  const int HUE_TEXT = 4;
                  const int LAND = 5;
                  const int LAND_COLOR = 6;
                  const int SPECTRAL = 7;
                  const int SHADOW = 8;
                  const int LIGHTS = 9;
                  const int EFFECT_HUED = 10;
                  const int OUTLINE = 11;
                  const int GUMP = 20;
                  const float HUE_COLUMNS = 16.0;
                  const float HUE_WIDTH = 32.0;
                  const float HUES_PER_TEXTURE = 16384.0;
                  in vec2 vTexCoord;
                  in vec3 vNormal;
                  in vec3 vHue;
                  uniform sampler2D uDrawSampler;
                  uniform sampler2D uHueSampler;
                  uniform sampler2D uLightSampler;
                  uniform vec2 uTexelSize;
                  uniform float uBrightlight;
                  out vec4 outColor;

                  vec3 hueRgb(float gray, float hue) {
                    float halfPixelX = (1.0 / (HUE_COLUMNS * HUE_WIDTH)) * 0.5;
                    float hueColumnWidth = 1.0 / HUE_COLUMNS;
                    float hueStart = fract(hue / HUE_COLUMNS);
                    float x = clamp(hueStart + gray / HUE_COLUMNS, hueStart + halfPixelX, hueStart + hueColumnWidth - halfPixelX);
                    float y = mod(hue, HUES_PER_TEXTURE) / (HUES_PER_TEXTURE - 1.0);
                    return texture(uHueSampler, vec2(x, y)).rgb;
                  }

                  float landLight(vec3 normalValue) {
                    vec3 light = normalize(vec3(0.0, 1.0, 1.0));
                    vec3 normal = normalize(normalValue);
                    float base = max(dot(normal, light), 0.0) / 2.0 + 0.5;
                    return base + (uBrightlight * (base - 0.85355339) - (base - 0.85355339));
                  }

                  vec3 coloredLight(float shader, float gray) {
                    return texture(uLightSampler, vec2(gray, (shader - 0.5) / 63.0)).rgb;
                  }

                  void main() {
                    vec4 color = texture(uDrawSampler, vTexCoord);
                    int mode = int(vHue.y);
                    if (mode == OUTLINE) {
                      if (color.a > 0.0) discard;
                      for (int x = -1; x <= 1; x++) {
                        for (int y = -1; y <= 1; y++) {
                          if (texture(uDrawSampler, vTexCoord + vec2(float(x), float(y)) * uTexelSize).a > 0.0) {
                            outColor = vec4(vNormal.rgb, vHue.z);
                            return;
                          }
                        }
                      }
                      discard;
                    }
                    if (color.a == 0.0) discard;
                    float alpha = vHue.z;
                    if (mode == NONE) {
                      outColor = color * alpha;
                      return;
                    }
                    float hue = vHue.x;
                    if (mode >= GUMP) {
                      mode -= GUMP;
                      if (color.r < 0.02) hue = 0.0;
                    }
                    if (mode == HUED || (mode == PARTIAL_HUED && color.r == color.g && color.r == color.b)) {
                      color.rgb = hueRgb(color.r, hue);
                    } else if (mode == HUE_TEXT_NO_BLACK) {
                      if (color.r > 0.04 || color.g > 0.04 || color.b > 0.04) color.rgb *= hueRgb(1.0, hue);
                    } else if (mode == HUE_TEXT) {
                      color.rgb *= vNormal;
                    } else if (mode == LAND) {
                      color.rgb *= landLight(vNormal);
                    } else if (mode == LAND_COLOR) {
                      color.rgb = hueRgb(color.r, hue) * landLight(vNormal);
                    } else if (mode == SPECTRAL) {
                      alpha = 1.0 - color.r * 1.5;
                      color.rgb = vec3(0.0);
                    } else if (mode == SHADOW) {
                      alpha = 0.4;
                      color.rgb = vec3(0.0);
                    } else if (mode == LIGHTS) {
                      color.rgb = coloredLight(vHue.x - 1.0, color.r);
                    } else if (mode == EFFECT_HUED) {
                      color.rgb = hueRgb(color.g, hue);
                    }
                    outColor = color * alpha;
                  }`;
                const program = this.gl.createProgram();
                this.gl.attachShader(program, this.compile(this.gl.VERTEX_SHADER, vertex));
                this.gl.attachShader(program, this.compile(this.gl.FRAGMENT_SHADER, fragment));
                this.gl.linkProgram(program);
                if (!this.gl.getProgramParameter(program, this.gl.LINK_STATUS)) throw new Error(this.gl.getProgramInfoLog(program));
                return program;
              }

              compile(type, source) {
                const shader = this.gl.createShader(type);
                this.gl.shaderSource(shader, source);
                this.gl.compileShader(shader);
                if (!this.gl.getShaderParameter(shader, this.gl.COMPILE_STATUS)) throw new Error(this.gl.getShaderInfoLog(shader));
                return shader;
              }

              createFallbackTexture() {
                const texture = this.gl.createTexture();
                this.gl.bindTexture(this.gl.TEXTURE_2D, texture);
                this.gl.texImage2D(this.gl.TEXTURE_2D, 0, this.gl.RGBA, 1, 1, 0, this.gl.RGBA, this.gl.UNSIGNED_BYTE, new Uint8Array([255, 255, 255, 255]));
                this.setTextureSampling(this.gl.NEAREST);
                return texture;
              }

              setTextureSampling(filter) {
                this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_MIN_FILTER, filter);
                this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_MAG_FILTER, filter);
                this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_WRAP_S, this.gl.CLAMP_TO_EDGE);
                this.gl.texParameteri(this.gl.TEXTURE_2D, this.gl.TEXTURE_WRAP_T, this.gl.CLAMP_TO_EDGE);
              }

              createResource(id, width, height, flags) {
                this.deleteResource(id);
                const texture = this.gl.createTexture();
                this.gl.bindTexture(this.gl.TEXTURE_2D, texture);
                this.gl.texImage2D(this.gl.TEXTURE_2D, 0, this.gl.RGBA8, width, height, 0, this.gl.RGBA, this.gl.UNSIGNED_BYTE, null);
                this.setTextureSampling(this.gl.NEAREST);
                const renderTarget = (flags & 1) !== 0;
                const hasDepth = (flags & 2) !== 0;
                const hasStencil = (flags & 4) !== 0;
                const resource = { id, width, height, renderTarget, hasDepth, hasStencil, texture, framebuffer: null, depthStencil: null };
                if (renderTarget) {
                  resource.framebuffer = this.gl.createFramebuffer();
                  this.gl.bindFramebuffer(this.gl.FRAMEBUFFER, resource.framebuffer);
                  this.gl.framebufferTexture2D(this.gl.FRAMEBUFFER, this.gl.COLOR_ATTACHMENT0, this.gl.TEXTURE_2D, texture, 0);
                  if (hasDepth) {
                    resource.depthStencil = this.gl.createRenderbuffer();
                    this.gl.bindRenderbuffer(this.gl.RENDERBUFFER, resource.depthStencil);
                    if (hasStencil) {
                      this.gl.renderbufferStorage(this.gl.RENDERBUFFER, this.gl.DEPTH24_STENCIL8, width, height);
                      this.gl.framebufferRenderbuffer(this.gl.FRAMEBUFFER, this.gl.DEPTH_STENCIL_ATTACHMENT, this.gl.RENDERBUFFER, resource.depthStencil);
                    } else {
                      this.gl.renderbufferStorage(this.gl.RENDERBUFFER, this.gl.DEPTH_COMPONENT24, width, height);
                      this.gl.framebufferRenderbuffer(this.gl.FRAMEBUFFER, this.gl.DEPTH_ATTACHMENT, this.gl.RENDERBUFFER, resource.depthStencil);
                    }
                  }
                  if (this.gl.checkFramebufferStatus(this.gl.FRAMEBUFFER) !== this.gl.FRAMEBUFFER_COMPLETE) throw new Error(`incomplete render target ${id}`);
                  this.gl.bindFramebuffer(this.gl.FRAMEBUFFER, null);
                }
                this.resources.set(id, resource);
              }

              deleteResource(id) {
                const old = this.resources.get(id);
                if (!old) return;
                this.gl.deleteTexture(old.texture);
                if (old.framebuffer) this.gl.deleteFramebuffer(old.framebuffer);
                if (old.depthStencil) this.gl.deleteRenderbuffer(old.depthStencil);
                this.resources.delete(id);
              }

              patchResource(id, x, y, width, height, pixels) {
                const resource = this.resources.get(id);
                if (!resource) throw new Error(`texture patch references unknown resource ${id}`);
                this.gl.bindTexture(this.gl.TEXTURE_2D, resource.texture);
                this.gl.pixelStorei(this.gl.UNPACK_ALIGNMENT, 1);
                this.gl.texSubImage2D(this.gl.TEXTURE_2D, 0, x, y, width, height, this.gl.RGBA, this.gl.UNSIGNED_BYTE, pixels);
              }

              render(buffer, frameWidth, frameHeight, payloadOffset = HEADER_SIZE) {
                const view = new DataView(buffer);
                let offset = payloadOffset;
                if (view.getUint32(offset, false) !== 0x444c5354) throw new Error('invalid display-list payload');
                const version = view.getUint16(offset + 4, true);
                if (version !== 1) throw new Error(`unsupported display-list version ${version}`);
                const resourceSequence = view.getUint32(offset + 8, true);
                const resourceCount = view.getUint32(offset + 12, true);
                const commandCount = view.getUint32(offset + 16, true);
                const hueTextureId = view.getUint32(offset + 20, true);
                const lightTextureId = view.getUint32(offset + 24, true);
                const commandBytes = view.getUint32(offset + 28, true);
                offset += 32;
                for (let i = 0; i < resourceCount; i++) {
                  const kind = view.getUint8(offset);
                  const flags = view.getUint8(offset + 1);
                  const payloadLength = view.getUint32(offset + 4, true);
                  const id = view.getUint32(offset + 12, true);
                  const payload = offset + 16;
                  const end = payload + payloadLength;
                  if (end > buffer.byteLength) throw new Error('truncated display-list resource');
                  if (kind === 1) {
                    this.createResource(id, view.getInt32(payload, true), view.getInt32(payload + 4, true), flags);
                  } else if (kind === 2) {
                    const x = view.getInt32(payload, true);
                    const y = view.getInt32(payload + 4, true);
                    const width = view.getInt32(payload + 8, true);
                    const height = view.getInt32(payload + 12, true);
                    const dataLength = view.getInt32(payload + 16, true);
                    if (dataLength !== width * height * 4 || payload + 20 + dataLength !== end) throw new Error('invalid texture patch length');
                    this.patchResource(id, x, y, width, height, new Uint8Array(buffer, payload + 20, dataLength));
                  } else if (kind === 3) {
                    if (payloadLength !== 0) throw new Error('invalid texture delete length');
                    this.deleteResource(id);
                  }
                  offset = end;
                }
                if (this.canvas.width !== frameWidth || this.canvas.height !== frameHeight) {
                  this.canvas.width = frameWidth;
                  this.canvas.height = frameHeight;
                }
                this.frameWidth = frameWidth;
                this.frameHeight = frameHeight;
                this.hueTextureId = hueTextureId;
                this.lightTextureId = lightTextureId;
                this.setTarget(0);
                const commandsEnd = offset + commandBytes;
                let batches = 0;
                for (let i = 0; i < commandCount; i++) {
                  if (offset + 8 > commandsEnd) throw new Error('truncated display-list command');
                  const kind = view.getUint8(offset);
                  const flags = view.getUint8(offset + 1);
                  const payloadLength = view.getUint32(offset + 4, true);
                  const payload = offset + 8;
                  const end = payload + payloadLength;
                  if (end > commandsEnd) throw new Error('invalid display-list command length');
                  if (kind === 1) this.setTarget(view.getUint32(payload, true));
                  else if (kind === 2) this.clear(flags, view, payload);
                  else if (kind === 3) { this.draw(flags, view, buffer, payload); batches++; }
                  offset = end;
                }
                if (offset !== commandsEnd) throw new Error('display-list command byte count mismatch');
                return { resourceSequence, resourceCount, commandCount, batches };
              }

              setTarget(id) {
                this.currentTarget = id;
                if (id === 0) {
                  this.gl.bindFramebuffer(this.gl.FRAMEBUFFER, null);
                  this.targetWidth = this.frameWidth || this.canvas.width;
                  this.targetHeight = this.frameHeight || this.canvas.height;
                  this.targetFlip = 1;
                } else {
                  const resource = this.resources.get(id);
                  if (!resource?.renderTarget) throw new Error(`unknown render target ${id}`);
                  this.gl.bindFramebuffer(this.gl.FRAMEBUFFER, resource.framebuffer);
                  this.targetWidth = resource.width;
                  this.targetHeight = resource.height;
                  this.targetFlip = -1;
                }
                this.gl.viewport(0, 0, this.targetWidth, this.targetHeight);
              }

              clear(flags, view, payload) {
                this.gl.disable(this.gl.SCISSOR_TEST);
                let mask = 0;
                if (flags & 1) {
                  this.gl.colorMask(true, true, true, true);
                  this.gl.clearColor(view.getUint8(payload) / 255, view.getUint8(payload + 1) / 255, view.getUint8(payload + 2) / 255, view.getUint8(payload + 3) / 255);
                  mask |= this.gl.COLOR_BUFFER_BIT;
                }
                if (flags & 2) {
                  this.gl.depthMask(true);
                  this.gl.clearDepth(view.getFloat32(payload + 4, true));
                  mask |= this.gl.DEPTH_BUFFER_BIT;
                }
                if (flags & 4) {
                  this.gl.stencilMask(0xff);
                  this.gl.clearStencil(view.getInt32(payload + 8, true));
                  mask |= this.gl.STENCIL_BUFFER_BIT;
                }
                this.gl.clear(mask);
              }

              draw(flags, view, buffer, payload) {
                const textureId = view.getUint32(payload, true);
                const spriteCount = view.getUint32(payload + 4, true);
                const vx = view.getInt32(payload + 8, true);
                const vy = view.getInt32(payload + 12, true);
                const vw = view.getInt32(payload + 16, true);
                const vh = view.getInt32(payload + 20, true);
                const sx = view.getInt32(payload + 24, true);
                const sy = view.getInt32(payload + 28, true);
                const sw = view.getInt32(payload + 32, true);
                const sh = view.getInt32(payload + 36, true);
                const colorSource = view.getUint8(payload + 40);
                const colorDestination = view.getUint8(payload + 41);
                const colorFunction = view.getUint8(payload + 42);
                const alphaSource = view.getUint8(payload + 43);
                const alphaDestination = view.getUint8(payload + 44);
                const alphaFunction = view.getUint8(payload + 45);
                const depthFunction = view.getUint8(payload + 46);
                const textureFilter = view.getUint8(payload + 47);
                const brightlight = view.getFloat32(payload + 52, true);
                const texture = this.resources.get(textureId);
                if (!texture) throw new Error(`draw references unknown texture ${textureId}`);
                this.gl.viewport(vx, this.targetFlip > 0 ? this.targetHeight - vy - vh : vy, vw, vh);
                if (flags & 1) {
                  this.gl.enable(this.gl.SCISSOR_TEST);
                  this.gl.scissor(sx, this.targetFlip > 0 ? this.targetHeight - sy - sh : sy, sw, sh);
                } else this.gl.disable(this.gl.SCISSOR_TEST);
                this.gl.enable(this.gl.BLEND);
                this.gl.blendColor(view.getUint8(payload + 48) / 255, view.getUint8(payload + 49) / 255, view.getUint8(payload + 50) / 255, view.getUint8(payload + 51) / 255);
                this.gl.blendFuncSeparate(this.blendFactor(colorSource, false), this.blendFactor(colorDestination, false), this.blendFactor(alphaSource, true), this.blendFactor(alphaDestination, true));
                this.gl.blendEquationSeparate(this.blendEquation(colorFunction), this.blendEquation(alphaFunction));
                if (flags & 2) {
                  this.gl.enable(this.gl.DEPTH_TEST);
                  this.gl.depthFunc(this.compareFunction(depthFunction));
                } else this.gl.disable(this.gl.DEPTH_TEST);
                this.gl.depthMask((flags & 4) !== 0);
                this.gl.useProgram(this.program);
                this.gl.uniform1f(this.uniforms.flipY, this.targetFlip);
                this.gl.uniform1f(this.uniforms.brightlight, brightlight);
                this.gl.uniform2f(this.uniforms.texelSize, 1 / texture.width, 1 / texture.height);
                this.bindTexture(0, texture.texture, textureFilter === 1 ? this.gl.NEAREST : this.gl.LINEAR);
                this.bindTexture(1, this.resources.get(this.hueTextureId)?.texture || this.fallbackTexture, this.gl.NEAREST);
                this.bindTexture(2, this.resources.get(this.lightTextureId)?.texture || this.fallbackTexture, this.gl.NEAREST);
                this.gl.uniform1i(this.uniforms.drawSampler, 0);
                this.gl.uniform1i(this.uniforms.hueSampler, 1);
                this.gl.uniform1i(this.uniforms.lightSampler, 2);
                const vertexOffset = payload + 56;
                const vertices = new Float32Array(buffer, vertexOffset, spriteCount * 4 * 11);
                this.gl.bindVertexArray(this.vertexArray);
                this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.vertexBuffer);
                this.gl.bufferData(this.gl.ARRAY_BUFFER, vertices, this.gl.STREAM_DRAW);
                this.ensureIndices(spriteCount);
                this.gl.drawElements(this.gl.TRIANGLES, spriteCount * 6, this.gl.UNSIGNED_SHORT, 0);
                this.gl.bindVertexArray(null);
              }

              bindTexture(unit, texture, filter) {
                this.gl.activeTexture(this.gl.TEXTURE0 + unit);
                this.gl.bindTexture(this.gl.TEXTURE_2D, texture);
                this.setTextureSampling(filter);
              }

              ensureIndices(spriteCount) {
                if (spriteCount <= this.indexCapacity) return;
                this.indexCapacity = spriteCount;
                const indices = new Uint16Array(spriteCount * 6);
                for (let i = 0; i < spriteCount; i++) {
                  const vertex = i * 4;
                  const index = i * 6;
                  indices[index] = vertex;
                  indices[index + 1] = vertex + 1;
                  indices[index + 2] = vertex + 2;
                  indices[index + 3] = vertex + 1;
                  indices[index + 4] = vertex + 3;
                  indices[index + 5] = vertex + 2;
                }
                this.gl.bindBuffer(this.gl.ELEMENT_ARRAY_BUFFER, this.indexBuffer);
                this.gl.bufferData(this.gl.ELEMENT_ARRAY_BUFFER, indices, this.gl.STATIC_DRAW);
              }

              blendFactor(value, alpha) {
                return [this.gl.ONE, this.gl.ZERO, this.gl.SRC_COLOR, this.gl.ONE_MINUS_SRC_COLOR, this.gl.SRC_ALPHA, this.gl.ONE_MINUS_SRC_ALPHA, this.gl.DST_COLOR, this.gl.ONE_MINUS_DST_COLOR, this.gl.DST_ALPHA, this.gl.ONE_MINUS_DST_ALPHA, alpha ? this.gl.CONSTANT_ALPHA : this.gl.CONSTANT_COLOR, alpha ? this.gl.ONE_MINUS_CONSTANT_ALPHA : this.gl.ONE_MINUS_CONSTANT_COLOR, this.gl.SRC_ALPHA_SATURATE][value] ?? this.gl.ONE;
              }

              blendEquation(value) {
                return [this.gl.FUNC_ADD, this.gl.FUNC_SUBTRACT, this.gl.FUNC_REVERSE_SUBTRACT, this.gl.MAX, this.gl.MIN][value] ?? this.gl.FUNC_ADD;
              }

              compareFunction(value) {
                return [this.gl.ALWAYS, this.gl.NEVER, this.gl.LESS, this.gl.LEQUAL, this.gl.EQUAL, this.gl.GEQUAL, this.gl.GREATER, this.gl.NOTEQUAL][value] ?? this.gl.ALWAYS;
              }
            }

            function connect() {
              const scheme = location.protocol === 'https:' ? 'wss' : 'ws';
              socket = new WebSocket(`${scheme}://${location.host}/ws`);
              socket.binaryType = 'arraybuffer';
              socket.onopen = () => {
                status.textContent = 'connected · waiting for frame';
                viewport.focus();
                send({ type: 'viewerReady', resourceSequence: 0 });
              };
              socket.onclose = () => { status.textContent = 'disconnected · reconnecting…'; setTimeout(connect, 1000); };
              socket.onerror = () => socket.close();
              socket.onmessage = event => {
                if (!(event.data instanceof ArrayBuffer) || event.data.byteLength < HEADER_SIZE) return;
                const view = new DataView(event.data);
                if (view.getUint32(0, false) !== 0x54554f46) return;
                const messageType = view.getUint8(5);
                const messageFlags = view.getUint8(6);
                if (view.getUint8(4) !== 1 || ![1, 2, 3].includes(messageType) || view.getUint8(7) !== 0 || (messageFlags & ~1) !== 0 || (messageFlags !== 0 && messageType !== 3)) return;
                const nextFrame = {
                  messageType,
                  flags: messageFlags,
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

            function show(canvas) {
              activeCanvas = canvas;
              pixelCanvas.hidden = canvas !== pixelCanvas;
              commandCanvas.hidden = canvas !== commandCanvas;
            }

            async function drawLatest() {
              if (drawing) return;
              drawing = true;
              try {
                while (latestPendingFrame) {
                  const frame = latestPendingFrame;
                  latestPendingFrame = null;
                  if (frame.messageType === 3) {
                    commandRenderer ??= new DisplayListRenderer(commandCanvas);
                    let displayList = frame.bytes;
                    let payloadOffset = HEADER_SIZE;
                    if (frame.flags & 1) {
                      if (typeof DecompressionStream !== 'function') throw new Error('compressed display lists require DecompressionStream support');
                      const compressed = new Blob([frame.bytes.slice(HEADER_SIZE)]).stream();
                      displayList = await new Response(compressed.pipeThrough(new DecompressionStream('deflate'))).arrayBuffer();
                      payloadOffset = 0;
                    }
                    const info = commandRenderer.render(displayList, frame.width, frame.height, payloadOffset);
                    show(commandCanvas);
                    send({ type: 'resourceAck', resourceSequence: info.resourceSequence });
                    lastRenderInfo = ` · ${info.commandCount} commands · ${info.resourceCount} resources`;
                  } else {
                    if (pixelCanvas.width !== frame.width || pixelCanvas.height !== frame.height) {
                      pixelCanvas.width = frame.width;
                      pixelCanvas.height = frame.height;
                    }
                    if (frame.messageType === 1) {
                      const bitmap = await createImageBitmap(new Blob([frame.bytes.slice(HEADER_SIZE)], { type: 'image/png' }));
                      pixelContext.drawImage(bitmap, 0, 0);
                      bitmap.close();
                    } else {
                      const pixels = new Uint8ClampedArray(frame.bytes, HEADER_SIZE, frame.payloadLength);
                      pixelContext.putImageData(new ImageData(pixels, frame.width, frame.height), 0, 0);
                    }
                    show(pixelCanvas);
                    lastRenderInfo = frame.messageType === 1 ? ' · PNG framebuffer' : ' · RGBA framebuffer';
                  }
                  frameId = frame.id;
                  const elapsed = Math.max(0.001, (performance.now() - statsStarted) / 1000);
                  status.textContent = `frame ${frameId} · ${frame.width}×${frame.height} · ${(receivedFrames / elapsed).toFixed(1)} fps · ${(receivedBytes / elapsed / 1024).toFixed(0)} KiB/s${lastRenderInfo}`;
                }
              } catch (error) {
                status.textContent = `renderer error · ${error.message}`;
                console.error(error);
              } finally {
                drawing = false;
              }
            }

            function send(message) {
              if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ ...message, frameId }));
            }

            function point(event) {
              const bounds = activeCanvas.getBoundingClientRect();
              return {
                x: (event.clientX - bounds.left) * activeCanvas.width / Math.max(1, bounds.width),
                y: (event.clientY - bounds.top) * activeCanvas.height / Math.max(1, bounds.height)
              };
            }

            viewport.addEventListener('pointermove', event => send({ type: 'pointerMove', ...point(event) }));
            viewport.addEventListener('pointerdown', event => {
              viewport.focus();
              viewport.setPointerCapture(event.pointerId);
              send({ type: 'pointerDown', button: event.button, ...point(event) });
              event.preventDefault();
            });
            viewport.addEventListener('pointerup', event => {
              send({ type: 'pointerUp', button: event.button, ...point(event) });
              if (viewport.hasPointerCapture(event.pointerId)) viewport.releasePointerCapture(event.pointerId);
              event.preventDefault();
            });
            viewport.addEventListener('wheel', event => {
              send({ type: 'wheel', deltaY: event.deltaY, ...point(event) });
              event.preventDefault();
            }, { passive: false });
            viewport.addEventListener('contextmenu', event => event.preventDefault());

            function keyboardMessage(type, event) {
              send({ type, code: event.code, key: event.key, altKey: event.altKey, ctrlKey: event.ctrlKey, shiftKey: event.shiftKey, metaKey: event.metaKey });
            }

            viewport.addEventListener('keydown', event => {
              keyboardMessage('keyDown', event);
              if (!event.ctrlKey && !event.altKey && !event.metaKey && event.key.length === 1) send({ type: 'text', text: event.key });
              event.preventDefault();
            });
            viewport.addEventListener('keyup', event => { keyboardMessage('keyUp', event); event.preventDefault(); });
            connect();
          </script>
        </body>
        </html>
        """;
}
