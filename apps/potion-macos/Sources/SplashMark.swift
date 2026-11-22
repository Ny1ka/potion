import SwiftUI

/// The build-up animation (08-motion.md, "Splash / build-up animation"; reference splash/splash.html):
/// the four bands fill the flask from below, the neck drops in, then the wordmark and tagline rise.
/// Plays once. With Reduce Motion on, the finished mark shows immediately.
/// Same geometry as `PotionMark` (100-unit box), built from shapes so each part can animate.
struct SplashMark: View {
    var size: CGFloat = 96
    var showsWordmark = true
    var tagline: String?

    @State private var played = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private let bands: [(y: CGFloat, category: PotionCategory, delay: Double)] = [
        (75.5, .docs, 0), (61, .video, 0.15), (46.5, .audio, 0.30), (32, .image, 0.45),
    ]

    var body: some View {
        VStack(spacing: PotionSpacing.x4) {
            mark
                .frame(width: size, height: size)
                .accessibilityLabel("Potion")
            if showsWordmark {
                VStack(spacing: PotionSpacing.x2) {
                    Text("potion")
                        .font(PotionFont.brand(size * 0.42))
                        .foregroundStyle(PotionColor.textPrimary)
                    if let tagline {
                        Text(tagline)
                            .font(PotionFont.body)
                            .foregroundStyle(PotionColor.textSecondary)
                    }
                }
                .opacity(shown ? 1 : 0)
                .offset(y: shown ? 0 : 10)
                .animation(animation(PotionMotion.standard(0.4), delay: 0.75), value: played)
            }
        }
        .onAppear { played = true }
    }

    private var shown: Bool { played || reduceMotion }

    private func animation(_ base: Animation, delay: Double) -> Animation? {
        reduceMotion ? nil : base.delay(delay)
    }

    private var mark: some View {
        GeometryReader { geometry in
            let s = min(geometry.size.width, geometry.size.height) / 100
            ZStack(alignment: .topLeading) {
                // Neck
                RoundedRectangle(cornerRadius: 3 * s, style: .continuous)
                    .fill(PotionColor.textPrimary)
                    .frame(width: 14 * s, height: 18 * s)
                    .offset(x: 43 * s, y: 12 * s + (shown ? 0 : -12 * s))
                    .opacity(shown ? 1 : 0)
                    .animation(animation(PotionMotion.emphasized(0.3), delay: 0.6), value: played)
                // Bands, clipped by the bulb
                ZStack(alignment: .topLeading) {
                    ForEach(bands, id: \.y) { band in
                        Rectangle()
                            .fill(band.category.fill)
                            .frame(width: 60 * s, height: 12.5 * s)
                            .offset(x: -2 * s, y: (band.y - 32) * s + (shown ? 0 : 16 * s))
                            .opacity(shown ? 1 : 0)
                            .animation(animation(PotionMotion.standard(0.42), delay: band.delay), value: played)
                    }
                }
                .frame(width: 56 * s, height: 56 * s, alignment: .topLeading)
                .clipShape(Circle())
                .offset(x: 22 * s, y: 32 * s)
            }
            .frame(width: 100 * s, height: 100 * s, alignment: .topLeading)
        }
        .aspectRatio(1, contentMode: .fit)
    }
}
