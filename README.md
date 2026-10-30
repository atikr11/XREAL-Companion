# XREAL Companion

A Windows HUD prototype for XREAL Air 2. It provides separate Daily, School, Developer, and AI panels, offline voice commands, and a desktop settings window.

> This is an experimental community project. It is not made, supported, or endorsed by XREAL.

## What it does

- Shows independently placeable Daily, School, Developer, and AI panels.
- Uses Air 2 motion readings over direct USB/HID for 3DoF head movement.
- Uses the XREAL Experimental Unity package for Windows fullscreen integration. Head tracking is handled by the app's direct USB/HID reader, not by XREAL SDK tracking APIs. See [XREAL's privacy policy](https://www.xreal.com/privacy-policy) for information about the SDK provider.
- Includes local offline voice commands. Microphone processing uses Vosk on the PC; the app does not upload or save microphone recordings.
- Offers optional AI chat through a user-configured OpenAI-compatible service.

## AI and privacy

No AI API key is included in the source or release. To use AI, users provide their own compatible endpoint, model, and API key. The key is held in memory for the current app session and is not saved by the app. AI requests are only sent when the user chooses **Ask**; the selected provider receives the question and any data included in it.

Voice recognition runs locally with the bundled Vosk English model. Listening starts only when the user starts it in settings or holds **M** in the glasses app. Audio is processed locally and is not uploaded or saved as a recording.

Tasks, notes, flashcards, layouts, and voice-command mappings are stored locally in the user's Windows `LocalLow` application data folder.

## Download and run

Download the complete Windows release archive and extract the entire folder. Run `XREAL Companion.exe` from the extracted folder; the executable alone is not sufficient because Unity requires the adjacent `_Data` folder and runtime files.

Connect the Air 2 directly over a USB-C port that supports DisplayPort and USB data. Hold the glasses still during startup calibration. Press **F1** to open the separate desktop settings window. Use its Overview, Panels, Daily, School, Developer, AI, Voice, and Appearance pages to configure the HUD. The Appearance page can show or hide the bottom keyboard hints on the panels.

## Build from source

- Unity Editor `6000.0.85f1`
- Windows build support for Unity
- XREAL Unity SDK package archives obtained from the [official XREAL developer download page](https://developer.xreal.com/download/) and used under XREAL's applicable API terms

The XREAL package archives are not included here. Download the two package archives from XREAL's official developer portal, accept the applicable API terms, and put them in the project's `Packages/LocalSDK/` folder with these exact names: `com.xreal.xr.tar.gz` and `com.xreal.xr.experimental.tar.gz`. The project manifest uses relative paths, and `.gitignore` excludes the archives. Do not commit or redistribute the SDK archives unless XREAL's terms explicitly allow it. The imported XREAL sample folder is also excluded from the public source.

The current Windows build contains XREAL SDK runtime files. Confirm XREAL's applicable terms allow redistribution of those runtime files before publishing a built-app archive.

Open the project in Unity, resolve the XREAL package dependency, then use **XREAL Companion > Build Air 2 Prototype**.

## Controls

- **F1**: open desktop settings
- **P**: move the group of panels to the current viewing direction while keeping their relative arrangement
- **M (hold)**: listen for a voice command while held; release to stop
- **Alt+1..4**: select a section
- **R**: recalibrate
- **H**: show/hide the selected panel
- **Esc**: exit the glasses app

## Known limits

Tracking is 3DoF orientation tracking. There is no absolute heading source in the sensor stream, so long-term yaw drift cannot be eliminated completely. Panel placement is relative to the current tracking origin; it is not a persistent room anchor. The panels are local widgets, not captured Windows application windows or virtual monitors.

## Third-party components

The project includes attribution and license files for the direct HID protocol research and the Vosk library/model. See `Assets/XREALCompanionStandalone/THIRD-PARTY-NOTICES.txt`, `Assets/XREALCompanionStandalone/ar-drivers-LICENSE.txt`, and `Assets/XREALCompanionStandalone/Apache-2.0-Vosk.txt`. The XREAL SDK is a separate dependency governed by XREAL's terms.

## License

The original XREAL Companion code is licensed under MIT; see `LICENSE`. Third-party components retain their own licenses and terms. In particular, MIT does not grant rights to the XREAL SDK or its runtime files.
