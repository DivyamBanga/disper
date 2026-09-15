# Disper

A fully local, always-on voice dictation app for Windows 11 — like Wispr Flow, but everything runs
on your machine. Hold a key anywhere, speak, let go, and your words drop into whatever you're typing in.
No cloud, no account, no audio ever leaves the computer.

## How it feels

- **Hold to talk.** Hold **Right Alt** anywhere. A small dark pill fades in at the bottom-center of the
  screen with audio-reactive bars. Speak, release, and the text appears where your cursor is.
- **Quick tap to lock.** A short tap starts hands-free mode — it keeps listening until you tap again.
  Press **Esc** to cancel.
- **Snappy.** The speech model stays loaded in memory, so a ten-second clip transcribes in well under a
  second on this hardware (measured ~0.7 s). Punctuation and capitalization come from the model.
- **Out of the way.** The pill never steals focus and is click-through. When idle it's completely invisible.

## The dashboard

Left-click the tray icon (or the Start Menu shortcut) to open it.

- **Home** — greeting, words dictated today and all-time, estimated time saved, day streak, and a mic test.
- **History** — a searchable local log of everything you've dictated, with copy and delete.
- **Dictionary** — teach Disper the exact spelling of names and terms ("Divyam", "uWaterloo"); close
  misspellings and "sounds like" variants are corrected before the text is inserted.
- **Snippets** — say a trigger phrase ("my email") to expand a saved block of text.
- **Settings** — hotkey, hold-vs-tap, paste vs. type, filler-word removal, history, sounds, start-at-login,
  microphone, your name, accent color, and the speech model / thread count.

## What's under the hood

| Piece | Choice |
| --- | --- |
| UI | C# / .NET 9 / WPF, custom-drawn overlay and tray |
| Speech-to-text | [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) 1.13.8 running one of three CPU int8 models (see below) |
| Audio | WASAPI shared-mode capture (NAudio), converted to 16 kHz mono, silence-trimmed |
| Hotkey | Low-level keyboard hook on its own thread; the key is swallowed so other apps never see it |
| Insertion | Clipboard paste with save/restore (console-aware), or simulated Unicode typing |
| Cleanup | Rule-based: filler removal, personal dictionary, snippets. No LLM, so it adds no latency |

Models are downloaded once to `%LOCALAPPDATA%\Disper\models`. Settings and history live in
`%APPDATA%\Disper`. Logs are in `%LOCALAPPDATA%\Disper\logs`.

## Build & run

Requires the .NET 9 SDK (Windows). From the repo root:

```
dotnet build src/Disper -c Debug
dotnet run --project src/Disper
```

On first launch, if the model isn't present it downloads (~480 MB) and the pill shows the progress. To
produce an installed build in `%LOCALAPPDATA%\Programs\Disper`:

```
dotnet publish src/Disper -c Release -r win-x64 --self-contained false -o "%LOCALAPPDATA%\Programs\Disper"
```

The app registers itself to start at login (toggle in Settings) and runs from the tray.

## Speech models

Pick one in Settings — the chosen model downloads once and is then fully offline. Measured on this laptop
(12 s clip):

| Model | Feel | English WER | Latency | RAM | Download |
| --- | --- | --- | --- | --- | --- |
| **Parakeet 0.6B** (default) | Balanced — most accurate | ~6% | ~0.7–1.8 s | ~0.9 GB | 482 MB |
| **Parakeet 110M** | Light — fast, low memory | ~7.5% | ~0.5–0.7 s | ~0.4 GB | 137 MB |
| **Moonshine Base** | Different engine, MIT-licensed | ~10% | ~1.1 s | ~0.4 GB | 185 MB |

All three add punctuation and capitalization and run entirely on the CPU. Parakeet 110M is the snappiest with
almost no accuracy cost, so switch to it if you want the fastest possible turnaround.

## Notes

- English only.
- Memory: the resident model uses roughly 0.4–0.9 GB depending on the model — the price of instant, offline transcription.
- Regenerate the icons after editing `tools/make_icons.py` with `python tools/make_icons.py`.
- `tools/Bench` times the models on your CPU across thread counts.
