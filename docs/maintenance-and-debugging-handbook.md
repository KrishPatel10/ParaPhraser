# ParaPhraser maintenance and debugging handbook

This is the self-contained technical manual for the current ParaPhraser project. It is written so that you can rebuild, run, diagnose, modify, and recover the application without needing its original author or an AI assistant.

The guide describes the code as it exists now. When behavior and this document disagree, the source code is the final authority. After a meaningful architectural change, update this file in the same commit.

## 1. What the application is

ParaPhraser is a Windows tray application with no normal main window. It waits in the background for a global keyboard shortcut, copies the text currently selected in another application, opens a WPF overlay near the mouse, sends an instruction and context to a local Ollama model, and lets the user copy or paste the result back into the original application.

The default local model is `gemma3:4b`. The API stays on the local machine at `http://127.0.0.1:11434`; the application contains no cloud AI client and stores no rewrite history.

Current user flow:

1. ParaPhraser starts, creates a tray icon, registers two Windows-wide shortcuts, and checks Ollama.
2. The user selects text in Notepad, Teams, a browser, Outlook, or another application.
3. `Ctrl+Alt+G` starts in Fix grammar mode; `Ctrl+Alt+R` starts in Rewrite mode.
4. ParaPhraser temporarily sends `Ctrl+C`, reads the selected text, and restores the previous clipboard.
5. The overlay opens. The selected text and instruction are editable.
6. The user chooses Fix grammar, Rewrite, or Shorten and a tone. The UI shows that output is always English.
7. Generate sends a structured request to local Ollama.
8. The model returns replacement text. ParaPhraser performs an extra correction request if the result is not English.
9. Copy puts the result on the clipboard. Replace returns focus to the original window and sends `Ctrl+V`.

## 2. The shortest useful mental model

```text
Windows sign-in / manual launch
        |
        v
App.xaml.cs -- mutex, tray icon, startup check, hotkey registration
        |
  Ctrl+Alt+G/R
        |
        v
GlobalHotkeyService -- receives WM_HOTKEY
        |
        v
RewriteCoordinator -- owns the complete operation
        |
        +--> ClipboardSelectionService -- copy selection and restore clipboard
        |
        +--> OverlayWindow -- edit context/options and click Generate
                 |
                 v
        OllamaTextTransformer -- POST /api/chat to gemma3:4b
                 |
                 v
        OverlayWindow -- review result
                 |
          Copy or Replace
                 |
                 v
        ClipboardSelectionService -- reactivate original window and paste
```

If a failure happens, first locate its stage in this chain. This avoids changing Ollama code for a clipboard problem or changing UI code for a hotkey-registration problem.

## 3. Repository map and ownership

```text
ParaPhraser/
|-- global.json                         Required .NET SDK family
|-- Directory.Build.props               Shared compiler rules
|-- ParaPhraser.sln                      Solution containing all projects
|-- README.md                            Short user-oriented setup guide
|-- docs/
|   |-- architecture.md                  Earlier architectural overview
|   `-- maintenance-and-debugging-handbook.md  This manual
|-- scripts/
|   |-- build.ps1                        Restore + Release build + smoke tests
|   `-- publish-win-x64.ps1              Self-contained single-file publish
|-- src/
|   |-- ParaPhraser.Core/                AI-independent domain and Ollama client
|   |   |-- ITextTransformer.cs          Provider abstraction
|   |   |-- RewriteRequest.cs            Everything sent into a transformation
|   |   |-- RewriteResult.cs             Text/provider/duration result
|   |   |-- RewriteContext.cs            App/window/optional surrounding text
|   |   |-- RewriteMode.cs               Grammar/Rewrite/Shorten/Professional
|   |   |-- RewriteTone.cs               Original/Polite/Professional/Casual/Emojified
|   |   |-- WritingLanguage.cs            Source-language classification
|   |   |-- WritingLanguageResolver.cs    Lightweight source/output detection
|   |   |-- OllamaTextTransformer.cs      Local AI readiness, prompts, API calls
|   |   |-- LocalAiStatus.cs              Ready flag and human-readable message
|   |   |-- LocalAiUnavailableException.cs Expected local-AI outage error
|   |   `-- DemoTextTransformer.cs        Deterministic smoke-test transformer
|   `-- ParaPhraser.Desktop/             WPF/WinForms Windows shell
|       |-- App.xaml                      Global colors and control templates
|       |-- App.xaml.cs                   Process lifetime and composition root
|       |-- OverlayWindow.xaml            Complete overlay UI
|       |-- OverlayWindow.xaml.cs         UI state and event handlers
|       |-- app.manifest                  asInvoker and Windows compatibility
|       `-- Services/
|           |-- GlobalHotkeyService.cs    RegisterHotKey + WM_HOTKEY sink
|           |-- RewriteCoordinator.cs     Orchestrates capture/generate/replace
|           |-- ClipboardSelectionService.cs Clipboard and focus workflow
|           |-- ClipboardSnapshot.cs      Safe clipboard snapshot/restore
|           |-- ClipboardRetry.cs         Async busy-clipboard retry policy
|           |-- StartupRegistrationService.cs HKCU Run registration
|           |-- NativeMethods.cs          user32.dll interop and SendInput
|           |-- SelectionContext.cs       Text + original HWND + app metadata
|           |-- HotkeyModifiers.cs        Win32 modifier enum
|           `-- VirtualKey.cs             G/R virtual-key enum
`-- tests/
    `-- ParaPhraser.Core.SmokeTests/      Executable assertions, no test framework
```

