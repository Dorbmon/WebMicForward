# Web Mic Forward

网页采集麦克风并显示波形，通过 Cloudflare Realtime SFU 或 Worker WebSocket 转发给 Windows 客户端。Windows 客户端把收到的 PCM 音频播放到指定的 Windows 渲染设备；如果该设备是 VB-CABLE / Virtual Audio Cable 的输入端，系统中的配对录音端点就能作为虚拟麦克风使用。

普通 Windows 用户态程序不能直接注册新的麦克风设备。真正的虚拟麦克风需要已安装、已签名的虚拟音频驱动。本项目的客户端负责把网页音频送入这类驱动。

## 部署方式概览

推荐生产部署走 `Realtime SFU`：

```text
Browser microphone
  -> WebRTC / Opus
  -> Cloudflare Realtime SFU
  -> Realtime WebSocket adapter
  -> Worker Durable Object room
  -> Windows client
  -> Virtual audio cable render endpoint
  -> PC recording device
```

本地开发或没有配置 Realtime 时走 `WebSocket PCM` 回退：

```text
Browser microphone
  -> Worker WebSocket room
  -> Windows client
  -> Virtual audio cable render endpoint
```

Realtime SFU 的 WebSocket adapter 要求 Cloudflare 从公网连回一个 `wss://` endpoint，所以完整 SFU 链路必须部署到 Cloudflare 后测试。本地 `http://localhost:8787` 只能完整验证 WebSocket PCM 模式。

## 项目结构

- `worker/src/index.ts`: Worker 入口、Realtime SFU 信令代理、Durable Object 房间转发。
- `web/`: 静态网页、麦克风采集、波形、静音门控、WebRTC 发布。
- `client/WebMicForward.Core/`: Windows 音频设备枚举、WebSocket 接收、音频解包、WASAPI 输出核心库。
- `client/WebMicForward.Client/`: 命令行客户端，适合脚本和调试。
- `client/WebMicForward.Gui/`: Windows GUI 客户端，适合完整安装和配置流程。
- `wrangler.template.toml`: 可上传的 Cloudflare Worker 配置模板。真实 `wrangler.toml` 是本地私有配置，不上传。

## 前置条件

- Node.js 22+ 和 npm。
- .NET 10 SDK。
- Cloudflare 账号，并启用 Workers / Durable Objects。
- 生产 SFU 模式需要一个 Cloudflare Realtime SFU App。
- Windows 上安装 VB-CABLE、Virtual Audio Cable 或同类虚拟音频线缆。
- Windows 客户端运行机器需要能访问部署后的 Worker WebSocket URL。

## 本地验证

安装依赖：

```powershell
npm install
```

启动本地 Worker：

```powershell
npm run dev
```

打开：

```text
http://localhost:8787/?room=test
```

另开一个 PowerShell，查看 Windows 输出设备：

```powershell
dotnet run --project client/WebMicForward.Client -- --list-devices
```

启动客户端。没有虚拟线缆时可以先不传 `--device`，音频会输出到默认播放设备，便于验证链路。

```powershell
dotnet run --project client/WebMicForward.Client -- --server ws://localhost:8787/ws --room test --device "CABLE Input"
```

网页上点击 `连接`，确认状态变为已连接，再点击 `启动麦克风`。本地未配置 Realtime 时页面会自动选择 `WebSocket PCM`。

## Windows GUI 完整流程

GUI 客户端把常用流程串到一个窗口里：

- 检测是否已安装类似 `CABLE Input` 的虚拟线缆播放端点。
- 打开 VB-CABLE 官方下载页。
- 打开 Windows 声音设置。
- 配置 Worker WebSocket URL、房间、token、延迟和输出设备。
- 复制或打开对应网页 URL。
- 一键启动和停止 Windows 接收端。

启动 GUI：

```powershell
npm run gui:build
dotnet run --project client/WebMicForward.Gui
```

发布单文件 GUI：

```powershell
npm run gui:publish
```

发布产物在：

