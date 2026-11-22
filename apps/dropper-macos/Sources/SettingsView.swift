import AppKit
import PotionCore
import SwiftUI

/// Settings keys (UserDefaults). Defaults come from core/spec/sources.json where it has one.
enum SettingsKey {
    /// Folder path; empty means Downloads.
    static let outputFolder = "outputFolder"
    static let nameTemplate = "nameTemplate"
    /// Gigabytes; 0 means no limit.
    static let maxSizeGB = "maxSizeGB"
    static let limitDuration = "limitDuration"
    static let maxDurationMinutes = "maxDurationMinutes"
    static let maxParallel = "maxParallel"
    static let keepSource = "keepSource"
    static let keepHistory = "keepHistory"
    static let notifications = "notifications"
}

/// Typed access to the settings, with their defaults.
enum DropperSettings {
    static var defaults: UserDefaults { .standard }
    private static var registry: SourceRegistry { .shared }

    static var outputFolder: URL {
        let path = defaults.string(forKey: SettingsKey.outputFolder) ?? ""
        if !path.isEmpty, FileManager.default.fileExists(atPath: path) { return URL(fileURLWithPath: path, isDirectory: true) }
        return FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
    }
    static var nameTemplate: String {
        let template = defaults.string(forKey: SettingsKey.nameTemplate) ?? ""
        return template.trimmingCharacters(in: .whitespaces).isEmpty ? registry.naming.defaultTemplate : template
    }
    static var defaultSizeGB: Int { Int(registry.download.defaultMaxBytes / 1_073_741_824) }
    static var maxBytes: Int64 {
        let gb = defaults.object(forKey: SettingsKey.maxSizeGB) as? Int ?? defaultSizeGB
        return gb == 0 ? Int64.max : Int64(gb) * 1_073_741_824
    }
    static var maxDurationMinutes: Int? {
        guard defaults.object(forKey: SettingsKey.limitDuration) as? Bool ?? true else { return nil }
        return defaults.object(forKey: SettingsKey.maxDurationMinutes) as? Int ?? registry.download.defaultMaxDurationMinutes
    }
    static var maxParallel: Int { defaults.object(forKey: SettingsKey.maxParallel) as? Int ?? 2 }
    static var keepSource: Bool { defaults.bool(forKey: SettingsKey.keepSource) }
    static var keepHistory: Bool { defaults.object(forKey: SettingsKey.keepHistory) as? Bool ?? true }
    static var notifications: Bool { defaults.object(forKey: SettingsKey.notifications) as? Bool ?? true }
}

/// 05-layout.md, "Settings": a standard Settings window with General, Output and Advanced tabs. Changes apply to
/// downloads queued after the change.
struct SettingsView: View {
    var body: some View {
        TabView {
            GeneralSettings()
                .tabItem { Label("General", systemImage: "gearshape") }
            OutputSettings()
                .tabItem { Label("Output", systemImage: "square.and.arrow.down") }
            AdvancedSettings()
                .tabItem { Label("Advanced", systemImage: "slider.horizontal.3") }
        }
        .frame(width: PotionSize.convertSheetWidth + PotionSpacing.x16)
    }
}

struct GeneralSettings: View {
    @EnvironmentObject private var model: DropperModel
    @EnvironmentObject private var queue: DropQueue
    @AppStorage(SettingsKey.notifications) private var notifications = true
    @AppStorage(SettingsKey.keepHistory) private var keepHistory = true
    @AppStorage(SettingsKey.maxParallel) private var maxParallel = 2
    @State private var confirmingClear = false

    var body: some View {
        Form {
            Toggle("Notify me when downloads finish while Potion Dropper is in the background", isOn: $notifications)
            Stepper(value: $maxParallel, in: 1...4) {
                LabeledContent("Downloads at once:") { Text("\(maxParallel)").monospacedDigit() }
            }
            .onChange(of: maxParallel) { queue.maxParallel = $0 }
            Section {
                Toggle("Keep a download history", isOn: $keepHistory)
                HStack {
                    Text("The history stays on this Mac and lists only what you see in Download History.")
                        .font(PotionFont.caption)
                        .foregroundStyle(PotionColor.textSecondary)
                    Spacer()
                    Button("Clear History…") { confirmingClear = true }
                        .disabled(model.history.entries.isEmpty)
                }
            }
        }
        .formStyle(.grouped)
        .fixedSize(horizontal: false, vertical: true)
        .confirmationDialog("Clear the download history?", isPresented: $confirmingClear) {
            Button("Clear History", role: .destructive) {
                try? model.history.clear()
                model.objectWillChange.send()
            }
        } message: {
            Text("The list is deleted from this Mac. Downloaded files stay where they are.")
        }
    }
}

struct OutputSettings: View {
    @AppStorage(SettingsKey.outputFolder) private var outputFolder = ""
    @AppStorage(SettingsKey.nameTemplate) private var nameTemplate = SourceRegistry.shared.naming.defaultTemplate
    @AppStorage(SettingsKey.keepSource) private var keepSource = false

    private var example: String {
        let registry = SourceRegistry.shared
        let name = DropNaming.render(DropperSettings.nameTemplate, fileName: "Interview.mp4", host: "www.example.com",
                                     date: DropJobRunner.localDate(), formatName: "MP3",
                                     maxLength: registry.download.maxFileNameLength - registry.naming.reserveForSuffix,
                                     fallback: registry.naming.fallbackName)
        return "\(name).mp3"
    }

    var body: some View {
        Form {
            Section {
                LabeledContent("Save downloads to:") {
                    HStack {
                        Text(FileManager.default.displayName(atPath: DropperSettings.outputFolder.path))
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .help(DropperSettings.outputFolder.path)
                        Button("Choose…", action: chooseFolder)
                    }
                }
                Toggle("Keep the original download next to the converted file", isOn: $keepSource)
            }
            Section {
                TextField("File names:", text: $nameTemplate)
                Text("Use {title}, {site}, {date} and {format}. Example: \(example)")
                    .font(PotionFont.caption)
                    .foregroundStyle(PotionColor.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .formStyle(.grouped)
        .fixedSize(horizontal: false, vertical: true)
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Choose"
        panel.directoryURL = DropperSettings.outputFolder
        guard panel.runModal() == .OK, let url = panel.url else { return }
        outputFolder = url.path
    }
}

struct AdvancedSettings: View {
    @AppStorage(SettingsKey.maxSizeGB) private var maxSizeGB = DropperSettings.defaultSizeGB
    @AppStorage(SettingsKey.limitDuration) private var limitDuration = true
    @AppStorage(SettingsKey.maxDurationMinutes) private var maxDurationMinutes = SourceRegistry.shared.download.defaultMaxDurationMinutes

    var body: some View {
        Form {
            Picker("Largest download:", selection: $maxSizeGB) {
                ForEach([1, 2, 4, 8, 16, 32], id: \.self) { Text("\($0) GB").tag($0) }
                Divider()
                Text("No limit").tag(0)
            }
            Section {
                Toggle("Limit the length of videos and audio", isOn: $limitDuration)
                Stepper(value: $maxDurationMinutes, in: 5...240, step: 5) {
                    LabeledContent("Longest:") { Text("\(maxDurationMinutes) minutes").monospacedDigit() }
                }
                .disabled(!limitDuration)
                Text("Longer files are deleted after downloading, since direct links don't say how long they are.")
                    .font(PotionFont.caption)
                    .foregroundStyle(PotionColor.textSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .formStyle(.grouped)
        .fixedSize(horizontal: false, vertical: true)
    }
}
