<p align="center">
  <img src="assets/icon/altabi-icon-1024.png" alt="Altabi icon" width="128" height="128">
</p>

<h1 align="center">Altabi</h1>

<p align="center">
  <strong>Alt+Tab by app, not by window.</strong>
</p>

<p align="center">
  A Mac-style application switcher for Windows, with window previews when you need them.
</p>

## Pop the Tab

Windows' Alt+Tab shows every window. Altabi shows every app, like Command+Tab on a Mac, and
remembers which window of each app you were using.

## Features

- ⚡ Instant. Tap Alt+Tab quickly to jump straight back to the previous app without seeing anything.
- 🗂️ Grouped. Windows are grouped by application, most recently used first.
- 🪟 Previews. Keep Alt held and the selected app's windows appear as live thumbnails. Press `Q` and `W` to pick one.
- 🔁 Window hopping. `Alt + Q` / `Alt + W` jump straight to the previous / next window of the app you are in.
- 🖱️ Mouse too. Hover an app or a preview to select it, click to switch.
- ✨ Native. Acrylic panel, smooth animations, sharp high-resolution icons.

## Use It

| Keys | Action |
| --- | --- |
| `Alt + Tab` | Select the previous app; release Alt to switch |
| `Tab` / `Shift + Tab` | Next / previous app while Alt is held |
| `Alt + Shift + Tab` | Start from the least recently used app |
| `Q` / `W` | Previous / next window of the selected app |
| `Alt + Q` / `Alt + W` | Previous / next window of the current app; release Alt to switch |
| `Esc` | Cancel without switching |

Releasing Alt within 85 ms of the first Tab switches without showing the switcher. A second Tab
shows it immediately. Previews appear for apps with more than one window. `Alt + Q` and `Alt + W`
behave like `Alt + Tab`, `Shift + Tab`, `Q`/`W`: a quick release switches silently, holding Alt shows
the switcher with the previews open.

Altabi only claims `Alt + Tab`, `Alt + Shift + Tab`, `Alt + Q` and `Alt + W`. Every other Alt
shortcut, and AltGr combinations such as `AltGr + Q` (@), reach the focused
application untouched. While the switcher is open, other keys are ignored.

## Portable releases and WinKit

Release packages are self-contained: no separate .NET installation is required. Altabi runs in
the notification area without opening a window, and a named mutex prevents duplicate
instances. The `winkit.json` release manifest describes portable packages and their SHA-256
checksums for WinKit. In managed mode, WinKit owns startup and
updates; the independent startup toggle is disabled.

Build from source with the .NET 10 SDK:

```powershell
dotnet build Altabi.slnx -c Release
./scripts/publish-portable.ps1 -AppName Altabi -ProjectPath src/Altabi/Altabi.csproj -Version v0.2.1
```

Diagnostics are written to `%LOCALAPPDATA%\Altabi\Logs\altabi.log`. Only switcher keys are
recorded, never typed text.

## Note

Windows does not let normal apps read the keyboard while a window with a higher integrity level
is focused: elevated apps such as Task Manager, and UIAccess apps such as TeamSpeak 3. There the
native Alt+Tab appears, and Altabi leaves it alone until it closes instead of opening a second
switcher on top. Run Altabi elevated if you need it there too.
