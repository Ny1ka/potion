# Privacy

Effective 22 November 2026. This policy covers Potion and Potion Dropper for macOS and Windows.

**The short version:** both apps work on your computer. They don't collect, store or send any information about
you to us or to anyone else. There are no accounts, no analytics, no telemetry and no crash reporting.

## Potion

- Potion converts files on your computer. Your files are never uploaded.
- Potion makes no network connections.
- Settings you change (for example the save folder, the default formats or "Move originals to Trash") are saved
  on your computer: in the app's preferences on macOS, and in `%LOCALAPPDATA%\Potion\settings.json` on Windows.
- If you turn on "Convert with Potion" in File Explorer, Potion adds an entry to your user's part of the Windows
  registry. Turning it off removes it.

## Potion Dropper

- Potion Dropper connects only to the links you give it. For each site, it also asks for that site's `robots.txt`
  file, so it can respect the site's rules for automated tools.
- Each request identifies the app honestly as `PotionDropper/<version> (+https://github.com/Ny1ka/potion)`. It
  sends no cookies, no saved passwords and no other identifiers. Like any download, the site you download from
  sees your IP address and the request itself, under that site's own privacy policy.
- Downloaded files are converted on your computer and never uploaded.
- **Download history:** the app keeps a list of your finished and failed downloads (the link, the site, the file
  name, the date and where it was saved) on your computer, so you can find them again in Download History. Nothing
  else is kept. You can remove entries, clear the whole list, or turn the history off in Settings. Clearing it
  never deletes your downloaded files.
- Settings you change are saved on your computer: in the app's preferences on macOS, and in
  `%LOCALAPPDATA%\Potion Dropper\settings.json` on Windows.

## Both apps

- Notifications ("12 files converted", "3 downloads finished") are shown by your operating system and stay on your
  computer.
- Neither app checks for updates or contacts us. If automatic update checks are ever added, they'll be optional,
  and this policy will say exactly what they send before they're turned on.
- Your operating system may collect crash or usage reports for all apps, depending on your system settings. Those
  reports go to Apple or Microsoft under their privacy policies, not to us.
- The apps aren't directed at children and don't knowingly collect anything from anyone.

## Changes and contact

If this policy changes, the new version will be published with the app and in the project repository
(https://github.com/Ny1ka/potion), with a new effective date.

Questions: nyikabusiness@gmail.com