```text
client/WebMicForward.Gui/bin/Release/net10.0-windows/win-x64/publish/
```

推荐使用顺序：

1. 打开 GUI。
2. 如果顶部提示没有虚拟线缆，点击 `Install / download virtual cable`，下载并安装 VB-CABLE。安装驱动时需要管理员权限，安装后通常需要重启。
3. 回到 GUI，点击 `Refresh devices`，确认检测到 `CABLE Input`。
4. 填写 `Worker WebSocket URL`，例如 `wss://web-mic-forward.<your-subdomain>.workers.dev/ws`。
5. 填写 `Room`，必要时填写 `Token`。
6. 点击 `Open web page`，浏览器会打开匹配的网页房间。
7. 点击 GUI 的 `Start receiver`。
8. 在网页点击 `连接` 和 `启动麦克风`。
9. 在目标应用里选择虚拟线缆录音端点，例如 `CABLE Output`。

GUI 会把配置保存到：

```text
%APPDATA%\WebMicForward\gui-settings.json
```

## Cloudflare 部署指南

### 1. 登录 Wrangler

```powershell
npx wrangler login
npx wrangler whoami
```

### 2. 创建 Realtime SFU App

在 Cloudflare Dashboard 创建 Realtime SFU App，记录：

- Realtime SFU App ID
- Realtime SFU App Secret

App Secret 只放到 Worker secret，不能写进前端代码，也不要提交到 Git。

### 3. 配置本地 `wrangler.toml`

真实 `wrangler.toml` 可能包含自定义域名、房间 token、Realtime App ID 等本地配置，本仓库默认不上传它。第一次部署前先复制模板：

```powershell
Copy-Item wrangler.template.toml wrangler.toml
```

如果只部署 WebSocket PCM 模式，可以不配置 Realtime 变量。

如果要启用 Realtime SFU，在 `wrangler.toml` 中配置：

```toml
[vars]
SFU_API_BASE = "https://rtc.live.cloudflare.com/v1"
REALTIME_SFU_APP_ID = "<your-realtime-sfu-app-id>"
```

如果已有 `[vars]` 段，把这两行合并进去，不要重复创建多个 `[vars]`。

### 4. 写入 Secrets

启用 Realtime SFU：

```powershell
npx wrangler secret put REALTIME_SFU_BEARER_TOKEN
```

提示输入时粘贴 Realtime SFU App Secret。

可选：给房间加共享令牌。启用后，网页和 Windows 客户端都必须传同一个 token。

```powershell
npx wrangler secret put ROOM_TOKEN
```

### 5. 部署前检查

```powershell
npm run typecheck
npm audit --audit-level=high
npx wrangler deploy --dry-run
dotnet build client/WebMicForward.Client/WebMicForward.Client.csproj
```

### 6. 部署 Worker

```powershell
npm run deploy
```

部署完成后记录 Worker URL，例如：

```text
https://web-mic-forward.<your-subdomain>.workers.dev
```

### 7. 构建 Windows 客户端

命令行客户端开发机直接运行：

```powershell
dotnet run --project client/WebMicForward.Client -- --help
```

GUI 客户端开发机直接运行：

```powershell
dotnet run --project client/WebMicForward.Gui
```

发布命令行客户端 runtime-dependent 版本：

```powershell
dotnet publish client/WebMicForward.Client/WebMicForward.Client.csproj -c Release -r win-x64 --self-contained false
```

发布单文件自包含版本：

