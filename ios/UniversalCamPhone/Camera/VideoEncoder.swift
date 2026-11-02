import VideoToolbox
import CoreMedia

/// Encodes raw YUV CMSampleBuffers to H.264 Annex B NAL units using VideoToolbox hardware encoder.
final class VideoEncoder {

    // MARK: - Types

    struct EncodedFrame {
        let data:      Data      // H.264 Annex B NAL unit(s)
        let ptsUs:     Int64     // Presentation timestamp in microseconds
        let isKeyframe: Bool
    }

    // MARK: - Public

    /// Called on the encoder's private queue for each encoded frame.
    var onEncodedFrame: ((EncodedFrame) -> Void)?

    /// Called when the encoder encounters an error.
    var onError: ((Error) -> Void)?

    // MARK: - Private (all accessed exclusively on encoderQueue)

    private var session:      VTCompressionSession?
    private let encoderQueue  = DispatchQueue(label: "com.universalcam.videoencoder")
    private var frameCount    = 0

    private var lastWidth: Int32 = 1920
    private var lastHeight: Int32 = 1080
    private var lastFPS: Int32 = 30
    private var lastBitrate: Int = 8_000_000

    // MARK: - Lifecycle

    func prepare(width: Int32, height: Int32, fps: Int32 = 30, bitrate: Int = 8_000_000) {
        encoderQueue.async { [weak self] in
            self?.createSession(width: width, height: height, fps: fps, bitrate: bitrate)
        }
    }

    /// Encode a video sample buffer. Safe to call from any thread —
    /// the actual encoding is dispatched to the encoder queue.
    func encode(_ sampleBuffer: CMSampleBuffer) {
        // Retain the sample buffer for use on the encoder queue
        let retained = sampleBuffer
        encoderQueue.async { [weak self] in
            self?.encodeOnQueue(retained)
        }
    }

    func flush() {
        encoderQueue.async { [weak self] in
            guard let session = self?.session else { return }
            VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
        }
    }

    func invalidate() {
        encoderQueue.async { [weak self] in
            guard let self, let session = self.session else { return }
            VTCompressionSessionInvalidate(session)
            self.session = nil
        }
    }

    deinit {
        if let session {
            VTCompressionSessionInvalidate(session)
        }
    }

    // MARK: - Private (must run on encoderQueue)

    private func encodeOnQueue(_ sampleBuffer: CMSampleBuffer) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        // Adapt to the actual pixel buffer dimensions. The front camera may deliver
        // portrait-oriented buffers (e.g. 1080×1920) even when the session preset
        // is hd1920x1080, because videoRotationAngle is metadata-only on some configs.
        // Feeding mismatched buffers to a landscape VTCompressionSession squishes the image.
        let bufW = Int32(CVPixelBufferGetWidth(pixelBuffer))
        let bufH = Int32(CVPixelBufferGetHeight(pixelBuffer))
        if session == nil || bufW != lastWidth || bufH != lastHeight {
            createSession(width: bufW, height: bufH, fps: lastFPS, bitrate: lastBitrate)
        }

        guard let session else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let dur = CMSampleBufferGetDuration(sampleBuffer)

        var flags: VTEncodeInfoFlags = []
        // Bound reference-chain recovery to roughly one second at the configured frame rate.
        let forceKeyframe = frameCount % max(1, Int(lastFPS)) == 0
        let frameProperties: CFDictionary? = forceKeyframe
            ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary
            : nil

        let status = VTCompressionSessionEncodeFrame(
            session, imageBuffer: pixelBuffer, presentationTimeStamp: pts,
            duration: dur, frameProperties: frameProperties,
            sourceFrameRefcon: nil, infoFlagsOut: &flags
        )

        if status != noErr {
            let error = NSError(domain: NSOSStatusErrorDomain, code: Int(status))
            onError?(error)
            recreateSession()
        }