The Core project must not depend on WPF, WinForms, the tray icon, or clipboard APIs. The Desktop project may depend on Core. Keeping that direction makes the AI behavior testable without launching the Windows UI.

## 4. Toolchain and build configuration

### Required toolchain

- Windows 10 or Windows 11, x64.
- .NET 10 SDK. `global.json` requests `10.0.100` and allows the latest compatible feature band. A newer installed .NET 10 SDK such as `10.0.400` is valid.
- Visual Studio 2026/18.x with the **.NET desktop development** workload, or the `dotnet` CLI alone.
- Ollama and `gemma3:4b` for real transformations. Ollama is not needed for the normal deterministic smoke test.

Visual Studio 2022 17.x does not support targeting .NET 10. It can produce confusing SDK/targeting errors even when the CLI works.

### Important compiler rules

`Directory.Build.props` enables nullable references, implicit usings, the latest C# language version, deterministic builds, and `TreatWarningsAsErrors`. A warning therefore fails the build. Fix the warning; do not casually disable the rule.

The Desktop project intentionally enables both:

```xml
<UseWPF>true</UseWPF>
<UseWindowsForms>true</UseWindowsForms>
```

WPF supplies the overlay and clipboard APIs. WinForms supplies `NotifyIcon` and its tray menu. Types such as `IDataObject`, `Clipboard`, `Point`, and `KeyEventArgs` exist in both frameworks, so use explicit aliases or fully qualified names when a compiler reports ambiguity. Do not remove WinForms just to hide the ambiguity; that breaks the tray integration.

### Normal clean verification

Run from `C:\Learning\ParaPhraser`:

```powershell
dotnet --info
dotnet restore .\ParaPhraser.sln
dotnet build .\ParaPhraser.sln --configuration Release --no-restore
dotnet run --project .\tests\ParaPhraser.Core.SmokeTests\ParaPhraser.Core.SmokeTests.csproj --configuration Release --no-build
```

The last line should print:

```text
All ParaPhraser.Core smoke tests passed.
```

The repository shortcut for the same sequence is:

```powershell
.\scripts\build.ps1
```

To run the desktop app in Debug configuration:

```powershell
dotnet run --project .\src\ParaPhraser.Desktop\ParaPhraser.Desktop.csproj
```

There is no console window because the output type is `WinExe`. Successful startup is indicated by the ParaPhraser tray icon and its Ollama notification.

## 5. Exact startup lifecycle

`App.OnStartup` in `src/ParaPhraser.Desktop/App.xaml.cs` is the composition root. In order, it does the following:

1. Creates the named mutex `Local\ParaPhraser.Desktop`.
2. If the mutex already exists, calls `Shutdown()` immediately. This is the single-instance guard.
3. Creates `OverlayWindow`.
4. Creates `ClipboardSelectionService`.
5. reads `PARAPHRASER_OLLAMA_MODEL`; if blank, `OllamaTextTransformer` uses `gemma3:4b`.
6. Creates `RewriteCoordinator` and connects its local-AI-unavailable event.
7. Creates `GlobalHotkeyService`.
8. Registers hotkey ID 1 as `Ctrl+Alt+G` and ID 2 as `Ctrl+Alt+R`.
9. Creates the WinForms tray icon and menu.
10. Starts `NotifyOllamaStatusAsync` without blocking startup.
11. Registers a WPF dispatcher exception handler that displays unhandled UI errors in the overlay.

At exit, `App.OnExit` unsubscribes events, unregisters hotkeys, disposes the hidden hotkey window and tray icon, closes the overlay, and releases the mutex.

### Ollama startup readiness check

Startup performs up to five attempts. Each `GET /api/tags` attempt has a three-second cancellation timeout, with a two-second delay between failed attempts. The total worst-case wait is roughly 23 seconds. It then shows one five-second tray balloon:

- **ParaPhraser is ready** when the endpoint works and the exact configured model is listed.
- **Local AI isn't ready** when Ollama is down, slow, or the model is absent.

This readiness check is advisory. The tray application remains running even when Ollama is unavailable. Generate tries the API again and shows both an overlay error and warning balloon on a connection/model failure.

## 6. Global hotkey path

`GlobalHotkeyService` creates a message-only Win32 window using WPF's `HwndSource`. It registers each shortcut with `RegisterHotKey` and automatically adds the `MOD_NOREPEAT` flag. Holding the shortcut therefore does not create repeated operations.

Windows sends `WM_HOTKEY` (`0x0312`) to `WindowProcedure`. The service raises `HotkeyPressed` with the integer ID. `App.OnHotkeyPressed` maps ID 1 to Grammar and ID 2 to Rewrite, then calls `RewriteCoordinator.HandleAsync`.

Registration failure throws `Win32Exception` with a message that the shortcut may already be used. Because registration occurs during startup, a debugger should be set to break on thrown `Win32Exception` when diagnosing a missing tray icon or an instant exit.

To change a hotkey, update all three places that describe it:

1. The registration call and/or IDs in `App.xaml.cs`.
2. `VirtualKey.cs` if a new key is not defined.
3. The disabled shortcut labels in `ConfigureTrayIcon` and the README/this guide.

## 7. Selection capture, step by step

