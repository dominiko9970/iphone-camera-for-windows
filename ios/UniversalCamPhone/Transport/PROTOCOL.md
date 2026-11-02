# UniversalCam Network Protocol Specification

Version: 1.0  
Transport: QUIC over UDP  
Port: 7779  
Service: _universalcam._tcp

---

## Connection Flow

```
iPhone                                  PC/Windows
  |                                      |
  |------ [Bonjour Discovery] --------->|
  |<----- [Service Advertisement] ------|
  |                                      |
  |------ [QUIC Handshake + TLS] ------>|
  |<----- [Connection Established] -----|
  |                                      |
  |<----- {"type":"welcome"} ------------|
  |------ {"type":"hello",...} --------->|
  |                                      |
  |<----- {"type":"configure",...} ------|
  |------ {"type":"configure_ack"} ----->|
  |                                      |
  |------ {"type":"start_stream"} ------>|
  |                                      |
  |====== [Video/Audio Frames] ========>|
  |====== [Binary Stream] ==============>|
  |                                      |
  |<----- {"type":"ping",ts:...} --------|
  |------ {"type":"pong",ts:...} ------->|
  |                                      |
  |------ {"type":"stop_stream"} ------->|
  |                                      |
  |------ [Disconnect] ------------------>|
```

---

## 1. Service Discovery (Bonjour/mDNS)

### Service Type
```
_universalcam._tcp.local.
```

### Published by iPhone
```
Name: [Device Name] (e.g., "John's iPhone")
Port: 7779
Domain: local.
```

### Published by PC
```
Name: [Computer Name] (e.g., "DESKTOP-ABC123")
Port: 7779
Domain: local.
```

**Resolution**: Both parties discover each other's IPv4 addresses

---

## 2. QUIC Connection

### Parameters
- **Protocol**: QUIC (RFC 9000)
- **ALPN**: `universalcam/1`
- **Port**: 7779
- **TLS**: Required (self-signed in dev, validated in prod)
- **Direction**: Bidirectional

### Connection Initiation
iPhone initiates connection to discovered PC IP address.

---

## 3. Stream Type Multiplexing

Every message on the wire is prefixed by a **1-byte stream type** that tells the
receiver how to interpret the rest of the data:

| Value | Stream | Direction | Framing |
|-------|--------|-----------|---------|
| `0x00` | Control | Bidirectional | JSON + newline (0x0A) |
| `0x01` | Video | Phone → PC | Binary frame header + payload |
| `0x02` | Audio | Phone → PC | Binary frame header + payload |

The stream type byte is **always** the first byte of every send. Receivers must
read this byte first to decide whether to parse JSON or a binary frame header.

---

## 4. Control Messages (JSON)

All control messages are prefixed by `0x00` (stream type), followed by a
**JSON object**, terminated by newline (`\n` = 0x0A).

### Wire Format
```
┌──────────────────────────────────────────┐
│ Stream Type (1 byte = 0x00)             │ Byte 0
├──────────────────────────────────────────┤
│ JSON payload (N bytes, UTF-8)           │ Bytes 1 .. N
├──────────────────────────────────────────┤
│ Newline delimiter (1 byte = 0x0A)       │ Byte N+1
└──────────────────────────────────────────┘
```

### Message Format
```json
{
  "type": "message_type",
  ...additional fields
}
```

### 4.1 Welcome (PC → iPhone)

Sent immediately after QUIC connection established.

```json
{
  "type": "welcome"
}
```

**Response**: iPhone sends `hello`

---

### 4.2 Hello (iPhone → PC)

iPhone introduces itself with capabilities.

```json
{
  "type": "hello",
  "deviceName": "John's iPhone",
  "capabilities": ["h264", "aac_lc", "stereo_audio"]
}
```

**Fields**:
- `deviceName` (string): iOS device name
- `capabilities` (array): Supported features
  - `"h264"` - H.264 video encoding
  - `"hevc"` - HEVC/H.265 (future)
  - `"aac_lc"` - AAC-LC audio
  - `"stereo_audio"` - 2-channel audio
  - `"mono_audio"` - 1-channel audio

---

### 4.3 Configure (PC → iPhone)

PC requests specific encoding settings.

```json
{
  "type": "configure",
  "resolution": "1920x1080",
  "fps": 30,
  "codec": "h264",
  "bitrate": 8000000
}
```

**Fields**:
- `resolution` (string): Width x height (e.g., "1280x720", "1920x1080", "3840x2160")
- `fps` (integer): Frames per second (15, 30, 60)
- `codec` (string): "h264" or "hevc"
- `bitrate` (integer): Bits per second (e.g., 8000000 = 8 Mbps)

