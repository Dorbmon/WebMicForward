const TARGET_RATE = 48000;
const FRAME_SAMPLES = 960;
const MAGIC = [0x57, 0x4d, 0x46, 0x31]; // WMF1
const PRE_ROLL_FRAMES = 4;
const MAX_BUFFERED_BYTES = 512 * 1024;

const $ = (id) => document.getElementById(id);

const elements = {
  roomInput: $("roomInput"),
  tokenInput: $("tokenInput"),
  transportSelect: $("transportSelect"),
  connectButton: $("connectButton"),
  disconnectButton: $("disconnectButton"),
  startMicButton: $("startMicButton"),
  stopMicButton: $("stopMicButton"),
  thresholdInput: $("thresholdInput"),
  hangoverInput: $("hangoverInput"),
  gateInput: $("gateInput"),
  statusLine: $("statusLine"),
  audioLine: $("audioLine"),
  waveCanvas: $("waveCanvas"),
  thresholdValue: $("thresholdValue"),
  hangoverValue: $("hangoverValue"),
  gateState: $("gateState"),
  rmsValue: $("rmsValue"),
  sentValue: $("sentValue"),
  skippedValue: $("skippedValue"),
  clientValue: $("clientValue")
};

const state = {
  ws: null,
  realtimeEnabled: false,
  transportMode: "ws",
  sfu: null,
  stream: null,
  audioContext: null,
  sourceNode: null,
  workletNode: null,
  silentGain: null,
  analyser: null,
  drawId: 0,
  seq: 0,
  gateOpen: false,
  hangoverLeft: 0,
  preRoll: [],
  lastRms: 0,
  lastPeak: 0,
  lastStatusSent: 0,
  stats: {
    sentBytes: 0,
    sentFrames: 0,
    skippedFrames: 0,
    droppedFrames: 0,
    clients: 0
  }
};

function initRoom() {
  const params = new URLSearchParams(location.search);
  const room = params.get("room") || crypto.randomUUID().slice(0, 8);
  const token = params.get("token") || "";
  elements.roomInput.value = room;
  elements.tokenInput.value = token;
}

async function initConfig() {
  try {
    const response = await fetch("/api/config");
    const config = await response.json();
    state.realtimeEnabled = Boolean(config.realtimeEnabled);
    elements.transportSelect.value = state.realtimeEnabled ? "sfu" : "ws";
    elements.transportSelect.querySelector('option[value="sfu"]').disabled = !state.realtimeEnabled;
  } catch {
    state.realtimeEnabled = false;
    elements.transportSelect.value = "ws";
    elements.transportSelect.querySelector('option[value="sfu"]').disabled = true;
  }
}

function setStatus(text, className = "") {
  elements.statusLine.textContent = text;
  elements.statusLine.className = className;
}

function setAudioLine(text, className = "") {
  elements.audioLine.textContent = text;
  elements.audioLine.className = className;
}

function buildWsUrl() {
  const wsUrl = new URL("/ws", location.href);
  wsUrl.protocol = location.protocol === "https:" ? "wss:" : "ws:";
  wsUrl.searchParams.set("room", elements.roomInput.value.trim());
  wsUrl.searchParams.set("role", "source");

  const token = elements.tokenInput.value.trim();
  if (token) {
    wsUrl.searchParams.set("token", token);
  }

  return wsUrl;
}

function connect() {
  const room = elements.roomInput.value.trim();
  if (!room) {
    setStatus("请输入房间名", "is-error");
    return;
  }

  disconnect();

  const ws = new WebSocket(buildWsUrl());
  ws.binaryType = "arraybuffer";
  state.ws = ws;
  setStatus("连接中...");

  ws.addEventListener("open", () => {
    elements.connectButton.disabled = true;
    elements.disconnectButton.disabled = false;
    elements.roomInput.disabled = true;
    elements.tokenInput.disabled = true;
    elements.transportSelect.disabled = true;
    setStatus("已连接", "is-live");
    sendJson({ type: "source-status", gateOpen: state.gateOpen, rms: state.lastRms });
  });

  ws.addEventListener("message", (event) => {
    if (typeof event.data !== "string") {
      return;
    }

    try {
      const message = JSON.parse(event.data);
      if (message.type === "peers" && message.roles) {
        state.stats.clients = Number(message.roles.client || 0);
      }
    } catch {
      // Ignore non-JSON control messages from older deployments.
    }
  });

  ws.addEventListener("close", () => {
    if (state.ws === ws) {
      state.ws = null;
    }
    elements.connectButton.disabled = false;
    elements.disconnectButton.disabled = true;
    elements.roomInput.disabled = false;
    elements.tokenInput.disabled = false;
    elements.transportSelect.disabled = false;
    setStatus("未连接");
  });

  ws.addEventListener("error", () => {
    setStatus("连接错误", "is-error");
  });
}

