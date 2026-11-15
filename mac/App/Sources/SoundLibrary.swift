import AVFoundation
import Foundation

/// Decoding sound files and importing them (files, YouTube via yt-dlp).
enum SoundLibrary {
    private static var cache: [String: [Float]] = [:]
    private static let lock = NSLock()

    /// Mono 48 kHz floats, loudness-matched to about a speaking voice. Short clips are cached.
    static func decode(_ url: URL) -> [Float]? {
        let key = url.path + "|" + String((try? url.resourceValues(forKeys: [.contentModificationDateKey]))?
            .contentModificationDate?.timeIntervalSince1970 ?? 0)
        lock.lock(); if let hit = cache[key] { lock.unlock(); return hit }; lock.unlock()

        guard let file = try? AVAudioFile(forReading: url) else { return nil }
        let inFmt = file.processingFormat
        guard let outFmt = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48000, channels: 1, interleaved: false),
              let conv = AVAudioConverter(from: inFmt, to: outFmt) else { return nil }
        let maxFrames = AVAudioFrameCount(48000 * 180)                       // 3 minutes max
        var result: [Float] = []
        result.reserveCapacity(Int(min(Double(file.length) * 48000 / inFmt.sampleRate, Double(maxFrames))))
        let inBuf = AVAudioPCMBuffer(pcmFormat: inFmt, frameCapacity: 8192)!
        var eof = false
        while !eof && result.count < Int(maxFrames) {
            guard let out = AVAudioPCMBuffer(pcmFormat: outFmt, frameCapacity: 16384) else { break }
            var err: NSError?
            let st = conv.convert(to: out, error: &err) { _, status in
                do {
                    try file.read(into: inBuf)
                    if inBuf.frameLength == 0 { status.pointee = .endOfStream; return nil }
                    status.pointee = .haveData
                    return inBuf
                } catch {
                    status.pointee = .endOfStream; return nil
                }
            }
            if let ch = out.floatChannelData?[0], out.frameLength > 0 {
                result.append(contentsOf: UnsafeBufferPointer(start: ch, count: Int(out.frameLength)))
            }
            if st == .endOfStream || st == .error || out.frameLength == 0 { eof = true }
        }
        normalize(&result)
        if result.count <= 48000 * 30 {
            lock.lock(); cache[key] = result; lock.unlock()
        }
        return result
    }

    // Same rule as Windows: 80th-percentile 50 ms RMS to -20 dBFS, peaks kept under 0.95.
    static func normalize(_ a: inout [Float]) {
        guard !a.isEmpty else { return }
        let block = 2400
        var rms: [Double] = []
        var peak: Float = 0
        var i = 0
        while i < a.count {
            let n = min(block, a.count - i)
            var e = 0.0
            for j in 0..<n { let v = a[i + j]; e += Double(v * v); peak = max(peak, abs(v)) }
            rms.append((e / Double(n)).squareRoot())
            i += block
        }
        guard peak > 1e-4 else { return }
        rms.sort()
        let loud = rms[Int(Double(rms.count) * 0.8)]
        guard loud > 1e-5 else { return }
        let g = Float(min(max(min(0.1 / loud, 0.95 / Double(peak)), 0.05), 20))
        for k in a.indices { a[k] *= g }
    }

    /// Copy a file into the Sounds folder; returns the new SoundItem.
    static func importFile(_ src: URL) throws -> SoundItem {
        Paths.ensure()
        let name = src.deletingPathExtension().lastPathComponent
        var dest = Paths.sounds.appendingPathComponent(src.lastPathComponent)
        var n = 2
        while FileManager.default.fileExists(atPath: dest.path) {
            dest = Paths.sounds.appendingPathComponent("\(name) \(n).\(src.pathExtension)"); n += 1
        }
        try FileManager.default.copyItem(at: src, to: dest)
        return SoundItem(name: name, file: dest.lastPathComponent)
    }

    // MARK: YouTube (yt-dlp, downloaded on first use; the m4a audio track needs no ffmpeg)
    static var ytDlp: URL { Paths.bin.appendingPathComponent("yt-dlp_macos") }

    static func ensureYtDlp(progress: @escaping (String) -> Void) async throws {
        if FileManager.default.isExecutableFile(atPath: ytDlp.path) { return }
        Paths.ensure()
        progress("Downloading yt-dlp…")
        let (tmp, resp) = try await URLSession.shared.download(from:
            URL(string: "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_macos")!)
        guard (resp as? HTTPURLResponse)?.statusCode == 200 else { throw ImportError("Couldn't download yt-dlp.") }
        try? FileManager.default.removeItem(at: ytDlp)
        try FileManager.default.moveItem(at: tmp, to: ytDlp)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: ytDlp.path)
    }

    static func importYouTube(_ link: String, progress: @escaping (String) -> Void) async throws -> SoundItem {
        try await ensureYtDlp(progress: progress)
        progress("Downloading audio…")
        let out = try await run(ytDlp, [
            "-f", "bestaudio[ext=m4a]/bestaudio[ext=mp3]/bestaudio",
            "--no-playlist", "--no-progress", "--restrict-filenames",
            "-o", Paths.sounds.path + "/%(title).80s.%(ext)s",
            "--print", "after_move:filepath",
            link,
        ])
        guard let path = out.split(separator: "\n").last.map(String.init), FileManager.default.fileExists(atPath: path) else {
            throw ImportError("yt-dlp didn't produce a file.")
        }
        let url = URL(fileURLWithPath: path)
        guard decode(url) != nil else {
            try? FileManager.default.removeItem(at: url)
            throw ImportError("That video's audio format isn't supported.")
        }
        let title = url.deletingPathExtension().lastPathComponent.replacingOccurrences(of: "_", with: " ")
        return SoundItem(name: title, file: url.lastPathComponent)
    }

    struct ImportError: LocalizedError { let message: String; init(_ m: String) { message = m }; var errorDescription: String? { message } }

    private static func run(_ exe: URL, _ args: [String]) async throws -> String {
        try await withCheckedThrowingContinuation { cont in
            let p = Process()
            p.executableURL = exe
            p.arguments = args
            let outPipe = Pipe(), errPipe = Pipe()
            p.standardOutput = outPipe; p.standardError = errPipe
            p.terminationHandler = { proc in
                let out = String(data: outPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
                let err = String(data: errPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
                if proc.terminationStatus == 0 { cont.resume(returning: out.trimmingCharacters(in: .whitespacesAndNewlines)) }
                else { cont.resume(throwing: ImportError(err.split(separator: "\n").last.map(String.init) ?? "yt-dlp failed.")) }
            }
            do { try p.run() } catch { cont.resume(throwing: error) }
        }
    }
}