`RewriteCoordinator.HandleAsync` first attempts `SemaphoreSlim.WaitAsync(0)`. If another capture, generation, or replacement is active, the second hotkey is ignored. This prevents overlapping clipboard operations.

`ClipboardSelectionService.CaptureAsync` then:

1. Waits for Ctrl/Alt/Shift from the global shortcut to be released. It checks up to 40 times at 15 ms intervals.
2. Calls `GetForegroundWindow`. This HWND is the future replacement target.
3. Gets the target process name and window title. These are prompt context, not surrounding document text.
4. Captures every available clipboard format with `ClipboardSnapshot`.
5. Records `GetClipboardSequenceNumber`.
6. Uses `SendInput` to send Ctrl+C.
7. Polls the sequence number up to 20 times at 20 ms intervals, for about 400 ms maximum.
8. Reads Unicode text. Blank selections and non-text selections are rejected.
9. Returns `SelectionContext(Text, TargetWindowHandle, ApplicationName, WindowTitle)`.
10. Restores the original clipboard in a `finally` block, including on failure.

Every clipboard read/write runs through `ClipboardRetry`. For `CLIPBRD_E_CANT_OPEN`, it retries with increasing asynchronous delays of 25, 50, 100, 200, 350, 500, 750, and 1000 ms—roughly three seconds total. The awaits deliberately resume on the original STA dispatcher thread because WPF clipboard APIs require it. Clipboard contention is common on Windows because only one process can open the clipboard at a time.

The overlay's “Selected context” editor changes what is sent to the model. It does **not** change the original selection target or selection range. Replace still pastes into the field/window captured at shortcut time.

### What “context” currently means

The model receives:

- The editable selected text.
- The selected action and tone. English is the fixed output language.
- The editable one-off instruction.
- The original process name, for example `notepad` or `ms-teams`.
- The original window/document title when Windows exposes it.

`RewriteContext` has a `SurroundingText` property, but `RewriteCoordinator` currently does not populate it. ParaPhraser does not read the whole page, chat, email thread, or document. Adding UI Automation or application integrations would be required for true surrounding context.

## 8. Overlay UI and its state

`OverlayWindow.xaml` is the entire visible product UI. `App.xaml` owns the global dark theme, brushes, buttons, and the custom ComboBox popup/template.

`OverlayWindow.ShowComposer` resets the screen for every fresh shortcut:

- Copies the captured selection into editable `OriginalText`.
- Copies the default instruction into editable `InstructionText`.
- Selects the action associated with the shortcut.
- Resets tone to Original. Output language is fixed to English and is not resettable.
- Hides any old result and disables Copy/Replace.
- Positions the window 12 device-independent pixels right and 16 down from the cursor, then clamps it inside the virtual desktop.
- Focuses the instruction field.

Generate validates that selected context and instruction are not blank. `Ctrl+Enter` is the keyboard equivalent of Generate. Escape hides the window. The close button hides it too; WPF close is intercepted so the resident process keeps running. Only `CloseForShutdown` permits a real close.

### Fragile but important dropdown index mappings

The XAML item order and code-behind `SelectedIndex` switches form a contract:

| Dropdown index | Action | Tone |
|---:|---|---|
| 0 | Fix grammar | Original |
| 1 | Rewrite | Polite |
| 2 | Shorten | Professional |
| 3 | — | Casual |
| 4 | — | Emojified |

Action mapping lives in `GetSelectedMode`; tone mapping lives inside `GenerateAsync`. If you reorder or insert `ComboBoxItem` elements in XAML, update those switches and add a smoke/UI test. Otherwise the label may say one thing while the model receives another enum. The language control is now a non-interactive **English output · always** badge.

Changing Action updates the default instruction only when the instruction box is blank or still equals the previous default. A manually edited instruction is preserved.

## 9. Coordinator and request creation

`RewriteCoordinator` is deliberately the only class that knows the complete operation. Its two main phases are:

### Handle phase

`HandleAsync` captures `SelectionContext` and gives the overlay a callback. If capture returns null, it displays “Select some editable text, then press the shortcut again.” The callback closes over the original context so later replacement knows which HWND to reactivate.

### Generate phase

`GenerateAsync` creates:

```csharp
new RewriteRequest(
    selectedText,
    mode,
    instruction,
    new RewriteContext(context.ApplicationName, context.WindowTitle),
    tone)
```

It calls `ITextTransformer.TransformAsync`. This interface is the provider boundary: replacing Ollama with another local engine should require a new transformer implementation, not changes to clipboard/UI code.

`LocalAiUnavailableException` is treated specially: its text appears in the overlay and fires `LocalAiUnavailable`, which produces a warning balloon. Other exceptions appear only in the overlay.

## 10. Ollama request contract

`OllamaTextTransformer` owns all local-model behavior.

### Endpoint and timeout

- Base URL: `http://127.0.0.1:11434/`
- Readiness: `GET api/tags`
- Generation: `POST api/chat`
- HTTP timeout: two minutes
- Default model: `gemma3:4b`
- Streaming: disabled
- Main temperature: `0.2`
- Context window requested: `8192` tokens

The main JSON shape is equivalent to:

```json
{
  "model": "gemma3:4b",
  "stream": false,
  "messages": [
    { "role": "system", "content": "multilingual writing policy..." },
    { "role": "user", "content": "structured prompt..." }
  ],
  "options": {
    "temperature": 0.2,
    "num_ctx": 8192
  }
}
```