function disconnect() {
  if (state.ws) {
    state.ws.close(1000, "source disconnect");
    state.ws = null;
  }
  elements.connectButton.disabled = false;
  elements.disconnectButton.disabled = true;
  elements.roomInput.disabled = false;
  elements.tokenInput.disabled = false;
  elements.transportSelect.disabled = false;
}

async function startMic() {
  if (!navigator.mediaDevices?.getUserMedia) {
    setAudioLine("浏览器不支持麦克风采集", "is-error");
    return;
  }

  await stopMic();
  state.transportMode = currentTransportMode();

  const stream = await navigator.mediaDevices.getUserMedia({
    audio: {
      channelCount: 1,
      echoCancellation: false,
      noiseSuppression: false,
      autoGainControl: false
    }
  });

  const AudioContextCtor = window.AudioContext || window.webkitAudioContext;
  const audioContext = new AudioContextCtor({
    latencyHint: "interactive",
    sampleRate: TARGET_RATE
  });
  await audioContext.audioWorklet.addModule("/audio-worklet.js");

  const sourceNode = audioContext.createMediaStreamSource(stream);
  const analyser = audioContext.createAnalyser();
  analyser.fftSize = 2048;

  const workletNode = new AudioWorkletNode(audioContext, "mic-forward-processor", {
    numberOfInputs: 1,
    numberOfOutputs: 1,
    outputChannelCount: [1],
    processorOptions: {
      targetRate: TARGET_RATE,
      frameSamples: FRAME_SAMPLES
    }
  });
  const silentGain = audioContext.createGain();
  silentGain.gain.value = 0;

  workletNode.port.onmessage = (event) => {
    if (event.data?.type === "frame") {
      handleAudioFrame(event.data.frame);
    }
  };

  sourceNode.connect(analyser);
  sourceNode.connect(workletNode);
  workletNode.connect(silentGain).connect(audioContext.destination);

  let sfu = null;
  if (state.transportMode === "sfu") {
    sfu = await startRealtimePublish(stream);
  }

  Object.assign(state, {
    stream,
    sfu,
    audioContext,
    sourceNode,
    workletNode,
    silentGain,
    analyser,
    gateOpen: false,
    hangoverLeft: 0,
    preRoll: []
  });

  elements.startMicButton.disabled = true;
  elements.stopMicButton.disabled = false;
  setAudioLine(`${Math.round(audioContext.sampleRate)} Hz / mono / ${state.transportMode.toUpperCase()}`, "is-live");
  startDrawing();
}

async function stopMic() {
  cancelAnimationFrame(state.drawId);
  await stopRealtimePublish();

  if (state.workletNode) {
    state.workletNode.port.onmessage = null;
    state.workletNode.disconnect();
  }
  if (state.silentGain) {
    state.silentGain.disconnect();
  }
  if (state.sourceNode) {
    state.sourceNode.disconnect();
  }
  if (state.stream) {
    for (const track of state.stream.getTracks()) {
      track.stop();
    }
  }
  if (state.audioContext && state.audioContext.state !== "closed") {
    void state.audioContext.close();
  }

  Object.assign(state, {
    stream: null,
    audioContext: null,
    sourceNode: null,
    workletNode: null,
    silentGain: null,
    analyser: null,
    sfu: null,
    gateOpen: false,
    hangoverLeft: 0,
    preRoll: []
  });

  elements.startMicButton.disabled = false;
  elements.stopMicButton.disabled = true;
  setAudioLine("麦克风未启动");
  drawIdleWaveform();
}

