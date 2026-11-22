import PotionCore
import SwiftUI

/// Settings window (05-layout.md, "Settings"): General, Output and Advanced tabs. Standard controls only.
struct SettingsView: View {
    /// The tab Settings reopens on.
    @AppStorage("settings.tab") private var tab = "general"

    var body: some View {
        TabView(selection: $tab) {
            GeneralSettings()
                .tabItem { Label("General", systemImage: "gearshape") }
                .tag("general")
            OutputSettings()
                .tabItem { Label("Output", systemImage: "square.and.arrow.down") }
                .tag("output")
            AdvancedSettings()
                .tabItem { Label("Advanced", systemImage: "slider.horizontal.3") }
                .tag("advanced")
        }
        .frame(width: PotionSize.convertSheetWidth + PotionSpacing.x16)
    }
}

struct GeneralSettings: View {
    @AppStorage(SettingsKey.includeSubfolders) private var includeSubfolders = true
    @AppStorage(SettingsKey.notifications) private var notifications = true

    var body: some View {
        Form {
            Toggle("Include subfolders when adding a folder", isOn: $includeSubfolders)
            Toggle("Notify me when conversions finish while Potion is in the background", isOn: $notifications)
        }
        .formStyle(.grouped)
        .fixedSize(horizontal: false, vertical: true)
    }
}

struct OutputSettings: View {
    @EnvironmentObject var queue: QueueModel

    var body: some View {
        Form {
            Section {
                LabeledContent("Save converted files to:") { SaveToPicker(showLabel: false) }
                Toggle("Move originals to Trash after converting", isOn: $queue.moveOriginalsToTrash)
            }
            Section("Default format for new files") {
                ForEach(FormatCategory.allCases, id: \.self) { category in
                    DefaultFormatPicker(category: category)
                }
            }
        }
        .formStyle(.grouped)
        .fixedSize(horizontal: false, vertical: true)
    }
}

/// The format new files of a category start with. Potion also updates it whenever a format is picked in the queue.
struct DefaultFormatPicker: View {
    let category: FormatCategory
    @AppStorage private var formatID: String

    init(category: FormatCategory) {
        self.category = category
        _formatID = AppStorage(wrappedValue: "", SettingsKey.lastOutput(category))
    }

    var body: some View {
        let formats = Self.outputs(for: category)
        Picker(PotionCategory(rawValue: category.rawValue)?.label ?? category.rawValue, selection: $formatID) {
            Text("Automatic").tag("")
            // Same-category formats first, then the others under their category (video files can default to MP3 or GIF).
            ForEach(FormatGroups.grouped(formats), id: \.category) { group in
                Divider()
                if group.category.rawValue != category.rawValue { Text(group.category.label) }
                ForEach(group.formats) { Text($0.name).tag($0.id) }
            }
        }
    }

    /// Every format files of this category can become (union over its formats), the same category first, so a
    /// remembered choice like "video → MP3" always appears in the list.
    static func outputs(for category: FormatCategory) -> [FileFormat] {
        var seen = Set<String>()
        var result: [FileFormat] = []
        for format in FormatRegistry.shared.formats where format.category == category && format.isAvailable(on: ConversionMatrix.currentPlatform) {
            for option in ConversionMatrix.shared.outputs(for: format.id) where seen.insert(option.output.id).inserted {
                result.append(option.output)
            }
        }
        return result.filter { $0.category == category } + result.filter { $0.category != category }
    }
}

struct AdvancedSettings: View {
    @EnvironmentObject var queue: QueueModel
    @AppStorage(SettingsKey.maxParallel) private var maxParallel = QueueModel.defaultParallel

    var body: some View {
        Form {
            Stepper(value: $maxParallel, in: 1...QueueModel.parallelLimit) {
                LabeledContent("Convert at the same time:") {
                    Text(maxParallel == 1 ? "1 file" : "\(maxParallel) files").monospacedDigit()
                }
            }
            LabeledContent("Audio and video tools:") {
                if queue.service.tools != nil {
                    Label("Installed", systemImage: "checkmark").foregroundStyle(PotionColor.success)
                } else {
                    Label("Missing. Reinstall Potion to restore them.", systemImage: "exclamationmark.circle")
                        .foregroundStyle(PotionColor.error)
                }
            }
        }
        .formStyle(.grouped)
        .fixedSize(horizontal: false, vertical: true)
    }
}
