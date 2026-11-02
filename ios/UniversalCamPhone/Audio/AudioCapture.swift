import AVFoundation
import AudioToolbox
import CoreMedia

/// Captures microphone audio via AVAudioEngine and encodes to AAC-LC frames.
final class AudioCapture {

    // MARK: - Types

    struct AudioFrame {
        let data:     Data    // AAC-LC frame data
        let ptsUs:    Int64   // Presentation timestamp in microseconds
        let channels: Int     // 1 = mono, 2 = stereo
    }

    // MARK: - Public

    var onEncodedAudio: ((AudioFrame) -> Void)?

    // MARK: - Private

    private let engine = AVAudioEngine()
    private var aacConverter: AVAudioConverter?
    private let sampleRate: Double = 44100

    // MARK: - Lifecycle

    func start() {
        let input = engine.inputNode
        let inputFormat = input.outputFormat(forBus: 0)

        // Create AAC output format
        guard let aacFormat = AVAudioFormat(
            settings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: sampleRate,
                AVNumberOfChannelsKey: 2,
                AVEncoderBitRateKey: 128_000,
                AVEncoderBitRateStrategyKey: AVAudioBitRateStrategy_Constant
            ]
        ) else {
            print("[AudioCapture] Failed to create AAC format")
            return
        }

        // Intermediate PCM format at target sample rate for conversion
        guard let pcmFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: sampleRate,
            channels: 2,
            interleaved: false
        ) else {
            print("[AudioCapture] Failed to create PCM format")
            return
        }

        aacConverter = AVAudioConverter(from: pcmFormat, to: aacFormat)

        // Install tap on input node
        input.installTap(onBus: 0, bufferSize: 1024, format: inputFormat) { [weak self] buffer, time in
            self?.process(buffer: buffer, time: time, targetPCMFormat: pcmFormat)
        }

        do {
            try engine.start()
        } catch {
            print("[AudioCapture] Engine start failed: \(error)")
        }
    }

    func stop() {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        aacConverter = nil
    }

    deinit {
        stop()
    }

    // MARK: - Processing

    private func process(buffer: AVAudioPCMBuffer, time: AVAudioTime, targetPCMFormat: AVAudioFormat) {
        guard let converter = aacConverter else { return }

        // Convert input buffer to the target PCM format if needed
        let pcmBuffer: AVAudioPCMBuffer
        if buffer.format == targetPCMFormat {
            pcmBuffer = buffer
        } else {
            guard let formatConverter = AVAudioConverter(from: buffer.format, to: targetPCMFormat) else { return }
            guard let converted = AVAudioPCMBuffer(pcmFormat: targetPCMFormat, frameCapacity: buffer.frameLength) else { return }
            var consumed = false
            var error: NSError?
            formatConverter.convert(to: converted, error: &error) { _, outStatus in
                if consumed {
                    outStatus.pointee = .noDataNow
                    return nil
                }
                consumed = true
                outStatus.pointee = .haveData
                return buffer
            }
            if error != nil { return }
            pcmBuffer = converted
        }

        // Encode PCM to AAC
        guard let aacBuffer = AVAudioCompressedBuffer(
            format: converter.outputFormat,
            packetCapacity: 1,
            maximumPacketSize: 768
        ) as AVAudioCompressedBuffer? else { return }

        var consumed = false
        var error: NSError?
        converter.convert(to: aacBuffer, error: &error) { _, outStatus in
            if consumed {
                outStatus.pointee = .noDataNow
                return nil
            }
            consumed = true
            outStatus.pointee = .haveData
            return pcmBuffer
        }

        if let error {
            print("[AudioCapture] AAC encoding error: \(error)")
            return
        }

        guard aacBuffer.byteLength > 0 else { return }

        let data = Data(bytes: aacBuffer.data, count: Int(aacBuffer.byteLength))

        // Derive PTS from AVAudioTime's hostTime to align with video CMTime clock.
        // AVAudioTime.hostTime is in mach_absolute_time units; convert to microseconds
        // using the same clock base that CMTime uses.
        let ptsUs: Int64
        if time.isHostTimeValid {
            let hostTimeNs = AVAudioTime.seconds(forHostTime: time.hostTime)
            ptsUs = Int64(hostTimeNs * 1_000_000)
        } else {
            // Fallback: use current time
            let cmNow = CMClockGetTime(CMClockGetHostTimeClock())
            ptsUs = Int64(CMTimeGetSeconds(cmNow) * 1_000_000)
        }

        let channelCount = Int(converter.outputFormat.channelCount)
        let frame = AudioFrame(data: data, ptsUs: ptsUs, channels: channelCount)
        onEncodedAudio?(frame)
    }
}
