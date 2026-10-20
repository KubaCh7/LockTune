import AppKit
import CoreImage
import SwiftUI

/// Dominant colours and brightness extracted from album artwork.
struct ArtworkPalette: Equatable {
    struct RGB: Equatable {
        var r: Double, g: Double, b: Double

        var color: Color { Color(.sRGB, red: r, green: g, blue: b, opacity: 1) }
        var luminance: Double { 0.2126 * r + 0.7152 * g + 0.0722 * b }
        var saturation: Double {
            let mx = max(r, g, b), mn = min(r, g, b)
            return mx == 0 ? 0 : (mx - mn) / mx
        }

        func mixed(with other: RGB, _ amount: Double) -> RGB {
            RGB(r: r + (other.r - r) * amount,
                g: g + (other.g - g) * amount,
                b: b + (other.b - b) * amount)
        }

        func distance(to o: RGB) -> Double {
            let dr = r - o.r, dg = g - o.g, db = b - o.b
            return (dr * dr + dg * dg + db * db).squareRoot()
        }

        static let white = RGB(r: 1, g: 1, b: 1)
        static let black = RGB(r: 0, g: 0, b: 0)
    }

    /// Up to three dominant colours, most prominent first.
    var colors: [RGB]
    /// Average luminance of the artwork, 0…1.
    var averageLuminance: Double

    /// Background is bright enough that text should be dark.
    var isLight: Bool { averageLuminance > 0.62 }

    var primary: RGB { colors.first ?? RGB(r: 0.15, g: 0.18, b: 0.35) }
    var secondary: RGB { colors.count > 1 ? colors[1] : primary.mixed(with: .black, 0.35) }
    var tertiary: RGB { colors.count > 2 ? colors[2] : secondary.mixed(with: .white, 0.2) }

    /// Main text colour: white/black, lightly tinted with the artwork (like the
    /// soft blue-white clock in the reference video).
    var textColor: Color {
        isLight ? primary.mixed(with: .black, 0.82).color
                : primary.mixed(with: .white, 0.86).color
    }

    static let fallback = ArtworkPalette(
        colors: [RGB(r: 0.16, g: 0.20, b: 0.42), RGB(r: 0.36, g: 0.18, b: 0.42), RGB(r: 0.08, g: 0.10, b: 0.22)],
        averageLuminance: 0.18)
}

/// Result of analysing one artwork image.
struct ArtworkAnalysis {
    var palette: ArtworkPalette
    /// Small, heavily blurred, saturated copy used for the animated background.
    var blurred: NSImage?
}

enum ArtworkAnalyzer {
    private static let ciContext = CIContext(options: [.cacheIntermediates: false])

    /// Pure function, safe to call off the main thread.
    static func analyze(_ cgImage: CGImage) -> ArtworkAnalysis {
        let blurred = makeBlurred(cgImage)
        // Measure brightness on the blurred image: that's what text sits on.
        let palette = extractPalette(cgImage, luminanceSource: blurred.flatMap {
            $0.cgImage(forProposedRect: nil, context: nil, hints: nil)
        })
        return ArtworkAnalysis(palette: palette, blurred: blurred)
    }

    static func extractPalette(_ image: CGImage, luminanceSource: CGImage? = nil) -> ArtworkPalette {
        guard let pixels = samplePixels(image, side: 32), !pixels.isEmpty else { return .fallback }

        // Bucket colours into a 8×8×8 cube.
        var buckets: [Int: (sum: ArtworkPalette.RGB, count: Int)] = [:]
        for p in pixels {
            let key = (Int(p.r * 7.999) << 6) | (Int(p.g * 7.999) << 3) | Int(p.b * 7.999)
            var entry = buckets[key] ?? (sum: ArtworkPalette.RGB(r: 0, g: 0, b: 0), count: 0)
            entry.sum.r += p.r; entry.sum.g += p.g; entry.sum.b += p.b
            entry.count += 1
            buckets[key] = entry
        }

        let candidates: [(color: ArtworkPalette.RGB, score: Double)] = buckets.values.map { entry in
            let n = Double(entry.count)
            let c = ArtworkPalette.RGB(r: entry.sum.r / n, g: entry.sum.g / n, b: entry.sum.b / n)
            var score = n * (0.35 + c.saturation * 1.6)
            if c.luminance < 0.05 || c.luminance > 0.95 { score *= 0.3 }
            return (c, score)
        }.sorted { $0.score > $1.score }

        var chosen: [ArtworkPalette.RGB] = []
        for candidate in candidates where chosen.count < 3 {
            if chosen.allSatisfy({ $0.distance(to: candidate.color) > 0.22 }) {
                chosen.append(candidate.color)
            }
        }

        let lumPixels = luminanceSource.flatMap { samplePixels($0, side: 16) } ?? pixels
        let avg = lumPixels.reduce(0.0) { $0 + $1.luminance } / Double(max(lumPixels.count, 1))
        return ArtworkPalette(colors: chosen.isEmpty ? ArtworkPalette.fallback.colors : chosen,
                              averageLuminance: avg)
    }

    private static func samplePixels(_ image: CGImage, side: Int) -> [ArtworkPalette.RGB]? {
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let ctx = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8,
                                  bytesPerRow: side * 4, space: space,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return nil }
        ctx.interpolationQuality = .medium
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: side, height: side))
        guard let raw = ctx.data else { return nil }
        let bytes = raw.bindMemory(to: UInt8.self, capacity: side * side * 4)
        var result: [ArtworkPalette.RGB] = []
        result.reserveCapacity(side * side)
        for i in 0..<(side * side) {
            let a = Double(bytes[i * 4 + 3]) / 255
            guard a > 0.5 else { continue }
            result.append(ArtworkPalette.RGB(r: Double(bytes[i * 4]) / 255 / a,
                                             g: Double(bytes[i * 4 + 1]) / 255 / a,
                                             b: Double(bytes[i * 4 + 2]) / 255 / a))
        }
        return result
    }

    private static func makeBlurred(_ image: CGImage) -> NSImage? {
        let input = CIImage(cgImage: image)
        let longest = max(input.extent.width, input.extent.height)
        guard longest > 0 else { return nil }
        let scale = 128 / longest
        let small = input.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        let extent = small.extent.integral
        let output = small.clampedToExtent()
            .applyingGaussianBlur(sigma: 9)
            .applyingFilter("CIColorControls", parameters: [
                kCIInputSaturationKey: 1.75,
                kCIInputContrastKey: 1.05,
            ])
            .cropped(to: extent)
        guard let cg = ciContext.createCGImage(output, from: extent) else { return nil }
        return NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
    }
}