**Response**: iPhone sends `configure_ack`

---

### 4.4 Configure Ack (iPhone → PC)

iPhone confirms configuration applied.

```json
{
  "type": "configure_ack",
  "resolution": "1920x1080",
  "fps": 30
}
```

**Fields**:
- `resolution` (string): Actual resolution applied
- `fps` (integer): Actual FPS applied

---

### 4.5 Start Stream (iPhone → PC)

iPhone signals it's starting to send media frames.

```json
{
  "type": "start_stream"
}
```

**Action**: PC should prepare to receive binary video/audio frames

---

### 4.6 Stop Stream (Either → Other)

Request to stop media transmission.

```json
{
  "type": "stop_stream"
}
```

**Action**: Stop sending/expecting binary frames

---

### 4.7 Ping (Either → Other)

Measure round-trip time.

```json
{
  "type": "ping",
  "ts": 1678901234567
}
```

**Fields**:
- `ts` (int64): Unix timestamp in milliseconds

**Response**: Recipient sends `pong` with same timestamp

---

### 4.8 Pong (Response to Ping)

```json
{
  "type": "pong",
  "ts": 1678901234567
}
```

**Fields**:
- `ts` (int64): Original timestamp from ping

**Latency Calculation**: `(current_time - ping_sent_time) / 2`

---

## 5. Binary Media Frames

After `start_stream`, iPhone sends continuous binary frames for video and audio.
Each frame is prefixed by its stream type byte (`0x01` for video, `0x02` for audio).

### Frame Structure (14-byte header + payload)

```
┌────────────────────────────────────────┐
│ Stream Type (1 byte)                  │ Byte 0      (0x01=video, 0x02=audio)
├────────────────────────────────────────┤
│ Payload Length (4 bytes, LE uint32)   │ Bytes 1-4
├────────────────────────────────────────┤
│ PTS in microseconds (8 bytes, LE i64) │ Bytes 5-12
├────────────────────────────────────────┤
│ Flags (1 byte)                         │ Byte 13
├────────────────────────────────────────┤
│ Payload (N bytes)                      │ Bytes 14+
└────────────────────────────────────────┘
```

**All integers are Little-Endian**

---

### 5.1 Video Frames

#### Flags (byte 13)
```
Bit 0: Keyframe (1 = keyframe, 0 = delta frame)
Bit 1: Codec (0 = H.264, 1 = HEVC) [future]
Bits 2-7: Reserved (set to 0)
```

#### Payload Format: H.264 Annex B

```
[0x00 0x00 0x00 0x01] [NAL Unit]
[0x00 0x00 0x00 0x01] [NAL Unit]
...
```

**NAL Unit Types**:
- `0x67` (SPS) - Sequence Parameter Set
- `0x68` (PPS) - Picture Parameter Set  
- `0x65` (IDR) - Keyframe slice
- `0x61` (Coded Slice) - P-frame slice

#### Example Video Frame (Keyframe)

```
Hex dump:
01  ← Stream type: video
00 00 00 A4  ← Length = 164 bytes
00 00 00 00 00 00 00 00  ← PTS = 0 μs
01  ← Flags: keyframe=1, hevc=0

00 00 00 01 67 42 00 1F ...  ← H.264 Annex B NALs
```

---

### 5.2 Audio Frames

#### Flags (byte 13)
```
Value: Number of channels
  1 = Mono
  2 = Stereo
```

#### Payload Format: AAC-LC ADTS

Raw AAC frames (may include ADTS headers or just raw AAC)

**Format**: 44.1 kHz, AAC-LC, configured channel count

#### Example Audio Frame

```
Hex dump:
02  ← Stream type: audio
00 04 00 00  ← Length = 1024 bytes
15 CD 5B 00 00 00 00 00  ← PTS = 6000000 μs (6 sec)
02  ← Flags: 2 channels (stereo)

FF F1 50 80 ...  ← AAC data
```

---

## 6. Timing & Synchronization

### Presentation Timestamps (PTS)

- **Unit**: Microseconds (μs)
- **Origin**: Host time clock (shared between audio and video for A/V sync)
- **Encoding**: 64-bit signed integer, little-endian
- **Video**: Based on CMSampleBuffer presentation time (CMClock host time)
- **Audio**: Based on AVAudioTime host time (same clock domain as video)

### Frame Timing

**Video @ 30fps**:
- Frame 0: PTS = 0
- Frame 1: PTS = 33,333 μs
- Frame 2: PTS = 66,667 μs
- Frame 3: PTS = 100,000 μs