The user prompt is assembled with these headings:

```text
INSTRUCTION
<editable one-off instruction>

TONE
<tone-specific rule>

REQUIRED OUTPUT LANGUAGE
<detected or explicitly selected language rule>

SOURCE CONTEXT
Application: <process name>
Window or document: <window title>
Surrounding text: <only if supplied; currently absent>

SELECTED TEXT TO REPLACE
<editable selected text>
```

The system prompt tells the model to return English replacement text only, translate every non-English part, and preserve names, dates, facts, links, formatting intent, and commitments. The English-only rule also overrides a conflicting editable instruction such as “answer in Hindi.” This is probabilistic behavior, not a mathematical guarantee; the result is intentionally shown for review before replacement.

### Tone behavior

- Original preserves the selected text's tone.
- Polite requests considerate and courteous wording without excessive formality.
- Professional requests polished, confident, workplace-appropriate wording.
- Casual requests relaxed, friendly, conversational wording.
- Emojified requests a small number of relevant emojis while preserving facts.

Mode and tone are independent. “Fix grammar + Professional” corrects grammar and changes tone; “Rewrite + Original” rewrites while trying to preserve tone.

## 11. English-only output from multilingual input

English is now a non-negotiable application rule rather than a dropdown preference. English input is corrected or rewritten in English. Hindi, Hinglish, and other non-English input is translated into natural English while applying the chosen action, tone, and instruction.

| Input/selection | Source classification | Required model output |
|---|---|---|
| English | English | English |
| Any Devanagari text | Hindi | Natural English translation/rewrite |
| Latin text with at least 2 known Hinglish tokens | Hinglish | Natural English translation/rewrite |
| Another non-English script | Other/unknown to the lightweight resolver | Natural English translation/rewrite |

`WritingLanguageResolver` remains a small deterministic source/draft heuristic, not a full language identifier:

1. Any character from Unicode U+0900–U+097F classifies the source as Hindi.
2. Otherwise it extracts `[A-Za-z]+` tokens and counts words in `HinglishWords`.
3. Two or more known words classify it as Hinglish; otherwise it classifies it as English.

The classification tailors the prompt and detects common bad drafts; it no longer selects the output language. The main system instruction separately says that any language must become English, so unsupported source classifications still receive the English-only rule.

To improve Romanized Hindi detection, update the `HinglishWords` set in `WritingLanguageResolver.cs` and add positive and negative assertions to the smoke tests. Avoid common English words because false positives could reject a valid English draft.

### English correction pass

After the main response, `CorrectLanguageIfNeededAsync` verifies that the draft:

- Is classified as English rather than known Hindi/Hinglish.
- Contains no letter from a non-Latin script. Latin accented letters are allowed so names are not unnecessarily rejected.
- May still contain punctuation, numbers, URLs, symbols, and emojis.

If validation fails, a second Ollama request uses an English-only system prompt, temperature `0.1`, and the original text, user instruction, and incorrect draft. That system prompt explicitly overrides conflicting language instructions. If the corrected response is still non-English, the app shows an error instead of presenting it. One click can therefore take twice as long when the first draft violates the English-only contract.

## 12. Replace mechanics and why they fail

Replace is not an editor integration. It is a carefully guarded focus-and-paste operation.

`ClipboardSelectionService.ReplaceAsync`:

1. Snapshots the user's current clipboard.
2. Confirms the captured target HWND still exists with `IsWindow`.
3. Tries up to 12 times, 50 ms apart, to return foreground focus to that HWND.
4. Puts replacement Unicode text on the clipboard.
5. Waits 60 ms and reads the clipboard back to verify the exact text.
6. Rechecks the foreground window and refocuses if necessary.
7. Records the new clipboard sequence number.
8. Sends Ctrl+V with `SendInput`.
9. Waits 750 ms for the target application to process the paste.
10. Restores the previous clipboard only if no other application changed it during the paste. Restoration uses the full busy-clipboard retry policy.

The sequence-number guard is essential. Without it, ParaPhraser could overwrite something the user copied while a slow target was pasting. Restoration is intentionally best-effort: if Ctrl+V already succeeded but Teams continues holding the clipboard after all retries, ParaPhraser leaves the replacement text on the clipboard and keeps the overlay closed. A secondary restore failure must not make a successful replacement look unsuccessful.

Common limitations:

- Replacement is plain Unicode text, so rich formatting can be lost.
- The original selection can disappear when another window gains focus. Many editors preserve it, some do not.
- Windows User Interface Privilege Isolation blocks a normal process from sending input to an administrator-elevated target. Run both at the same privilege level; do not routinely run ParaPhraser as administrator.
- Browser editors, Teams, Outlook, Electron editors, remote desktops, and custom controls have different focus/paste behavior. Always establish a baseline in Notepad.
- If generation takes a long time and the target document/window closes, its HWND is no longer valid; Copy remains the safe fallback.

The first tuning constants for a slow editor are `FocusAttempts`, `FocusAttemptDelayMilliseconds`, `ClipboardReadyDelayMilliseconds`, and `PasteProcessingDelayMilliseconds` in `ClipboardSelectionService.cs`. Increase them conservatively; every increase affects perceived responsiveness.

## 13. Clipboard safety rules

Do not simplify clipboard code without preserving these invariants:

