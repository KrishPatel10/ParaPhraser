# ParaPhraser architecture

## Current vertical slice

1. `GlobalHotkeyService` registers system-wide shortcuts through `RegisterHotKey`.
2. `ClipboardSelectionService` waits for the shortcut modifiers to be released,
   preserves the clipboard, sends `Ctrl+C`, and reads the selected text.
3. `OverlayWindow` shows a one-off contextual instruction composer beside the cursor.
4. `RewriteCoordinator` sends the selection to the provider chosen in Settings.
5. The overlay shows the suggestion without saving conversation history.
6. When the user confirms, `ClipboardSelectionService` restores the original app,
   pastes the replacement, and restores the previous clipboard contents.

## Providers

`ChatTextTransformer` holds every prompt, so all modes behave the same on every
provider. It talks to an `IChatBackend`:

- `OllamaBackend` for offline mode.
- `AnthropicBackend` for Claude, using the official SDK. Its address is pinned so
  `ANTHROPIC_BASE_URL` cannot redirect a saved key.
- `OpenAiCompatibleBackend` for OpenAI, Gemini, and OpenRouter.

`OnlineProvider.All` lists the online providers. Add one that speaks the OpenAI
format by adding a line there. Model lists come live from each provider, so no
model names are built in apart from the Ollama default. If a provider's model
list is public, set `KeyCheckPath` to an address that needs a key, or any key
will pass the check on Save.

Every backend streams. `CompleteAsync` reports all the text so far through
`onText`, and `RewriteCoordinator` passes that to the overlay at most every 80 ms,
because rebuilding the document for each token stutters. Partial text goes through
`RichDocument.FromPartialModelOutput`, which hides half-written tags and code
fences. Format mode does not stream, because its reply is a list of phrases.
Stopping cancels the request, so an online provider stops generating and billing.

Temperature is sent only to Ollama, because current online models reject it. When
`IChatBackend.IsLocal` is false, application names and window titles are left out
of the prompt, and the overlay names the provider instead of saying processing is
private.

`AppSettings` encrypts keys with DPAPI for the current Windows user. A provider's
key error text is never shown, because some providers echo part of the key.

## Rich text

`RichDocument` is the only format the pipeline shares. Clipboard HTML and model
output both pass through its parser, which keeps an allowlist of formatting and
drops everything else, so model output never reaches the clipboard unfiltered.

- Capture reads the clipboard's HTML. A selection with no formatting stays plain
  text and takes the same path as before.
- `RichFlowDocument` converts to and from the overlay's editable `RichTextBox`.
- Formatted selections reach the model as a small HTML subset (`IsHtml`).
- Text color and highlight travel as CSS on `span`. Neutral colors are dropped
  because browsers stamp a default color on every run. After the model runs,
  `MatchColorsTo` removes colors the source never had and puts lost colors back
  on words that survived unchanged.
- Format is a separate mode. The model returns only a `FormattingPlan`, a list
  of phrases, and `RichDocument.ApplyFormatting` applies them by exact match, so
  the words cannot change. Do not move this into the rewrite prompt: when asked
  to emphasize while rewriting, the model invented sentences and deadlines to
  have something to bold.
- Paste puts both HTML and plain text on the clipboard. The rewrite is placed
  inside the source's own HTML document, because Word keeps fonts in its style
  sheet rather than on each paragraph.

## Boundaries

```mermaid
flowchart TD
    Desktop["Desktop agent"] --> Capture["Selection capture"]
    Desktop --> Overlay["Micro-overlay"]
    Capture --> Core["Core rewrite contracts"]
    Core --> Provider["AI provider"]
    Overlay --> Capture
```

`ParaPhraser.Core` contains no WPF or Win32 dependencies. A cloud provider,
local model, or test transformer can therefore implement `ITextTransformer`
without changing selection capture or UI code.

## Planned next slices

- A setting for the context limit.
- Configurable shortcut settings stored per Windows user.
- UI Automation selection capture before the clipboard fallback.
- Direct replace mode with a visible undo notification.
- RTF capture for apps that do not copy HTML.
- Startup registration and installer/update integration.

## Security rules

- Never monitor or transmit keystrokes continuously.
- Capture text only after an explicit shortcut.
- Reject password and protected controls when UI Automation is introduced.
- Never ship a shared AI provider key inside the desktop binary.
- `AppLog` must never receive selected text, model responses, instructions,
  window titles, or API keys. Log counts, names, and error codes. Do not log the
  error text from a rejected key, because some providers echo part of the key.