function currentTransportMode() {
  if (elements.transportSelect.value === "sfu" && state.realtimeEnabled) {
    return "sfu";
  }
  return "ws";
}

async function startRealtimePublish(stream) {
  const track = stream.getAudioTracks()[0];
  if (!track) {
    throw new Error("没有可发布的音频轨道");
  }

  const room = elements.roomInput.value.trim();
  const token = elements.tokenInput.value.trim();
  const trackName = `mic-${room}-${crypto.randomUUID().slice(0, 8)}`;
  const pc = new RTCPeerConnection({
    iceServers: [{ urls: "stun:stun.cloudflare.com:3478" }]
  });

  try {
    pc.addEventListener("connectionstatechange", () => {
      if (state.transportMode === "sfu" && state.sfu?.pc === pc) {
        setStatus(`SFU ${pc.connectionState}`, pc.connectionState === "connected" ? "is-live" : "");
      }
    });

    const transceiver = pc.addTransceiver(track, { direction: "sendonly" });
    const offer = await pc.createOffer();
    await pc.setLocalDescription({
      type: offer.type,
      sdp: enableOpusDtx(offer.sdp || "")
    });
    await waitForIceGatheringComplete(pc, 2500);

    const mid = transceiver.mid;
    if (!mid || !pc.localDescription?.sdp) {
      throw new Error("浏览器没有生成可用的 WebRTC mid/SDP");
    }

    const published = await postJson("/api/realtime/publish", {
      room,
      token,
      trackName,
      mid,
      type: pc.localDescription.type,
      sdp: pc.localDescription.sdp
    });

    if (published.sessionDescription) {
      await pc.setRemoteDescription(published.sessionDescription);
    }

    const sfu = {
      pc,
      track,
      sessionId: published.sessionId,
      trackName: published.trackName || trackName,
      adapterId: null
    };

    if (location.protocol === "https:") {
      const adapter = await postJson("/api/realtime/adapter/start", {
        room,
        token,
        sessionId: sfu.sessionId,
        trackName: sfu.trackName
      });
      sfu.adapterId = adapter.tracks?.[0]?.adapterId || null;
    } else {
      setStatus("SFU 已发布；本地 HTTP 不能启动 adapter", "is-quiet");
    }

    return sfu;
  } catch (error) {
    pc.close();
    throw error;
  }
}

async function stopRealtimePublish() {
  const sfu = state.sfu;
  state.sfu = null;
  if (!sfu) {
    return;
  }

  if (sfu.adapterId) {
    try {
      await postJson("/api/realtime/adapter/close", {
        token: elements.tokenInput.value.trim(),
        adapterId: sfu.adapterId
      });
    } catch (error) {
      console.warn("Failed to close SFU adapter", error);
    }
  }

  sfu.pc.close();
}

async function postJson(url, data) {
  const response = await fetch(url, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(data)
  });

  let parsed = {};
  try {
    parsed = await response.json();
  } catch {
    // Keep the default object for non-JSON failures.
  }

  if (!response.ok) {
    throw new Error(parsed.error || `请求失败: ${response.status}`);
  }

  return parsed;
}

function waitForIceGatheringComplete(pc, timeoutMs) {
  if (pc.iceGatheringState === "complete") {
    return Promise.resolve();
  }

  return new Promise((resolve) => {
    const timeout = setTimeout(done, timeoutMs);
    pc.addEventListener("icegatheringstatechange", onStateChange);

    function onStateChange() {
      if (pc.iceGatheringState === "complete") {
        done();
      }
    }

    function done() {
      clearTimeout(timeout);
      pc.removeEventListener("icegatheringstatechange", onStateChange);
      resolve();
    }
  });
}

function enableOpusDtx(sdp) {
  const opus = sdp.match(/^a=rtpmap:(\d+) opus\/48000\/2$/im);
  if (!opus) {
    return sdp;
  }

  const payloadType = opus[1];
  const fmtp = new RegExp(`^a=fmtp:${payloadType} .*$`, "im");
  if (fmtp.test(sdp)) {
    return sdp.replace(fmtp, (line) => (line.includes("usedtx=1") ? line : `${line};usedtx=1`));
  }

  return sdp.replace(opus[0], `${opus[0]}\r\na=fmtp:${payloadType} usedtx=1`);
}