- Capture and restore happen inside `try/finally`.
- All clipboard work runs on the WPF UI thread, which is STA.
- Unicode text is used explicitly.
- Every clipboard read and write retries `CLIPBRD_E_CANT_OPEN` asynchronously for roughly three seconds.
- Replace verifies the value after setting it.
- Replace restores the prior clipboard only when the sequence number proves no third party changed it.
- A busy-clipboard failure while restoring after a successful paste is non-fatal and must not reopen the overlay.

When fixing WPF/WinForms ambiguity, the intended clipboard types are:

```csharp
using Clipboard = System.Windows.Clipboard;
using TextDataFormat = System.Windows.TextDataFormat;
using IDataObject = System.Windows.IDataObject;
```

The tray types remain `System.Windows.Forms.NotifyIcon`, `ContextMenuStrip`, `ToolStripMenuItem`, and `ToolTipIcon`.

## 14. Ollama operations without the application

If `ollama` is not found after installation, open a new terminal or call the installed executable directly:

```powershell
& "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe" --version
& "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe" list
& "$env:LOCALAPPDATA\Programs\Ollama\ollama.exe" ps
```

Install or repair the model:

```powershell
ollama pull gemma3:4b
```

Check the server and port:

```powershell
Test-NetConnection 127.0.0.1 -Port 11434
Invoke-RestMethod http://127.0.0.1:11434/api/tags | ConvertTo-Json -Depth 6
```

Make a direct model request, bypassing ParaPhraser:

```powershell
$body = @{
    model = 'gemma3:4b'
    stream = $false
    messages = @(
        @{ role = 'user'; content = 'Rewrite professionally: kal report bhej dunga' }
    )
} | ConvertTo-Json -Depth 6

Invoke-RestMethod `
    -Uri http://127.0.0.1:11434/api/chat `
    -Method Post `
    -ContentType 'application/json' `
    -Body $body
```

Interpretation:

- Port check fails: Ollama process/server problem.
- `/api/tags` works but `gemma3:4b` is absent: model-installation or model-name problem.
- Direct chat fails or is slow: Ollama/model/GPU problem, not ParaPhraser UI.
- Direct chat works but Generate fails: inspect request construction, deserialization, or app exception handling.

On a machine with NVIDIA graphics, `nvidia-smi` shows GPU memory/use. The first request can be slow while Ollama loads the model. `ollama ps` shows whether the model is loaded and whether it is using GPU or CPU.

### Choosing another installed model

For one PowerShell process:

```powershell
$env:PARAPHRASER_OLLAMA_MODEL = 'another-model:tag'
dotnet run --project .\src\ParaPhraser.Desktop\ParaPhraser.Desktop.csproj
```

For the current Windows user:

```powershell
[Environment]::SetEnvironmentVariable(
    'PARAPHRASER_OLLAMA_MODEL',
    'another-model:tag',
    'User')
