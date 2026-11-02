# universal-cam — Project Roadmap

> Use your iPhone as a Windows webcam and microphone over USB or Wi-Fi.
> Status: **Phase 1 — Foundation** (active)

---

## Vision

Replace hardware capture cards and proprietary solutions with a free, open-architecture
app that streams H.264/HEVC video and AAC audio from an iPhone to Windows as a virtual
webcam and microphone — accessible to any app that uses DirectShow or Media Foundation
(Teams, OBS, Zoom, Slack, Discord, Webex).

Key differentiators over existing apps (Camo, iVCam, Iriun, DroidCam, FineCam):
- True plug-and-play: zero driver install via Windows Camera API + UVC emulation
- Native Windows 11 integration (taskbar widget, no background window required)
- On-device AI upscaling using AMD/Intel AI PC NPU chips
- Multi-iPhone director switching: seamless cut between 2–3 paired phones mid-call
- Spatial/binaural audio via iPhone dual-mic array
- Fully offline — zero telemetry, no account, no cloud dependency
- NDI HX3 output for professional broadcast workflows

---

## Tech Architecture

| Layer | Technology |
|---|---|
| UI framework | WinUI 3 / Windows App SDK 1.8 |
| Language | C# / .NET 8 |
| Transport | MsQuic (QUIC protocol over USB tunnel or Wi-Fi) |
| Video decode | Windows Media Foundation + DXVA2/D3D11VA (hardware) |
| Audio | WASAPI virtual audio device |
| Rendering | Direct3D 11 + WIC (Windows Imaging Component) |
| USB pairing | libimobiledevice / Apple Devices driver (Windows) |
| Distribution | Unpackaged EXE — no MSIX, target < 20 MB installer |

### Minimum System Requirements
- Windows 10 version 2004 (build 19041) or later
- .NET 8 Desktop Runtime
- Windows App Runtime 1.8
- Apple Devices (formerly iTunes) for USB pairing

### Target Platforms
- x64 only (Phase 1–3)
- ARM64 (Phase 4, if hardware partner interest)

---

## Phase 1 — Foundation
**Timeline:** Weeks 1–4
**Goal:** Buildable WinUI 3 shell + USB device enumeration. Enough to replace existing
apps for everyday meetings. Ship to closed beta.

### Milestones
- [ ] Dev environment fully configured (SDK, Build Tools, VS Code)
- [ ] Solution builds clean: `dotnet build universal-cam.sln --arch x64`
- [ ] WinUI 3 main window launches (unpackaged, no MSIX)
- [ ] USB device enumeration: detect connected iPhones via `Windows.Devices.Enumeration`
- [ ] libimobiledevice P/Invoke wrapper skeleton in `UniversalCam.Core`
- [ ] Wi-Fi auto-discovery via mDNS/Bonjour (LAN broadcast, no manual IP)
- [ ] Basic app manifest + app icon

### KPIs
- Build time < 30 s on clean
- Zero MSBuild warnings at `/warn:1`
- App launches without crash on Windows 10 19041 and Windows 11 26100
- Connect in under 30 seconds
- Video latency < 150 ms on Wi-Fi

---

## Phase 2 — Transport & Stream
**Timeline:** Weeks 5–10
**Goal:** Receive H.264 bitstream from iPhone over QUIC. First decoded frame on screen.

### Milestones
- [ ] MsQuic NuGet integrated; loopback QUIC connection test passes
- [ ] iPhone companion app (iOS) sends H.264 NAL units over QUIC
- [ ] Windows side receives and buffers NAL units (lock-free ring buffer)
- [ ] MF H.264 decoder (MFT) initialized with DXVA2 hardware acceleration
- [ ] First decoded frame rendered to WinUI 3 `SwapChainPanel` via D3D11
- [ ] Manual exposure, ISO, white balance, lens selector controls
- [ ] Configurable bitrate + encoder (H.264 / HEVC)
- [ ] Portrait mode (bokeh) and background blur
- [ ] Saved profiles / presets (JSON per profile, auto-load on connect)
- [ ] In-app recording to PC
- [ ] macOS client at parity with Windows (CoreMediaIO DAL plugin)

### KPIs
- End-to-end glass-to-glass latency < 100 ms (USB), < 150 ms (Wi-Fi)
- Zero frame drops at 1080p/30 on stable USB connection
- CPU < 5% for decode (hardware decode path confirmed)
- Background blur real-time at 1080p/30fps on iPhone 12+

---

## Phase 3 — Virtual Devices
**Timeline:** Weeks 11–16
**Goal:** App appears as a webcam and microphone in Windows. OBS/Teams/Zoom compatible.