function handleAudioFrame(frame) {
  const { rms, peak } = measureFrame(frame);
  state.lastRms = rms;
  state.lastPeak = peak;

  if (state.transportMode === "sfu") {
    handleRealtimeAudioGate(rms, peak);
    maybeSendSourceStatus();
    return;
  }

  const threshold = Number(elements.thresholdInput.value);
  const hangoverFrames = Math.round(Number(elements.hangoverInput.value) / 20);
  const gateEnabled = elements.gateInput.checked;
  const hasVoice = rms >= threshold || peak >= threshold * 3.2;

  if (!gateEnabled) {
    state.gateOpen = true;
    sendFrame(frame);
    maybeSendSourceStatus();
    return;
  }

  if (hasVoice) {
    if (!state.gateOpen) {
      state.gateOpen = true;
      flushPreRoll();
    }
    state.hangoverLeft = hangoverFrames;
    sendFrame(frame);
  } else if (state.gateOpen && state.hangoverLeft > 0) {
    state.hangoverLeft -= 1;
    sendFrame(frame);
  } else {
    if (state.gateOpen) {
      state.gateOpen = false;
      maybeSendSourceStatus(true);
    }
    rememberPreRoll(frame);
    state.stats.skippedFrames += 1;
  }

  maybeSendSourceStatus();
}

function handleRealtimeAudioGate(rms, peak) {
  const threshold = Number(elements.thresholdInput.value);
  const hangoverFrames = Math.round(Number(elements.hangoverInput.value) / 20);
  const gateEnabled = elements.gateInput.checked;
  const hasVoice = rms >= threshold || peak >= threshold * 3.2;

  if (!state.sfu?.track) {
    return;
  }

  if (!gateEnabled) {
    state.gateOpen = true;
    state.sfu.track.enabled = true;
    state.stats.sentFrames += 1;
    return;
  }

  if (hasVoice) {
    state.gateOpen = true;
    state.hangoverLeft = hangoverFrames;
    state.sfu.track.enabled = true;
    state.stats.sentFrames += 1;
  } else if (state.gateOpen && state.hangoverLeft > 0) {
    state.hangoverLeft -= 1;
    state.sfu.track.enabled = true;
    state.stats.sentFrames += 1;
  } else {
    state.gateOpen = false;
    state.sfu.track.enabled = false;
    state.stats.skippedFrames += 1;
  }
}

function measureFrame(frame) {
  let sum = 0;
  let peak = 0;
  for (let i = 0; i < frame.length; i += 1) {
    const value = frame[i];
    sum += value * value;
    peak = Math.max(peak, Math.abs(value));
  }
  return {
    rms: Math.sqrt(sum / frame.length),
    peak
  };
}

function rememberPreRoll(frame) {
  state.preRoll.push(new Float32Array(frame));
  while (state.preRoll.length > PRE_ROLL_FRAMES) {
    state.preRoll.shift();
  }
}

function flushPreRoll() {
  for (const frame of state.preRoll) {
    sendFrame(frame);
  }
  state.preRoll = [];
}

function sendFrame(frame) {
  const ws = state.ws;
  if (!ws || ws.readyState !== WebSocket.OPEN) {
    return;
  }

  if (ws.bufferedAmount > MAX_BUFFERED_BYTES) {
    state.stats.droppedFrames += 1;
    return;
  }

  const packet = encodeFrame(frame);
  ws.send(packet);
  state.stats.sentBytes += packet.byteLength;
  state.stats.sentFrames += 1;
}

function encodeFrame(frame) {
  const bytes = new ArrayBuffer(16 + frame.length * 2);
  const view = new DataView(bytes);
  for (let i = 0; i < MAGIC.length; i += 1) {
    view.setUint8(i, MAGIC[i]);
  }
  view.setUint32(4, state.seq >>> 0, true);
  view.setUint32(8, TARGET_RATE, true);
  view.setUint16(12, 1, true);
  view.setUint16(14, frame.length, true);

  let offset = 16;
  for (let i = 0; i < frame.length; i += 1) {
    const sample = Math.max(-1, Math.min(1, frame[i]));
    view.setInt16(offset, sample < 0 ? sample * 32768 : sample * 32767, true);
    offset += 2;
  }
  state.seq = (state.seq + 1) >>> 0;
  return bytes;
}