```powershell
dotnet publish client/WebMicForward.Client/WebMicForward.Client.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

发布 GUI 单文件自包含版本：

```powershell
npm run gui:publish
```

发布产物在：

```text
client/WebMicForward.Client/bin/Release/net10.0-windows/win-x64/publish/
client/WebMicForward.Gui/bin/Release/net10.0-windows/win-x64/publish/
```

### 8. 运行生产链路

网页：

```text
https://web-mic-forward.<your-subdomain>.workers.dev/?room=my-room
```

如果配置了 `ROOM_TOKEN`：

```text
https://web-mic-forward.<your-subdomain>.workers.dev/?room=my-room&token=<token>
```

Windows 客户端：

```powershell
dotnet run --project client/WebMicForward.Client -- --server wss://web-mic-forward.<your-subdomain>.workers.dev/ws --room my-room --device "CABLE Input"
```

也可以用 GUI：填入同一个 `Worker WebSocket URL` 和 `Room`，选择 `CABLE Input`，点击 `Start receiver`。

如果配置了 `ROOM_TOKEN`：

```powershell
dotnet run --project client/WebMicForward.Client -- --server wss://web-mic-forward.<your-subdomain>.workers.dev/ws --room my-room --token <token> --device "CABLE Input"
```

启动顺序建议：

1. 先启动 Windows 客户端。
2. 打开网页，确认房间名和 token。
3. 点击网页 `连接`。
4. 点击网页 `启动麦克风`。
5. 在 Windows 的目标应用中选择虚拟线缆的录音端点，例如 `CABLE Output`。

## 传输模式

### Realtime SFU

部署后，如果 Worker 配置了 `REALTIME_SFU_APP_ID` 和 `REALTIME_SFU_BEARER_TOKEN`，网页会优先选择 `Realtime SFU`。

该模式下：

- 浏览器用 WebRTC/Opus 发布麦克风音轨。
- Worker 代理 SDP 到 Cloudflare Realtime API，secret 不会下发到浏览器。
- Worker 创建 Realtime WebSocket adapter，把 SFU 音轨转成 48 kHz stereo PCM protobuf 包。
- Durable Object 把 adapter 的二进制包转发给 Windows 客户端。

### WebSocket PCM

没有配置 Realtime，或手动选择 `WebSocket PCM` 时使用该模式。

该模式下：

- 浏览器每 20ms 生成一帧 48 kHz mono PCM16。
- 静音门控在浏览器端执行。
- 静音时不向 Worker 发送 PCM 帧。
- Windows 客户端会把 mono PCM 复制成 stereo 输出。

## 省流和成本控制

`WebSocket PCM` 模式省流最直接：低于门限时不发送音频帧，客户端本地补零。

`Realtime SFU` 模式下，浏览器使用 WebRTC/Opus，页面会在静音时禁用音轨并请求 Opus DTX，尽量降低空闲码率。Cloudflare Realtime 的 track 会在长时间无媒体包时回收，因此不做“完全断流但保持同一轨道永久存活”的设计。

Cloudflare Realtime SFU 按 Cloudflare 到客户端或 adapter 的 egress 计费。浏览器推到 Cloudflare 的上行不计 SFU egress，但 SFU 通过 WebSocket adapter 发到 Worker/客户端方向会计入 egress。WebSocket adapter 当前是 beta；以 Cloudflare 当前官方计费页为准。

降低费用的建议：

- 不使用时停止网页麦克风。
- 保持 `静音省流` 开启。
- 只给一个 Windows 客户端接收同一房间。
- 长时间无人使用时关闭 Windows 客户端。
- 用 `ROOM_TOKEN` 避免别人误连房间。

## 虚拟麦克风设置

以 VB-CABLE 为例：

1. 安装 VB-CABLE。
2. Windows 客户端选择渲染端点 `CABLE Input`。
3. 会议软件、游戏或录音软件选择录音端点 `CABLE Output`。

查看设备名：

```powershell
dotnet run --project client/WebMicForward.Client -- --list-devices
```

按索引选择设备：

```powershell
dotnet run --project client/WebMicForward.Client -- --server wss://web-mic-forward.<your-subdomain>.workers.dev/ws --room my-room --device 3
```

按名称片段选择设备：

```powershell
dotnet run --project client/WebMicForward.Client -- --server wss://web-mic-forward.<your-subdomain>.workers.dev/ws --room my-room --device "CABLE Input"
```

## 音频包格式

`WebSocket PCM` 的每个二进制 WebSocket 消息是一帧音频。当前网页发送 `WMF2`，多带一个源端发送时间戳用于接收端估算延迟：

```text
0..3    ASCII "WMF2"
4..7    uint32 little-endian sequence
8..11   uint32 little-endian sampleRate, 当前固定 48000
12..13  uint16 little-endian channels, 当前固定 1
14..15  uint16 little-endian samplesPerChannel, 当前 960
16..23  uint64 little-endian capturedUnixMilliseconds
24..    int16 little-endian PCM samples
```

客户端仍兼容旧版 `WMF1` 包：

```text
0..3    ASCII "WMF1"
4..7    uint32 little-endian sequence
8..11   uint32 little-endian sampleRate, 当前固定 48000
12..13  uint16 little-endian channels, 当前固定 1
14..15  uint16 little-endian samplesPerChannel, 当前 960
16..    int16 little-endian PCM samples
```

Realtime WebSocket adapter 的每个二进制消息是 protobuf `Packet`，其中 `payload` 为 48 kHz stereo int16 little-endian PCM。客户端会自动识别并播放。

## 故障排查

### 网页显示只有 WebSocket PCM，没有 Realtime SFU

检查 Worker 是否配置了：

- `REALTIME_SFU_APP_ID`
- `REALTIME_SFU_BEARER_TOKEN`

然后重新部署：

```powershell
npm run deploy
```

### 本地 SFU 模式无法端到端工作

这是预期限制。Realtime WebSocket adapter 需要公网 `wss://` endpoint，本地 `http://localhost` 不能作为 adapter endpoint。部署到 Cloudflare 后再测 SFU 模式。

