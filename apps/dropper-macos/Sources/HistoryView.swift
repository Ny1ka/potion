import PotionCore
import SwiftUI

/// Download history (spec Part 6.1.4): only what the user can see here, and Clear History forgets all of it.
/// The downloaded files themselves are never touched.
struct HistoryView: View {
    @EnvironmentObject private var model: DropperModel
    @State private var confirmingClear = false

    private static let dateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .short
        return formatter
    }()

    var body: some View {
        let entries = model.history.entries
        VStack(spacing: 0) {
            if entries.isEmpty {
                Text("Downloads you finish appear here.")
                    .font(PotionFont.secondary)
                    .foregroundStyle(PotionColor.textSecondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                List(entries) { entry in
                    HistoryRow(entry: entry)
                        .contextMenu {
                            if !entry.files.isEmpty {
                                Button("Show in Finder") { model.showInFinder(entry.files.map(URL.init(fileURLWithPath:))) }
                            }
                            Button("Copy Link") {
                                NSPasteboard.general.clearContents()
                                NSPasteboard.general.setString(entry.link, forType: .string)
                            }
                            Divider()
                            Button("Remove from History") {
                                try? model.history.remove(entry.id)
                                model.objectWillChange.send()
                            }
                        }
                }
                .listStyle(.inset)
            }
            Divider()
            HStack {
                Text(entries.count == 1 ? "1 download" : "\(entries.count) downloads")
                    .font(PotionFont.secondary)
                    .foregroundStyle(PotionColor.textSecondary)
                Spacer()
                Button("Clear History…") { confirmingClear = true }
                    .disabled(entries.isEmpty)
            }
            .padding(PotionSpacing.x3)
        }
        .frame(minWidth: PotionSize.windowMin.width, minHeight: PotionSize.windowMin.height)
        .confirmationDialog("Clear the download history?", isPresented: $confirmingClear) {
            Button("Clear History", role: .destructive) {
                try? model.history.clear()
                model.objectWillChange.send()
            }
        } message: {
            Text("The list is deleted from this Mac. Downloaded files stay where they are.")
        }
    }

    struct HistoryRow: View {
        let entry: DropHistory.Entry

        var body: some View {
            HStack(spacing: PotionSpacing.x3) {
                Image(systemName: entry.error == nil ? "checkmark.circle" : "exclamationmark.circle")
                    .foregroundStyle(entry.error == nil ? PotionColor.success : PotionColor.error)
                    .accessibilityLabel(entry.error == nil ? "Finished" : "Failed")
                VStack(alignment: .leading, spacing: PotionSpacing.xHalf) {
                    Text(entry.name).font(PotionFont.fileName).lineLimit(1).truncationMode(.middle)
                    Text(entry.error ?? "\(entry.site) · \(HistoryView.dateFormatter.string(from: entry.date))")
                        .font(PotionFont.secondary)
                        .foregroundStyle(entry.error == nil ? PotionColor.textSecondary : PotionColor.error)
                        .lineLimit(1)
                }
            }
            .help(entry.link)
        }
    }
}