function sendJson(data) {
  const ws = state.ws;
  if (ws?.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify(data));
  }
}

function maybeSendSourceStatus(force = false) {
  const now = Date.now();
  if (!force && now - state.lastStatusSent < 5000) {
    return;
  }
  state.lastStatusSent = now;
  sendJson({
    type: "source-status",
    gateOpen: state.gateOpen,
    rms: Number(state.lastRms.toFixed(5)),
    transport: state.transportMode,
    sentFrames: state.stats.sentFrames,
    skippedFrames: state.stats.skippedFrames
  });
}

function startDrawing() {
  const canvas = elements.waveCanvas;
  const context = canvas.getContext("2d");
  const samples = new Uint8Array(state.analyser.fftSize);

  const draw = () => {
    state.drawId = requestAnimationFrame(draw);
    state.analyser.getByteTimeDomainData(samples);
    drawWaveform(context, canvas, samples);
  };

  draw();
}

function drawWaveform(context, canvas, samples) {
  const width = canvas.width;
  const height = canvas.height;
  context.clearRect(0, 0, width, height);

  context.fillStyle = "#090b0d";
  context.fillRect(0, 0, width, height);

  context.strokeStyle = "rgba(255,255,255,0.08)";
  context.lineWidth = 1;
  for (let y = 0; y <= height; y += height / 4) {
    context.beginPath();
    context.moveTo(0, y);
    context.lineTo(width, y);
    context.stroke();
  }

  context.strokeStyle = state.gateOpen ? "#3bc9b5" : "#ffcf5a";
  context.lineWidth = 3;
  context.beginPath();

  for (let i = 0; i < samples.length; i += 1) {
    const x = (i / (samples.length - 1)) * width;
    const y = (samples[i] / 255) * height;
    if (i === 0) {
      context.moveTo(x, y);
    } else {
      context.lineTo(x, y);
    }
  }

  context.stroke();
}

function drawIdleWaveform() {
  const canvas = elements.waveCanvas;
  const context = canvas.getContext("2d");
  context.fillStyle = "#090b0d";
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.strokeStyle = "rgba(255,255,255,0.08)";
  context.lineWidth = 1;
  for (let y = 0; y <= canvas.height; y += canvas.height / 4) {
    context.beginPath();
    context.moveTo(0, y);
    context.lineTo(canvas.width, y);
    context.stroke();
  }
}

function formatBytes(bytes) {
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  if (bytes < 1024 * 1024) {
    return `${(bytes / 1024).toFixed(1)} KB`;
  }
  return `${(bytes / 1024 / 1024).toFixed(2)} MB`;
}

function refreshUi() {
  elements.thresholdValue.textContent = Number(elements.thresholdInput.value).toFixed(3);
  elements.hangoverValue.textContent = `${elements.hangoverInput.value} ms`;
  elements.gateState.textContent = state.gateOpen ? "打开" : "关闭";
  elements.gateState.className = state.gateOpen ? "is-live" : "is-quiet";
  elements.rmsValue.textContent = state.lastRms.toFixed(3);
  elements.sentValue.textContent = state.transportMode === "sfu"
    ? `${state.stats.sentFrames} 帧`
    : formatBytes(state.stats.sentBytes);
  elements.skippedValue.textContent = `${state.stats.skippedFrames}`;
  elements.clientValue.textContent = `${state.stats.clients}`;
}

elements.connectButton.addEventListener("click", connect);
elements.disconnectButton.addEventListener("click", disconnect);
elements.startMicButton.addEventListener("click", () => {
  startMic().catch((error) => {
    console.error(error);
    setAudioLine(error.message || "麦克风启动失败", "is-error");
    stopMic();
  });
});
elements.stopMicButton.addEventListener("click", stopMic);
elements.thresholdInput.addEventListener("input", refreshUi);
elements.hangoverInput.addEventListener("input", refreshUi);
elements.gateInput.addEventListener("change", refreshUi);

initRoom();
void initConfig();
drawIdleWaveform();
refreshUi();
setInterval(refreshUi, 250);
