<p align="center">
  <img src="Assets/pipework-icon-1024.png" width="160" alt="Pipework icon">
</p>

# Pipework

A small macOS app for Piper text-to-speech. Paste text, browse and download Piper voices, sample them, and have it read to you. Built with C# on .NET 8 and Avalonia, so it runs on older Macs (I wrote it on Monterey).

## What it does

- Browse the full Piper voice catalog, filter by language, name or quality
- Download voices in-app, and sample any voice with a cached preview line
- Speak whatever you paste, with Pause, Stop, Replay and a speed slider (0.5x to 2x)
- Save the last audio as a WAV, or save the text in the box as a .txt

## Requirements

- macOS 12 (Monterey) or newer
- .NET 8 SDK
- Piper, installed with `pip3 install piper-tts`

Pipework looks for `piper` on your shell's PATH. If yours lives somewhere odd, set `PIPEWORK_PIPER` to the full path.

## Run it

    git clone https://github.com/smokeluce/pipework.git
    cd pipework
    dotnet run

Voices are stored in `~/.local/share/piper-voices`, the same folder Piper's own tooling uses, so voices you already have show up as installed. Cached samples and the voice list live in `~/Library/Application Support/Pipework`.

## Notes

- The build number in the window is the git commit count, with a `+` when there are uncommitted changes.
- Speed is applied at playback, so saved WAVs are always at normal speed.
- Pause uses SIGSTOP on the player, so audio can run on for a split second after you hit it.
- I wrap the published output in a `Pipework.app` by hand for now. A build script is on the list.

## Todo

- Clip length and size readout
- Guide button explaining Piper and the voice naming scheme
- Settings screen with a samples cache viewer and clear button
- Build script for the .app bundle

## Credits

Piper is from the Rhasspy project, and the voices come from [rhasspy/piper-voices](https://huggingface.co/rhasspy/piper-voices) on Hugging Face. Each voice has its own license, so check before using one in anything you ship.
