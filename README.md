<img src="src/ParaPhraser.Desktop/Assets/logo.svg" alt="" width="72">

# ParaPhraser

ParaPhraser is a Windows-resident writing assistant. It captures selected text
from the current application, runs a requested transformation, and displays a
small confirmation overlay near the cursor. It has no traditional main window.

## MVP status

This starter implements the complete Windows interaction loop:

- System tray process with single-instance protection.
- `Ctrl+Alt+G` for grammar correction.
- `Ctrl+Alt+R` for rewriting.
- `Ctrl+Alt+F` for formatting without changing a word.
- `Ctrl+Alt+E` for direct replace: rewrites and pastes straight away with an Undo
  button. If a detail looks missing, it opens the suggestion for review instead.
- Every shortcut can be changed in Settings.
- **Show changes** compares a suggestion with the original, and a warning names
  numbers, dates, names, and links that may have been dropped.
- Saved instructions, such as "Client email", in the overlay's Saved list.
- When an online model is busy, one click retries with a lighter model.
- Clipboard-preserving selection capture.
- Minimal result overlay with Copy, Replace, Enter, and Escape actions.
- Results appear as they are written. **Stop** or closing the overlay ends the
  request. A stopped result stays visible but cannot replace the original.
- One unified writing-tools screen with editable context, Fix grammar, Rewrite,
  and Shorten actions, plus Original, Polite, Professional, Casual, and
  Emojified tone choices.
- Multilingual input understanding with one predictable output language:
  English input stays English, while Hindi, Hinglish, and other non-English
  input is translated into natural English.
- Focus restoration and in-place paste.
- Rich text support: bold, italic, underline, text color, highlight, links,
  headings, and bullet or numbered lists survive capture, the model, and paste
  in apps that copy HTML. The target app decides which of these it supports.
  The suggestion is editable, with Ctrl+B, Ctrl+I, Ctrl+U, and a format toolbar
  that includes a color and highlight picker.
- Format mode: adds bold, italic, underline, red text, and highlight where they
  help, and never changes a word. Rewrite, Shorten, and Fix grammar change the
  words and keep existing formatting, but add none of their own.
- Provider-independent `ITextTransformer` abstraction.

ParaPhraser does not store conversation history. Choose the gear in the overlay,
or right-click the tray icon and choose **Settings**, to pick how it writes:

- **Offline** uses Ollama on this computer. Selected text, the application name,
  the window title, and the instruction stay on this computer. Settings links to
  the Ollama installer and can download the model for you.
- **Online** uses your own API key with Anthropic Claude, OpenAI, Google Gemini,
  or OpenRouter. Selected text and the instruction go to that provider, whose
  terms and pricing apply. Application names and window titles are not sent.
  Keys are encrypted for your Windows account with DPAPI and stored in
  `%APPDATA%\ParaPhraser\settings.json`.

When installed with Setup.exe, ParaPhraser asks GitHub for a new version each time it
starts, even in offline mode. That request contains none of your text.

To investigate a failure, right-click the tray icon and choose
**Open logs folder**. The log at `%LOCALAPPDATA%\ParaPhraser\logs` records each
request's mode, provider, model, application, character counts, timings, retries,
and errors. It stays on this computer and never contains selected text, model
replies, instructions, window titles, or API keys.

## Prerequisites

- Windows 10 or Windows 11.
- Visual Studio 2026 with the **.NET desktop development** workload, or the
  .NET 10 SDK.
- For offline mode, [Ollama](https://ollama.com/). For online mode, an API key
  from a supported provider.

`PARAPHRASER_OLLAMA_MODEL` still sets the Ollama model when none is saved in
Settings.

## Build and run

From PowerShell:

```powershell
cd ParaPhraser
.\scripts\build.ps1
dotnet run --project .\src\ParaPhraser.Desktop\ParaPhraser.Desktop.csproj
```

Then:

1. Open Notepad, Teams, Outlook, or a browser text field.
2. Type and select some text.
3. Press `Ctrl+Alt+G`, `Ctrl+Alt+R`, or `Ctrl+Alt+F`.
4. Choose Fix grammar, Rewrite, or Shorten on the same screen.
5. Edit the one-off instruction and select **Generate** or press `Ctrl+Enter`.
6. Optionally choose a tone: Original, Polite, Professional, Casual, or Emojified.
   Output is always English, regardless of the input language.
7. Review the suggestion and choose **Copy** or **Replace**.
8. Right-click the tray icon to open **Settings**, toggle
   **Run ParaPhraser at startup**, or exit.

ParaPhraser checks the chosen provider when it starts and shows a tray
notification. Click a warning to open Settings.

Start testing in Notepad. Rich editors such as Teams and Outlook will be added
to the compatibility matrix after the basic flow is confirmed.

## Publish a self-contained Windows build

```powershell
.\scripts\publish-win-x64.ps1
```

The executable is written to `artifacts\win-x64`.

To build the installer, publish a release, or build an MSIX package, see
[docs/packaging.md](docs/packaging.md).

## Known MVP limitations

- Offline mode needs Ollama running with the chosen model installed.
- Online mode has been run with a real Gemini key. Claude, OpenAI, and OpenRouter
  were tested only against fake servers and with an invalid key.
- Popular online models can be overloaded. ParaPhraser retries brief server
  errors and stops waiting after 30 seconds. If that happens, pick another model
  in Settings. Flash-lite models are often less busy.
- Context is currently limited to the selection, application name, and window
  title. Surrounding text through UI Automation is a later compatibility slice.
- Rich replacement uses one font for the whole result. Tables and images are
  not kept, nested lists are flattened, and apps that only copy RTF stay plain
  text.
- The local model can drop or change formatting, so check the suggestion before
  replacing it. Colors return automatically only on words that survive the
  rewrite unchanged. Use the picker for the rest.
- Black, white, and near-grey text or highlights count as the app's default, so
  deliberate grey is not kept.
- ParaPhraser refuses password fields and text an app marks as sensitive. An app
  that does not report its password fields to Windows could still be captured.
- Selected text that reads like a command can still influence the model. Obvious
  takeovers are refused, but always read the suggestion before replacing.
- Windows blocks simulated input into applications running at a higher
  integrity level, such as an administrator-elevated editor.
- Clipboard capture is a compatibility fallback. UI Automation capture will be
  added where supported.

For a complete source walkthrough, runtime sequence, debugger breakpoint map,
Ollama API details, and symptom-by-symptom recovery instructions, open the
[interactive HTML handbook](docs/maintenance-and-debugging-handbook.html).
Its editable source is the
[Markdown handbook](docs/maintenance-and-debugging-handbook.md).
See [docs/architecture.md](docs/architecture.md) for the shorter component
overview and next development slices.
