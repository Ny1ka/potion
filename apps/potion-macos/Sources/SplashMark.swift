import SwiftUI

/// The build-up animation (08-motion.md, "Splash / build-up animation"; reference splash/splash.html):
/// the four bands fill the flask from below, the neck and rim drop in, then the wordmark and tagline rise.
/// Plays once. With Reduce Motion on, the finished mark shows immediately.
/// Same geometry as `PotionMark` (100-unit box), built from shapes so each part can animate.
struct SplashMark: View {
    var size: CGFloat = 96
    var showsWordmark = true
    var tagline: String?

    @State private var played = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    // (top, height) in the 100-unit box, same as PotionMark; the bottom band runs past the flask and is clipped.
    private let bands: [(y: CGFloat, height: CGFloat, category: PotionCategory, delay: Double)] = [
        (76, 14, .docs, 0), (64, 10, .video, 0.15), (52, 10, .audio, 0.30), (40, 10, .image, 0.45),
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
            let drop = shown ? 0 : -12 * s
            let neckAnimation = animation(PotionMotion.emphasized(0.3), delay: 0.6)
            ZStack(alignment: .topLeading) {
                // Inside the flask: the bands fill it from below, then the neck fill drops on
                ZStack(alignment: .topLeading) {
                    Rectangle()
                        .fill(PotionColor.textPrimary)
                        .frame(width: 80 * s, height: 38 * s)
                        .offset(x: 10 * s, y: drop)
                        .opacity(shown ? 1 : 0)
                        .animation(neckAnimation, value: played)
                    ForEach(bands, id: \.y) { band in
                        Rectangle()
                            .fill(band.category.fill)
                            .frame(width: 80 * s, height: band.height * s)
                            .offset(x: 10 * s, y: band.y * s + (shown ? 0 : 16 * s))
                            .opacity(shown ? 1 : 0)
                            .animation(animation(PotionMotion.standard(0.42), delay: band.delay), value: played)
                    }
                }
                .frame(width: 100 * s, height: 100 * s, alignment: .topLeading)
                .clipShape(FlaskShape())
                // Rim, dropping on with the neck
                RoundedRectangle(cornerRadius: 3.5 * s)
                    .fill(PotionColor.textPrimary)
                    .frame(width: 26 * s, height: 7 * s)
                    .offset(x: 37 * s, y: 9 * s + drop)
                    .opacity(shown ? 1 : 0)
                    .animation(neckAnimation, value: played)
            }
            .frame(width: 100 * s, height: 100 * s, alignment: .topLeading)
        }
        .aspectRatio(1, contentMode: .fit)
    }
}

/// The Erlenmeyer flask silhouette from design-system/docs/02-logo.md (same path as logos/svg/potion-mark.svg).
private struct FlaskShape: Shape {
    func path(in rect: CGRect) -> Path {
        let s = min(rect.width, rect.height) / 100
        func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: rect.minX + x * s, y: rect.minY + y * s) }
        var path = Path()
        path.move(to: p(41, 14))
        path.addLine(to: p(59, 14))
        path.addLine(to: p(59, 38))
        path.addLine(to: p(81, 80))
        path.addQuadCurve(to: p(75, 88), control: p(84, 88))
        path.addLine(to: p(25, 88))
        path.addQuadCurve(to: p(19, 80), control: p(16, 88))
        path.addLine(to: p(41, 38))
        path.closeSubpath()
        return path
    }
}