```

Sign out/in or restart the application after changing a user environment variable. To return to the built-in default, set the user value to `$null`. A model change affects readiness checks and generation; the exact name must appear in `/api/tags`.

## 15. Tests and what they prove

The smoke-test project is a console executable with a local `Require` helper. It currently verifies:

- Deterministic demo grammar, rewrite, and professional transformations.
- Hindi detection from Devanagari.
- Hinglish detection from known Latin tokens.
- English detection.

Normal test runs use `DemoTextTransformer`; they prove domain wiring but do not prove Ollama is installed or that the model follows prompts.

The opt-in local-AI test makes real Ollama calls:

```powershell
$env:PARAPHRASER_RUN_LOCAL_AI_TESTS = '1'
dotnet run --project .\tests\ParaPhraser.Core.SmokeTests\ParaPhraser.Core.SmokeTests.csproj
Remove-Item Env:PARAPHRASER_RUN_LOCAL_AI_TESTS
```

It checks that both Hindi and Hinglish become English. It also passes an English source with the conflicting instruction “Translate this into Hindi” and verifies that the result remains English, proving that the application-level output rule wins. Because model output is probabilistic, treat repeated failures as either a prompt/model regression or an inadequate validation heuristic, and inspect the printed text before changing assertions.

There are currently no automated tests for global hotkeys, WPF controls, clipboard restoration, focus recovery, tray startup, or replacement across third-party editors. Those require integration/UI testing or a manual compatibility matrix.

## 16. Visual Studio debugging playbook

Use Visual Studio 2026, set `ParaPhraser.Desktop` as the startup project, and press F5. Before doing so, exit any already-running tray instance. Otherwise the new process encounters the named mutex and exits immediately.

Useful breakpoint sequence:

| Stage | Method to break in | Inspect |
|---|---|---|
| Process start | `App.OnStartup` | `isFirstInstance`, `configuredModel` |
| Hotkey registration | `GlobalHotkeyService.Register` | ID, modifiers, return value/error |
| Hotkey received | `GlobalHotkeyService.WindowProcedure` | `message`, `wParam` |
| Mode mapping | `App.OnHotkeyPressed` | `hotkeyId`, `mode` |
| Capture begins | `ClipboardSelectionService.CaptureAsync` | `targetWindow`, sequence number |
| Capture result | `RewriteCoordinator.HandleAsync` | `context.Text`, app name/title |
| UI choices | `OverlayWindow.GenerateAsync` | edited text, instruction, indices/enums |
| Request creation | `RewriteCoordinator.GenerateAsync` | full `RewriteRequest` |
| Ollama call | `OllamaTextTransformer.TransformAsync` | `_model`, payload, response status |
| Language validation | `CorrectLanguageIfNeededAsync` | detected language, draft, corrected text |
| Replace click | `OverlayWindow.ReplaceAsync` | `_replaceAsync` and exception |
| Focus/paste | `ClipboardSelectionService.ReplaceAsync` | target HWND, foreground HWND, sequences |

In **Debug > Windows > Exception Settings**, enable break-on-thrown temporarily for:

- `System.ComponentModel.Win32Exception`
- `System.Runtime.InteropServices.COMException`
- `ParaPhraser.Core.LocalAiUnavailableException`
- `System.InvalidOperationException`

The application catches several of these to show friendly UI. Breaking only on unhandled exceptions can therefore miss the original failure location.

For temporary diagnostics, use `System.Diagnostics.Debug.WriteLine` and read Visual Studio's **Output > Debug** pane. Never log selected text by default. Useful safe values are stage names, HWND values, elapsed times, HTTP status codes, model name, language enum, and clipboard sequence numbers.

## 17. Symptom-to-cause troubleshooting

### “The SDK 'Microsoft.NET.Sdk' specified could not be found”

Run:

```powershell
where.exe dotnet
dotnet --list-sdks
dotnet --info
& 'C:\Program Files\dotnet\dotnet.exe' --list-sdks
```

Likely causes:

- PATH resolves to `C:\Program Files (x86)\dotnet\dotnet.exe`, which has no matching SDK. Put `C:\Program Files\dotnet` first or use the x64 executable directly.
- .NET 10 SDK is missing. Install it; a .NET runtime alone is insufficient.
- Visual Studio 2022 is trying to load a .NET 10 project. Use Visual Studio 2026 or the CLI.
- A Visual Studio 2026 installation/workload is incomplete. Repair/install **.NET desktop development**.
- `global.json` requests an SDK family not installed. Do not change it blindly; first inspect installed SDKs and understand roll-forward.
- If a managed coding sandbox alone reports access denied for `C:\Users\<you>\AppData\Local\Microsoft SDKs`, but the same command succeeds in your normal x64 PowerShell, that is a sandbox permission boundary rather than a broken project or missing SDK. Use the normal terminal for the build or grant the narrowly requested build permission.

### Build reports `MSB3021`, `MSB3026`, or `MSB3027` and a locked DLL/exe

The running tray app is holding its build output. Right-click its tray icon and choose Exit, or inspect the exact process:

```powershell
Get-Process ParaPhraser -ErrorAction SilentlyContinue
```

If necessary, stop only the confirmed process ID:

```powershell
Stop-Process -Id <confirmed-pid>
```

Then rebuild. Do not terminate broad groups of `dotnet` processes; they may belong to other work.

### Compiler says `IDataObject` or `KeyEventArgs` is ambiguous

This is expected when both WPF and WinForms are enabled. Qualify the intended WPF type, for example:

```csharp
using IDataObject = System.Windows.IDataObject;
using Clipboard = System.Windows.Clipboard;
using Point = System.Windows.Point;

