import SwiftUI

/// First-launch onboarding (06-components.md, "Onboarding"): three short, skippable panels.
struct OnboardingView: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var page = 0
    private let lastPage = 2

    var body: some View {
        VStack(spacing: 0) {
            ZStack {
                switch page {
                case 0: welcome
                case 1: categories
                default: privacy
                }
            }
            .frame(maxWidth: 480)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .padding(PotionSpacing.x8)
            .transition(.opacity)
            .id(page)

            Divider()

            HStack(spacing: PotionSpacing.x2) {
                if page < lastPage {
                    Button("Skip") { finish() }
                        .keyboardShortcut(.cancelAction)
                } else {
                    Button("Back") { go(to: page - 1) }
                }
                Spacer()
                PageDots(count: lastPage + 1, current: page)
                Spacer()
                if page < lastPage {
                    Button("Continue") { go(to: page + 1) }
                        .keyboardShortcut(.defaultAction)
                } else {
                    Button("Get Started") { finish() }
                        .keyboardShortcut(.defaultAction)
                }
            }
            .padding(PotionSpacing.x4)
        }
        .frame(width: PotionSize.windowMin.width, height: PotionSize.windowMin.height)
        .background(PotionColor.background)
    }

    private func go(to newPage: Int) {
        withAnimation(reduceMotion ? nil : PotionMotion.standard(PotionMotion.slow)) { page = newPage }
    }

    private func finish() {
        UserDefaults.standard.set(true, forKey: SettingsKey.onboarded)
        dismiss()
    }

    private var welcome: some View {
        SplashMark(size: 96, tagline: "Any file in. The format you need out.")
    }

    private var categories: some View {
        VStack(spacing: PotionSpacing.x6) {
            HStack(spacing: PotionSpacing.x6) {
                ForEach(PotionCategory.allCases) { category in
                    VStack(spacing: PotionSpacing.x2) {
                        PotionCategoryTile(category, size: PotionSize.categoryTileXL)
                            .accessibilityHidden(true)
                        Text(category.label)
                            .font(PotionFont.secondary)
                            .foregroundStyle(PotionColor.textSecondary)
                    }
                }
            }
            Text("Drop anything. Potion shows you what it can become.")
                .font(PotionFont.title)
                .foregroundStyle(PotionColor.textPrimary)
                .multilineTextAlignment(.center)
        }
    }

    private var privacy: some View {
        VStack(spacing: PotionSpacing.x4) {
            Image(systemName: "lock")
                .font(.system(size: 34))
                .foregroundStyle(PotionColor.textSecondary)
                .accessibilityHidden(true)
            Text("Everything converts on this computer. Nothing is uploaded.")
                .font(PotionFont.title)
                .foregroundStyle(PotionColor.textPrimary)
                .multilineTextAlignment(.center)
            Text(AboutInfo.potionNote)   // spec Part 6.2, word for word
                .font(PotionFont.secondary)
                .foregroundStyle(PotionColor.textSecondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
            Text("In Finder, select files, Control-click and choose Services › Convert with Potion.")
                .font(PotionFont.secondary)
                .foregroundStyle(PotionColor.textSecondary)
                .multilineTextAlignment(.center)
            AgreementLine()
        }
    }
}

/// Small page indicator for the onboarding panels.
private struct PageDots: View {
    let count: Int
    let current: Int

    var body: some View {
        HStack(spacing: PotionSpacing.x2) {
            ForEach(0..<count, id: \.self) { index in
                Circle()
                    .fill(index == current ? PotionColor.textPrimary : PotionColor.textTertiary.opacity(0.5))
                    .frame(width: PotionSpacing.x1 + PotionSpacing.xHalf, height: PotionSpacing.x1 + PotionSpacing.xHalf)
            }
        }
        .accessibilityElement()
        .accessibilityLabel("Page \(current + 1) of \(count)")
    }
}