**Audio @ 44.1kHz, 1024 samples/frame**:
- Frame 0: PTS = 0
- Frame 1: PTS = 23,220 μs (1024/44100 * 1,000,000)
- Frame 2: PTS = 46,440 μs

---

## 7. Error Handling

### Connection Failures

**iPhone behavior**:
- Exponential backoff retry (2^n seconds, max 30s)
- Maximum 10 retry attempts
- UI shows error state

**PC behavior**:
- Accept new connections
- Handle sudden disconnects gracefully

### Stream Interruptions

**Scenarios**:
1. Network loss → Auto-reconnect
2. Background app → Continue with background audio
3. Low memory → Stop encoding, send `stop_stream`

---

## 8. Performance Characteristics

### Typical Metrics

| Metric | Value | Notes |
|--------|-------|-------|
| Latency | 50-150ms | Local WiFi |
| Bandwidth | ~8 Mbps | At 1080p30 |
| Keyframe interval | 2 seconds | Every 60 frames |
| Audio bitrate | 128 kbps | AAC-LC stereo |
| CPU (iPhone) | 15-25% | Modern devices |

### Bandwidth Calculation

**Video**: ~8 Mbps (configurable)  
**Audio**: 128 kbps  
**Control**: < 1 kbps  
**Total**: ~8.13 Mbps = ~1 MB/s

---

## 9. Security

### TLS Configuration

**Debug Builds**:
```swift
sec_protocol_options_set_verify_block { _, _, completion in
    completion(true)  // Accept self-signed certs
}
```

**Production Builds**:
```swift
sec_protocol_options_set_verify_block { _, sec_trust, completion in
    let trust = sec_trust_copy_ref(sec_trust).takeRetainedValue()
    let isValid = SecTrustEvaluateWithError(trust, &error)
    completion(isValid)  // Validate via system trust store
}
```

### Encryption

- **All data encrypted** via QUIC/TLS 1.3
- **Certificate pinning**: Recommended for production
- **Local network only**: No internet exposure by default

---

## 10. Implementation Notes

### iPhone (Sender)

1. Discover PC via Bonjour
2. Initiate QUIC connection
3. Wait for `welcome`
4. Send `hello` with capabilities
5. Apply `configure` if received
6. Send `start_stream` when user taps record
7. Stream video/audio frames continuously
8. Respond to `ping` with `pong`
9. Send `stop_stream` when user stops

### PC (Receiver)

1. Advertise via Bonjour
2. Listen on port 7779 (QUIC)
3. Accept connections
4. Send `welcome`
5. Parse `hello` for capabilities
6. Optionally send `configure`
7. Receive `start_stream` signal
8. Decode incoming binary frames
9. Send periodic `ping` for latency
10. Display video, play audio

---

## 11. Example Packet Captures

### Control Message Exchange

Each control message is prefixed by `0x00` (stream type) and terminated by `0x0A` (newline):

```
→ 0x00 {"type":"welcome"} 0x0A
← 0x00 {"type":"hello","deviceName":"iPhone 15","capabilities":["h264","aac_lc","stereo_audio"]} 0x0A
→ 0x00 {"type":"configure","resolution":"1920x1080","fps":30,"codec":"h264","bitrate":8000000} 0x0A
← 0x00 {"type":"configure_ack","resolution":"1920x1080","fps":30} 0x0A
← 0x00 {"type":"start_stream"} 0x0A
```

### First Video Frame (SPS+PPS+IDR)

```
Bytes: 4811 (14-byte header + 4797-byte payload)
  [0]:     01                       ← Stream type: video
  [1-4]:   BD 12 00 00              ← Length: 4797
  [5-12]:  00 00 00 00 00 00 00 00  ← PTS: 0
  [13]:    01                       ← Keyframe
  [14+]:   00 00 00 01 67 42 ...    ← H.264 Annex B
```

### Subsequent P-Frame

```
Bytes: 1251 (14-byte header + 1237-byte payload)
  [0]:     01                       ← Stream type: video
  [1-4]:   D5 04 00 00              ← Length: 1237
  [5-12]:  15 82 00 00 00 00 00 00  ← PTS: 33333
  [13]:    00                       ← Not keyframe
  [14+]:   00 00 00 01 61 ...       ← P-frame NAL
```

---

## 12. Testing Tools

### Wireshark Filter

```
udp.port == 7779
```

### Decode QUIC

Enable QUIC protocol dissector in Wireshark preferences.

### Mock Data

Use `MockWindowsServer.swift` to simulate PC receiver.

---

## Version History

- **v1.0** (2026-03): Initial protocol specification

---

**Protocol designed for low-latency local streaming**  
**Optimized for iPhone → Windows PC use case**
