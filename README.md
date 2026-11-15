# CashVoice

A Clownfish-style system-wide voice changer + soundboard for Windows 10/11 (x64).

* **Voice changer** — pitch, robot, echo, filters, distortion; 9 built-in voices + your own.
* **System integration with no virtual mic** — a real Audio Processing Object (APO) hooks your actual microphone, the same way Clownfish / Equalizer APO do. Discord, OBS, games etc. just use the mic normally.
* **Soundboard** — import files, drag & drop, or paste a YouTube link (downloaded with yt-dlp into `%APPDATA%\CashVoice\Sounds`). Sounds play *into the mic* (and optionally on your speakers).
* **Custom pitch slider (±24 semitones) on every voice.
* **Hotkeys for everything** — voice on/off, next/previous voice, mute mic, stop sounds, one hotkey per voice and per sound.
* Tray app, optional start-with-Windows.

## Get the installer (no Visual Studio needed)

1. Create a new GitHub repo and push this folder to the `main` branch.
2. Open **Actions → Build CashVoice installer** (runs on every push, or press *Run workflow*).
3. When it finishes, download the **CashVoice-Setup** artifact → `CashVoice-Setup.exe`.
4. Run it (admin). It trusts the build's self-signed certificate, registers the APO, installs the app.
5. Open CashVoice → **Settings** → tick your real microphone(s) → **Apply…** (UAC prompt; Windows audio restarts for a second).
6. Restart apps that were already using the mic. Done.

Uninstalling restores each mic's original driver effect.

## How it works

```
 CashVoice.exe (WPF, tray)                      audiodg.exe (Windows audio engine)
 ├─ hotkeys (RegisterHotKey)                     └─ CashVoiceAPO.dll  (stream effect on your mic)
 ├─ voice params ───────────────┐                    ├─ pitch shift / ring mod / echo / filters
 ├─ soundboard mixer ──► ring ──┼─ shared memory ──► ├─ mixes soundboard ring into the mic
 └─ yt-dlp + ffmpeg import      │  Global\CashVoiceShared
```

* `apo/` – C++ COM DLL (MSVC, static CRT). `dsp.h` is the platform-independent DSP.
* `app/` – .NET 8 WPF app. `Core/ApoInstaller.cs` writes the endpoint registry keys and backs up the old effect.
* `installer/` – Inno Setup script. `.github/workflows/build.yml` builds everything.

## Troubleshooting

* **"Driver not running yet"** – no mic applied yet, or the app using the mic hasn't been restarted.
* **Mic stops working** – Settings → untick the mic → Apply to revert.
* **Windows refuses to load the APO** – the audio engine can reject APOs with an untrusted signature. The installer trusts the build's self-signed cert; if it still won't load, test with `bcdedit /set testsigning on` (admin, reboot) to confirm it's signing.
* **Hotkey doesn't work** – used by another app (Settings lists conflicts), or an elevated window has focus.
* **YouTube import fails** – Settings → *Update yt-dlp*.

## Known limitations (v0.1)

* Simple dual-tap pitch shifter: fine for fun voices, some warble on big shifts.
* Sounds reach the mic ~60 ms after the hotkey.
* Not yet tested on real hardware — expect a debugging round.
