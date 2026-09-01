# ParaPhraser

ParaPhraser is a Windows-resident writing assistant. It captures selected text
from the current application, runs a requested transformation, and displays a
small confirmation overlay near the cursor. It has no traditional main window.

## MVP status

This starter implements the complete Windows interaction loop:

- System tray process with single-instance protection.
- `Ctrl+Alt+G` for grammar correction.
- `Ctrl+Alt+R` for rewriting.
- Clipboard-preserving selection capture.
- Minimal result overlay with Copy, Replace, Enter, and Escape actions.
- One unified writing-tools screen with editable context, Fix grammar, Rewrite,
  and Shorten actions, plus Original, Polite, Professional, Casual, and
  Emojified tone choices.
- Multilingual input understanding with one predictable output language:
  English input stays English, while Hindi, Hinglish, and other non-English
  input is translated into natural English.
- Focus restoration and in-place paste.
- Provider-independent `ITextTransformer` abstraction.

The desktop app uses Ollama on the local computer. Selected text, the active
application name, the active window title, and the one-off instruction are sent
only to the local Ollama endpoint. ParaPhraser does not store conversation
history.

## Prerequisites

- Windows 10 or Windows 11.
- Visual Studio 2026 with the **.NET desktop development** workload, or the
  .NET 10 SDK.
- [Ollama](https://ollama.com/) with the default local model installed:

```powershell
ollama pull gemma3:4b
```

Set `PARAPHRASER_OLLAMA_MODEL` before starting the app to use another installed
Ollama model.

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
3. Press `Ctrl+Alt+G` or `Ctrl+Alt+R`.
4. Choose Fix grammar, Rewrite, or Shorten on the same screen.
5. Edit the one-off instruction and select **Generate** or press `Ctrl+Enter`.
6. Optionally choose a tone: Original, Polite, Professional, Casual, or Emojified.
   Output is always English, regardless of the input language.
7. Review the suggestion and choose **Copy** or **Replace**.
8. Right-click the tray icon to toggle **Run ParaPhraser at startup** or exit.

ParaPhraser checks Ollama when it starts and shows a tray notification when the
local model is ready. It also shows a warning notification if Generate cannot
reach Ollama or the configured model is missing.

Start testing in Notepad. Rich editors such as Teams and Outlook will be added
to the compatibility matrix after the basic flow is confirmed.

## Publish a self-contained Windows build

```powershell
.\scripts\publish-win-x64.ps1
```

The executable is written to `artifacts\win-x64`. The final installer should
be created only after shortcut, startup, signing, and update behavior are stable.

## Known MVP limitations

- Ollama and the configured model must be installed and running locally.
- Context is currently limited to the selection, application name, and window
  title. Surrounding text through UI Automation is a later compatibility slice.
- Shortcuts are currently fixed.
- Replacement is plain text; rich formatting may be lost.
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