private void Handler(object sender, System.Windows.Input.KeyEventArgs e)
```

Do the same for `TextDataFormat` or `SelectionChangedEventArgs` if needed.

### Warning/error WFO0003 about high-DPI settings

The project places DPI configuration in the project file with `ApplicationHighDpiMode=PerMonitorV2`. Do not re-add legacy DPI elements to `app.manifest`; with warnings-as-errors, the Windows Forms analyzer can fail the build.

### App starts but there is no window

That is normal: ParaPhraser has no main window. Check the notification area and its hidden-icons menu. If there is no tray icon, debug `App.OnStartup`, hotkey registration, and `ConfigureTrayIcon`.

### App exits immediately under the debugger

An older ParaPhraser process probably owns `Local\ParaPhraser.Desktop`. Exit the tray instance and retry. If none exists, break in `OnStartup` and inspect `isFirstInstance` and startup exceptions.

### Tray icon exists but shortcuts do nothing

1. Confirm shortcuts are `Ctrl+Alt+G` and `Ctrl+Alt+R`.
2. Try Notepad with ordinary selected text.
3. Break in `WindowProcedure`; if not hit, registration/window-message delivery is the problem.
4. If hit, step through `OnHotkeyPressed` and `HandleAsync`.
5. Confirm a second operation is not holding `_singleOperation`; rapid repeated shortcuts are intentionally ignored.

### Overlay says to select editable text even though text looks selected

The Ctrl+C capture did not change the clipboard within about 400 ms, or the target did not publish Unicode text. Test Ctrl+C manually in that control. Try Notepad. A slow target may require a larger polling window; a protected/custom control may require future UI Automation support.

### Ollama notification says it is not ready

Run the port, `/api/tags`, `ollama list`, and direct chat checks in section 14. Match the configured model name exactly. Starting the Ollama desktop app/server and running `ollama pull gemma3:4b` resolves the common cases.

### Generate waits a long time

Check `ollama ps` and GPU use. The first call can load the model. Very long editable context consumes the 8192-token window and more inference time. A failed language validation can cause a second request. The application timeout is two minutes; shorten context before increasing it.

### Hindi or Hinglish is not fully translated into English

There is no language dropdown to fix this manually because English is already mandatory. Put a breakpoint in `CorrectLanguageIfNeededAsync`, inspect `IsEnglishOutput`, and confirm the strict second request runs. For Romanized Hindi that the small dictionary misses, add genuinely diagnostic tokens to `HinglishWords` plus positive and negative tests. If the model repeatedly fails the strict pass, try a shorter selection or a stronger local model before weakening validation.

### A dropdown label produces the wrong behavior

Compare the Action/Tone XAML item order against the index table in section 8 and the switches in `OverlayWindow.GenerateAsync`/`GetSelectedMode`. This is almost always an order-mapping mismatch. Output language has no index mapping.

### Replace does nothing, pastes elsewhere, or loses the selection

1. Verify Copy works. If not, the result stage is the problem rather than replacement.
2. Test Replace in Notepad.
3. Break in `ClipboardSelectionService.ReplaceAsync` and compare `context.TargetWindowHandle` with `GetForegroundWindow()`.
4. Confirm the original window still exists and is not elevated above ParaPhraser.
5. Increase focus or paste timing only if the debugger proves a timing failure.
6. Use Copy as the safe fallback for incompatible rich editors.

### Overlay reappears after Replace with `CLIPBRD_E_CANT_OPEN`

This means another process retained exclusive clipboard access. Current code routes every clipboard operation through `ClipboardRetry`. A failure before Ctrl+V shows the friendly “Another application is temporarily using the clipboard” message because replacement did not happen. A failure only while restoring the old clipboard after Ctrl+V is swallowed after the full retry window, leaves the generated text on the clipboard, and does not reopen the overlay. If the raw COM error reappears, confirm the running tray process was rebuilt/restarted and place breakpoints in `ClipboardRetry.ExecuteAsync`, `ClipboardSnapshot.TryRestoreAsync`, and `OverlayWindow.ReplaceAsync`.

### The user's old clipboard content disappears

Break in `ClipboardSnapshot.CaptureAsync/TryRestoreAsync` and record sequence numbers, without logging contents. Confirm all capture/paste code still has `finally` restoration and the third-party-change guard. If restoration exhausts its roughly three-second retry window, the generated replacement intentionally remains on the clipboard rather than reopening the overlay. Clipboard managers can introduce extra sequence changes; reproduce once with them disabled.

### Startup toggle is checked but nothing launches after sign-in

Inspect the exact per-user Run entry:

```powershell
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Get-ItemProperty -Path $runKey -Name ParaPhraser
```

The value should be a quoted absolute path to an existing `ParaPhraser.exe`. Startup registration records `Environment.ProcessPath` of the executable that toggled it. If the repository, Debug output, or published folder moved, the old path is stale. Run the executable you intend to keep, toggle startup off and on, then recheck the value.

### No tray balloon appears

Windows notification settings or Focus Assist/Do Not Disturb can suppress `NotifyIcon` balloons. The readiness state is still available by testing Ollama directly. Break in `ShowNotification` to prove the app requested it.

### XAML fails during `InitializeComponent`

Read the inner exception. Typical causes are an invalid resource key, a binding to a missing named control, a malformed `ControlTemplate`, or an event handler renamed only in XAML/code-behind. Search the XAML for the named resource/handler and keep `x:Name` contracts synchronized.

## 18. Run at Windows startup

`StartupRegistrationService` uses the current user's registry only:

```text
Key:   HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run
Value: ParaPhraser
Data:  "C:\absolute\path\to\ParaPhraser.exe"
```

It does not create a Windows service, scheduled task, or all-users entry, and it needs no administrator permission. The application launches when that user signs in.

Prefer toggling **Run ParaPhraser at startup** from the tray menu. To remove a stale entry manually:

```powershell
Remove-ItemProperty `
    -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
    -Name ParaPhraser `
    -ErrorAction SilentlyContinue