        frameCount += 1
    }

    private func recreateSession() {
        if let session {
            VTCompressionSessionInvalidate(session)
            self.session = nil
        }
        createSession(width: lastWidth, height: lastHeight, fps: lastFPS, bitrate: lastBitrate)
    }

    private func createSession(width: Int32, height: Int32, fps: Int32, bitrate: Int) {
        lastWidth = width
        lastHeight = height
        lastFPS = fps
        lastBitrate = bitrate
        frameCount = 0  // always start new session with a forced keyframe

        var s: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: nil,
            width: width,
            height: height,
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: nil,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: outputCallback,
            refcon: Unmanaged.passUnretained(self).toOpaque(),
            compressionSessionOut: &s
        )
        guard status == noErr, let s else {
            print("[VideoEncoder] Failed to create VTCompressionSession: \(status)")
            onError?(NSError(domain: NSOSStatusErrorDomain, code: Int(status)))
            return
        }
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_RealTime,          value: kCFBooleanTrue)
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        // Main profile supports CABAC for better compression (Baseline does not)
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_ProfileLevel,      value: kVTProfileLevel_H264_Main_AutoLevel)
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_AverageBitRate,    value: bitrate as CFNumber)
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: fps as CFNumber)
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_H264EntropyMode,   value: kVTH264EntropyMode_CABAC)
        // Set data rate limits: [bytes per second, period in seconds]
        let dataRateLimit = [Double(bitrate) / 8.0 * 1.5, 1.0] as CFArray
        VTSessionSetProperty(s, key: kVTCompressionPropertyKey_DataRateLimits, value: dataRateLimit)
        VTCompressionSessionPrepareToEncodeFrames(s)
        self.session = s
    }
}

// MARK: - VTCompressionOutputCallback (C function)

private func outputCallback(
    outputCallbackRefCon: UnsafeMutableRawPointer?,
    sourceFrameRefCon: UnsafeMutableRawPointer?,
    status: OSStatus,
    infoFlags: VTEncodeInfoFlags,
    sampleBuffer: CMSampleBuffer?
) {
    guard
        status == noErr,
        let sampleBuffer,
        CMSampleBufferDataIsReady(sampleBuffer),
        let refCon = outputCallbackRefCon
    else { return }

    let encoder = Unmanaged<VideoEncoder>.fromOpaque(refCon).takeUnretainedValue()

    // Detect keyframe by checking sample attachments
    let isKeyframe: Bool
    if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[CFString: Any]],
       let first = attachments.first {
        // If kCMSampleAttachmentKey_NotSync is absent or false, it's a keyframe
        isKeyframe = !(first[kCMSampleAttachmentKey_NotSync] as? Bool ?? false)
    } else {
        isKeyframe = true
    }

    guard let dataBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }
    var totalLength = 0
    var dataPointer: UnsafeMutablePointer<Int8>?
    CMBlockBufferGetDataPointer(dataBuffer, atOffset: 0, lengthAtOffsetOut: nil,
                                totalLengthOut: &totalLength, dataPointerOut: &dataPointer)
    guard let ptr = dataPointer else { return }

    // Convert AVCC (length-prefixed) to Annex B (start-code prefixed)
    var annexB = Data()

    // Prepend SPS/PPS for keyframes
    if isKeyframe, let formatDesc = CMSampleBufferGetFormatDescription(sampleBuffer) {
        annexB.append(annexBParameterSets(from: formatDesc))
    }

    var offset = 0
    while offset < totalLength {
        // Read 4-byte AVCC length prefix (big-endian)
        var nalLength: UInt32 = 0
        memcpy(&nalLength, ptr + offset, 4)
        nalLength = CFSwapInt32BigToHost(nalLength)
        offset += 4
        // Write Annex B start code
        annexB.append(contentsOf: [0x00, 0x00, 0x00, 0x01])
        annexB.append(Data(bytes: ptr + offset, count: Int(nalLength)))
        offset += Int(nalLength)
    }

    let ptsUs = Int64(CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sampleBuffer)) * 1_000_000)
    let frame = VideoEncoder.EncodedFrame(data: annexB, ptsUs: ptsUs, isKeyframe: isKeyframe)
    encoder.onEncodedFrame?(frame)
}

/// Extract SPS and PPS from the format description and return as Annex B data.
private func annexBParameterSets(from formatDescription: CMFormatDescription) -> Data {
    var data = Data()
    var paramSetCount = 0
    CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
        formatDescription, parameterSetIndex: 0,
        parameterSetPointerOut: nil, parameterSetSizeOut: nil,
        parameterSetCountOut: &paramSetCount, nalUnitHeaderLengthOut: nil
    )
    for i in 0..<paramSetCount {
        var paramSetPtr: UnsafePointer<UInt8>?
        var paramSetSize = 0
        let status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
            formatDescription, parameterSetIndex: i,
            parameterSetPointerOut: &paramSetPtr, parameterSetSizeOut: &paramSetSize,
            parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil
        )
        if status == noErr, let ptr = paramSetPtr {
            data.append(contentsOf: [0x00, 0x00, 0x00, 0x01])
            data.append(ptr, count: paramSetSize)
        }
    }
    return data
}