### Milestones
- [ ] Virtual camera driver (`KSCATEGORY_VIDEO_CAMERA`) or MF virtual source registered
- [ ] OBS / Teams / Zoom / Slack / Discord detect "universal-cam" as a camera source
- [ ] WASAPI virtual audio endpoint routes iPhone microphone audio to Windows
- [ ] Audio/video sync maintained (PTS alignment)
- [ ] Preview window in WinUI 3 shows local mirror of streamed video
- [ ] Auto face tracking / Center Stage clone (CoreML PoseNet)
- [ ] AI noise reduction (temporal denoiser via MPS / Metal)
- [ ] AI quality upscaling 720p → 4K-like (Real-ESRGAN on iPhone NPU)
- [ ] Multi-iPhone switching — director mode (2–3 phones, one-click cut in PC client)
- [ ] LUT / cinematic colour filter library (pre-baked 3D LUT via Metal)
- [ ] Background video replacement
- [ ] Spatial / stereo mic capture (iPhone dual-mic array, binaural AAC)
- [ ] Custom overlay / lower-thirds (SVG compositor in PC client)
- [ ] Screen / window capture mix-in (Windows DXGI capture + phone feed composite)
- [ ] Native RTMP / live stream output (ffmpeg RTMP client)
- [ ] Global hotkey / shortcut system (mute, switch cam, snapshot)

### KPIs
- Device enumerated within 2 s of app launch
- Audio latency < 50 ms relative to video
- No BSOD during driver load/unload cycle (10 iterations)
- Multi-phone switch latency < 500 ms
- AI upscaling faster than real-time on AMD/Intel AI PC

---

## Phase 4 — Platform & Enterprise
**Timeline:** Weeks 17–22
**Goal:** Installable product. Enterprise buyers, broadcast pros, developer ecosystem.

### Milestones
- [ ] Inno Setup or WiX installer < 20 MB
- [ ] Auto-start option (Windows startup task, no service)
- [ ] System tray icon with connection status
- [ ] Settings UI: resolution, codec, USB vs. Wi-Fi toggle
- [ ] NDI HX3 output (NewTek NDI SDK, multicast UDP)
- [ ] DSLR / mirrorless via capture card (UVC/HDMI as virtual source)
- [ ] Developer SDK / API (C++ / Swift for third-party app ingestion)
- [ ] Group / team licence management (admin portal, seat-based, SSO SAML 2.0)
- [ ] Zero telemetry enterprise mode (no analytics, no cloud dependency)
- [ ] Hardware partner / OEM programme (pre-install agreements)
- [ ] Linux client (Ubuntu 22.04+, v4l2loopback virtual cam driver)
- [ ] Android companion app (CameraX API, parity with iPhone app)
- [ ] Document / whiteboard camera mode (portrait, auto-deskew)
- [ ] Apple Vision Pro support (visionOS companion app)
- [ ] Code-signed binary (EV cert or documented self-signed trust procedure)
- [ ] GitHub release with installer artifact

### KPIs
- Installer size < 20 MB (excluding .NET runtime)
- Install completes in < 60 s
- 0 user-visible errors in 30-minute continuous use session
- NDI compatible with vMix, OBS NDI, Wirecast
- Enterprise pilot with >= 3 companies
- Android parity >= 70% feature overlap

---

## Competitive Landscape Summary

| App | Pricing | Free 4K | AI/ML | Multi-device | Notes |
|---|---|---|---|---|---|
| Camo | $49.99/yr | No (720p free) | Partial | No | Market leader, 10M+ users |
| iVCam | $9.99 one-time | No | No | Yes | Best Windows focus |
| Iriun | Free | Yes | No | No | 100% free, Linux |
| DroidCam | $14.99 one-time | No (OBS only) | No | No | Android primary |
| FineCam | Freemium | Yes | Yes | Yes | Multi-source mixing |
| EpocCam | DISCONTINUED | — | — | — | Elgato, removed 2025 |

---

## Open Questions & Risks

| Risk | Severity | Mitigation |
|---|---|---|
| Virtual camera driver signing (kernel mode requires EV cert, ~$300/yr) | High | Use MF Software Device (user-mode) as default; kernel driver for advanced mode |
| Apple USB protocol changes (libimobiledevice is reverse-engineered) | Medium | Abstract behind interface; monitor libimobiledevice releases |
| MsQuic over USB NCM latency vs. raw USB | Low | Benchmark early; fallback to TCP if QUIC overhead is measurable |
| HEVC licensing (Windows HEVC codec is a paid Store item) | Medium | Ship H.264 as default; check codec availability before enabling HEVC |
| Multi-iPhone mode: iOS background session limits | Medium | Use `AVCaptureMultiCamSession` on iPhone 11+ (requires dual-camera entitlement) |

---

*Last updated: 2026-03-15 — Source: iphone_webcam_feature_roadmap.docx*
