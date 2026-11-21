import SwiftUI

/// Main window content. M0 shows the empty-state drop zone; dropping and the queue arrive in M3.
struct ContentView: View {
    var body: some View {
        DropZoneView()
            .padding(PotionSpacing.x5)
    }
}

/// Empty-state drop zone (design-system/docs/06-components.md, "Drop zone").
struct DropZoneView: View {
    var body: some View {
        VStack(spacing: 6) {
            Image("MarkMono")
                .resizable()
                .renderingMode(.template)
                .frame(width: 48, height: 48)
                .foregroundStyle(PotionColor.textTertiary)
                .accessibilityHidden(true)
            Text("Drop files or folders")
                .font(.body.weight(.semibold))
                .foregroundStyle(PotionColor.textPrimary)
            Text("Video, audio, images, documents and PDFs")
                .font(PotionFont.secondary)
                .foregroundStyle(PotionColor.textSecondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .frame(minHeight: PotionSize.dropZoneMinHeight)
        .overlay(
            RoundedRectangle(cornerRadius: PotionRadius.lg, style: .continuous)
                .strokeBorder(PotionColor.borderStrong, style: StrokeStyle(lineWidth: 1.5, dash: [6, 4]))
        )
        .accessibilityElement(children: .combine)
    }
}

#Preview {
    ContentView()
        .frame(width: PotionSize.windowDefault.width, height: PotionSize.windowDefault.height)
}