### Windows 客户端连不上

检查 URL 协议：

- 本地：`ws://localhost:8787/ws`
- 部署后：`wss://<worker>/ws`

如果配置了 `ROOM_TOKEN`，客户端必须加：

```powershell
--token <token>
```

### 客户端有连接但目标应用没有麦克风声音

检查三件事：

- 客户端 `--device` 选的是虚拟线缆的播放端点，例如 `CABLE Input`。
- 目标应用选的是虚拟线缆的录音端点，例如 `CABLE Output`。
- Windows 音量混音器里没有把客户端或虚拟线缆静音。

### 浏览器无法启动麦克风

检查：

- 浏览器是否允许当前 Worker 域名使用麦克风。
- 页面是否在 HTTPS 下打开。生产环境必须使用 HTTPS。
- 系统隐私设置是否允许浏览器访问麦克风。

### 手机熄屏后停止发送

移动浏览器在锁屏或页面不可见后可能会挂起网页、麦克风采集或 WebSocket。网页会在麦克风启动后请求 Screen Wake Lock，尽量防止自动熄屏；如果用户手动按电源键锁屏，纯网页通常不能可靠继续发送音频。

### 音量太低

先看网页的 `RMS` 和 `峰值`。如果网页端数值本身很低，先调高网页里的 `输入增益`；如果峰值接近或超过 `1.000`，说明已经削波，不要继续调高输入增益。

如果网页端电平正常但目标应用听起来仍然小，再调高 GUI 里的 `Output volume %`。命令行可以使用：

```powershell
dotnet run --project client/WebMicForward.Client -- --server wss://<worker>/ws --room my-room --volume-percent 200 --device "CABLE Input"
```

`--volume-percent` 默认是 `100`，范围是 `0-500`。超过 `100` 会放大 PCM，过高时可能出现削波失真。

### 音频延迟太高或卡顿

尝试：

```powershell
dotnet run --project client/WebMicForward.Client -- --server wss://<worker>/ws --room my-room --latency-ms 120 --device "CABLE Input"
```

`--latency-ms` 默认是 `80`，网络抖动明显时可以调大。

## 常用命令

```powershell
npm install
npm run typecheck
npm run dev
npm run deploy
dotnet build client/WebMicForward.Client/WebMicForward.Client.csproj
dotnet run --project client/WebMicForward.Client -- --list-devices
```