```

Important: a `dotnet run` Debug executable lives under `bin\Debug\net10.0-windows`. Cleaning/moving the repository can invalidate that path. A stable published directory is a better startup target.

## 19. Publishing and moving the app

Create a self-contained x64 single-file build:

```powershell
.\scripts\publish-win-x64.ps1
```

Output goes to:

```text
C:\Learning\ParaPhraser\artifacts\win-x64
```

“Self-contained” means the .NET runtime is included. It does **not** include Ollama or `gemma3:4b`; those remain machine-level prerequisites.

Recommended manual deployment sequence:

1. Exit the Debug tray instance.
2. Publish.
3. Move/copy the entire published output to a stable location if desired.
4. Launch the published `ParaPhraser.exe`.
5. Confirm hotkey, Generate, Copy, and Replace in Notepad.
6. Toggle startup off and on from this published instance so the Run value points to it.
7. Sign out/in once to verify startup.

Re-publishing over a running published executable can fail because Windows locks loaded files. Exit it first.

## 20. Safely changing common features

### Add a new action

1. Add a member to `RewriteMode` if it is truly new.
2. Add/update default instructions in `RewriteCoordinator` and `OllamaTextTransformer`.
3. Add the ComboBox item in `OverlayWindow.xaml`.
4. Update both action index mappings in `OverlayWindow.xaml.cs`.
5. Decide whether it needs a global hotkey.
6. Add a smoke assertion and test with Ollama.

There is already a `Professional` mode in Core, but the unified screen normally represents professionalism as a Tone. Avoid creating two UI choices with unclear overlap unless the product behavior is intentionally different.

### Add a new tone

1. Add it to `RewriteTone`.
2. Add the UI item.
3. Map its index in `OverlayWindow.GenerateAsync`.
4. Add its prompt rule in `GetToneInstruction`.
5. Test facts, names, URLs, and dates for preservation.

### Change visual design or dropdown appearance

Start in `App.xaml`, not individual controls. The relevant shared resources are `ModernComboBoxStyle`, `ModernComboBoxItemStyle`, `OverlayButtonStyle`, `PrimaryButtonStyle`, brushes, and gradients. `OverlayWindow.xaml` should mainly describe layout. After edits, test normal, hover, keyboard focus, disabled state, selection, popup scrolling, 100%/150%/200% scaling, and a secondary monitor.

### Add true surrounding context

Do not silently scrape arbitrary windows. A robust design needs:

1. A bounded and visible context source, preferably Windows UI Automation around the focused control.
2. Permission/privacy rules and sensitive-field exclusions.
3. A size/token limit and truncation strategy.
4. Population of `RewriteContext.SurroundingText`.
5. A UI indication of exactly what will be sent.
6. Compatibility tests for each target application.

The prompt builder already supports a non-empty `SurroundingText`, so provider code needs little change.

### Replace Ollama with another local provider

Implement `ITextTransformer`. Keep endpoint/prompt/deserialization/provider errors inside that implementation. Construct it in `App.OnStartup`. Preserve `LocalAiUnavailableException` semantics if you want existing overlay/tray warnings to work.

## 21. Privacy, security, and trust boundaries

Current privacy properties:

- Requests go to loopback `127.0.0.1`, not a configured cloud URL.
- The application has no database or history store.
- It captures only after an explicit hotkey.
- It sends selected/edited text, process name, window title, instruction, mode, and tone. Output language is always English.
- It restores clipboard contents where safe.
- It runs `asInvoker`, without requested elevation.

Limitations and responsibilities:

- Ollama/model files and behavior are separate software; inspect their versions and licenses.
- Window titles can contain private document or conversation names.
- Selected text may contain secrets. Local processing reduces exposure but does not make indiscriminate handling safe.
- A local process with access to port 11434 can call Ollama; the app implements no authentication because it binds to the normal local endpoint.
- Model output can be factually or stylistically wrong. The review screen is a safety boundary; do not auto-replace without review.
- Do not add selected-text logging to routine diagnostics.

## 22. Known architectural limitations

- Windows only.
- Fixed global shortcuts.
- Clipboard/SendInput compatibility rather than native integrations.
- Plain-text replacement only.
- No persistent history by design at this stage.
- No settings window; model selection is an environment variable.
- Context is selection + app/window metadata, not surrounding content.
- No cancellation button for a generation already sent to Ollama.
- No structured logging or diagnostics panel.
- No automated UI/clipboard compatibility tests.
- Hinglish draft detection is a small heuristic and does not cover every transliteration spelling; the model prompt is the primary translator.
- The fixed `8192` context request may not be optimal for every model/hardware combination.

These are boundaries, not unexplained bugs. When planning work, decide whether the change fixes a regression inside the current boundary or deliberately expands the product.

## 23. Recovery checklist: use this tomorrow morning

If everything appears broken, work in this order:

1. Exit any ParaPhraser tray instance.
2. Open x64 PowerShell in `C:\Learning\ParaPhraser`.
3. Run `dotnet --info` and confirm a .NET 10 SDK from `C:\Program Files\dotnet`.
4. Run `.\scripts\build.ps1`; do not proceed until it passes.
5. Run `ollama list` and confirm `gemma3:4b`.
6. Run `Test-NetConnection 127.0.0.1 -Port 11434`.
7. Run the direct `/api/chat` request from section 14.
8. Start ParaPhraser with `dotnet run --project .\src\ParaPhraser.Desktop\ParaPhraser.Desktop.csproj`.
9. Confirm tray icon and readiness balloon.
10. Test `Ctrl+Alt+G` on selected plain text in Notepad.
11. Test Generate with English, Hindi, and Hinglish input; verify all three results are English.
12. Test Copy before Replace.
13. If a stage fails, attach the debugger at the corresponding breakpoint in section 16.
14. Only after Notepad works, test Teams/browser/Outlook and treat differences as editor compatibility problems.
15. After moving or republishing the executable, re-register startup from the final executable.

## 24. Definition of a healthy release

A release is healthy when all of the following are true:

- `scripts\build.ps1` completes with zero warnings and errors.
- The opt-in Ollama test passes on the supported model.
- Only one tray instance can run.
- Both shortcuts open the correct initial action.
- The original clipboard survives capture.
- Edited selected context is what the model receives.
- Every dropdown label maps to the intended enum.
- English stays English, while Devanagari Hindi, Hinglish, and other non-English input becomes English.
- Copy returns exact result text.
- Replace works in Notepad and fails safely with a useful message when focus cannot be restored.
- Ollama-down and model-missing paths show an overlay message and notification.
- Startup registration points to an existing stable executable and survives sign-out/sign-in.
- No selected text or history is written to disk.

When investigating a bug, record the input application, exact selected text category (not secret content), action, tone, source language, model name, Ollama status, whether Copy worked, whether Notepad reproduced it, and the first method/exception where behavior diverged. That small record usually identifies the owning layer immediately.
