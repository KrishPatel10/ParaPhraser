using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using ParaPhraser.Core;
using ParaPhraser.Desktop.Services;

var logFolder = Path.Combine(Path.GetTempPath(), $"paraphraser-log-{Guid.NewGuid():N}");
AppLog.FilePath = Path.Combine(logFolder, "paraphraser.log");

var transformer = new DemoTextTransformer();

var grammar = await transformer.TransformAsync(
    new RewriteRequest("i dont think this is ready", RewriteMode.Grammar));
Require(grammar.Text == "I don't think this is ready.", "Grammar transformation failed.");

var rewrite = await transformer.TransformAsync(
    new RewriteRequest("I have made the change and it will be completed soon", RewriteMode.Rewrite));
Require(rewrite.Text.Contains("I've made", StringComparison.Ordinal), "Rewrite transformation failed.");

var professional = await transformer.TransformAsync(
    new RewriteRequest("Hey, I will send it ASAP", RewriteMode.Professional));
Require(professional.Text.StartsWith("Hello", StringComparison.Ordinal), "Professional transformation failed.");

Require(
    WritingLanguageResolver.Resolve("मैं कल रिपोर्ट भेज दूँगा", WritingLanguage.Auto) == WritingLanguage.Hindi,
    "Hindi language detection failed.");
Require(
    WritingLanguageResolver.Resolve("kal report bhej dunga", WritingLanguage.Auto) == WritingLanguage.Hinglish,
    "Hinglish language detection failed.");
Require(
    WritingLanguageResolver.Resolve("The report is ready", WritingLanguage.Auto) == WritingLanguage.English,
    "English language detection failed.");

const string wordList = """
    <p class=MsoNormal>Hello <b style='mso-bidi-font-weight:normal'>bold</b><o:p></o:p></p>
    <p class=MsoListParagraphCxSpFirst style='mso-list:l0 level1 lfo1'><![if !supportLists]><span
    style='font-family:Symbol'><span style='mso-list:Ignore'>·<span style='font:7.0pt "Times New Roman"'>&nbsp;
    </span></span></span><![endif]>Bullet<o:p></o:p></p>
    <p class=MsoListParagraphCxSpLast style='mso-list:l1 level1 lfo2'><![if !supportLists]><span
    style='mso-list:Ignore'>1.<span style='font:7.0pt "Times New Roman"'>&nbsp;
    </span></span><![endif]>Step<o:p></o:p></p>
    """;
Require(
    RichDocument.ParseHtml(wordList).ToHtml()
        == "<p>Hello <b>bold</b></p>\n<ul>\n<li>Bullet</li>\n</ul>\n<ol>\n<li>Step</li>\n</ol>",
    "Word list parsing failed.");

Require(
    RichDocument.ParseHtml("""<span style="font-weight: 700;">a</span> <span style="font-style: italic;">b</span>""").ToHtml()
        == "<p><b>a</b> <i>b</i></p>",
    "Browser inline style parsing failed.");
Require(
    RichDocument.ParseHtml("""<b style="font-weight:normal" id="docs-internal-guid-1"><span style="font-weight:700">a</span> b</b>""").ToHtml()
        == "<p><b>a</b> b</p>",
    "Google Docs wrapper parsing failed.");
Require(
    RichDocument.ParseHtml("""<style>p{}</style><script>x()</script><a href="javascript:x()">a</a> <b onclick="x()">b</b>""").ToHtml()
        == "<p>a <b>b</b></p>",
    "HTML sanitizing failed.");
Require(
    RichDocument.ParseHtml("""<span style='color:red'>a</span> <span style='background:yellow;mso-highlight:yellow'>b</span>""").ToHtml()
        == """<p><span style="color:#ff0000">a</span> <span style="background-color:#ffff00">b</span></p>""",
    "Word color parsing failed.");
Require(
    !RichDocument.ParseHtml("""<span style="color: rgb(36, 36, 36); background-color: rgb(255, 255, 255);">a</span> <span style="color: #f7f8fc; background: rgb(31, 31, 31)">b</span>""").HasFormatting,
    "Default browser text must not count as colored.");
Require(
    RichDocument.ParseHtml("""<mark><span style="color:#0070c0;background-color:rgba(0, 0, 0, 0)">a</span></mark>""").ToHtml()
        == """<p><span style="color:#0070c0;background-color:#ffff00">a</span></p>""",
    "A transparent background must keep the highlight around it.");
Require(
    RichDocument.ParseHtml("""<a href="https://example.com"><span style="color:#0563c1;background:url(x)">a</span></a>""").ToHtml()
        == """<p><a href="https://example.com">a</a></p>""",
    "Links must not count as colored, and only real colors may pass.");
var coloredSource = RichDocument.ParseHtml(
    """<p>See the <span style="background-color:#ffff00"><b>budget numbers</b></span> and <span style="color:#0070c0">page</span>, <span style="color:#c00000">reworded</span></p>""");
Require(
    RichDocument.ParseHtml("<p>In debugging, the page has <b>Budget</b> numbers and budget numbers.</p>")
        .MatchColorsTo(coloredSource).ToHtml()
        == """<p>In debugging, the <span style="color:#0070c0">page</span> has <span style="background-color:#ffff00"><b>Budget</b></span><span style="background-color:#ffff00"> numbers</span> and budget numbers.</p>""",
    "Colors must return to the first whole-word match, across runs.");
Require(
    RichDocument.ParseHtml("""<p>the page and <span style="color:#0070c0">page</span></p>""")
        .MatchColorsTo(coloredSource).ToHtml()
        == """<p>the page and <span style="color:#0070c0">page</span></p>""",
    "A color the model kept must not be applied twice.");
var rewritten = RichDocument.ParseHtml("<p>Send it by Friday at 5 PM. Read The Great Gatsby and <b>call</b> Priya.</p>");
var formatted = rewritten.ApplyFormatting(new FormattingPlan(
    ["friday at 5 PM", "not in the text", "Send it by Friday at 5 PM.", new string('x', 61), "Priya"],
    ["The Great Gatsby"],
    ["call Priya"]));
Require(
    formatted.ToHtml()
        == "<p>Send it by <b>Friday at 5 PM</b>. Read <i>The Great Gatsby</i> and <b><u>call</u></b><u> Priya</u>.</p>",
    "Formatting must apply to exact phrases and skip sentences, long, or missing phrases.");
Require(
    formatted.ToPlainText() == rewritten.ToPlainText(),
    "Formatting must never change the text.");
Require(
    rewritten.ApplyFormatting(new FormattingPlan(["Priya"]), followModelStrictly: true).ToHtml()
        .Contains("<b>Priya</b>", StringComparison.Ordinal),
    "An instruction from the writer must be able to bold anything.");

var colorLimited = RichDocument.ParseHtml("<p>one two three four</p>").ApplyFormatting(
    new FormattingPlan(Highlight: ["one", "two", "three"], Red: ["four"]),
    followModelStrictly: true);
Require(
    colorLimited.ToHtml()
        == """<p><span style="background-color:#ffff00">one</span> <span style="background-color:#ffff00">two</span> three <span style="color:#c00000">four</span></p>""",
    "Automatic color must stay limited.");
Require(
    RichDocument.ParseHtml("""<p><span style="background-color:#00ffff">a</span></p>""")
        .ApplyFormatting(new FormattingPlan(Highlight: ["a"]), followModelStrictly: true).ToHtml()
        == """<p><span style="background-color:#00ffff">a</span></p>""",
    "Automatic color must not replace the writer's color.");
Require(
    !RichDocument.ParseHtml("<p>that lunch place was great</p>")
        .ApplyFormatting(new FormattingPlan(Highlight: ["lunch place"], Red: ["great"])).HasFormatting,
    "Casual text must stay plain even when the model picks something.");
Require(
    RichDocument.ParseHtml("<p>The flow is blocked until 12 March, so approve it.</p>")
        .ApplyFormatting(new FormattingPlan(["12 March"], Highlight: ["approve it"], Red: ["flow is blocked", "the flow"]))
        .ToHtml()
        == """<p>The <span style="color:#c00000">flow is blocked</span> until <b>12 March</b>, so <span style="background-color:#ffff00">approve it</span>.</p>""",
    "Real details and warnings must be formatted.");
Require(
    System.Text.Json.JsonSerializer.Deserialize<FormattingPlan>("""{"bold":["a"],"red":[]}""")?.Bold?.Count == 1,
    "A formatting plan must read from the model's JSON.");

Require(
    RichDocument.ParseHtml("""<p>by <span style="color:#ff0000;background-color:#00ffff"><b>tomorrow</b></span></p>""")
        .MatchColorsTo(RichDocument.FromPlainText("kal tak")).ToHtml()
        == "<p>by <b>tomorrow</b></p>",
    "Colors the model invented must be removed, keeping its bold.");
Require(
    RichDocument.ParseHtml("<p>a</p>").ToClipboardHtml().Contains("<p>a</p>", StringComparison.Ordinal)
    && RichDocument.ParseHtml("""<span style="background:yellow">a</span>""").ToClipboardHtml()
        .Contains("background-color:#ffff00;mso-highlight:yellow", StringComparison.Ordinal)
    && !RichDocument.ParseHtml("""<span style="background:yellow">a</span>""").ToHtml()
        .Contains("mso-highlight", StringComparison.Ordinal),
    "Word highlight markup belongs on the clipboard, not in the model prompt.");

Require(
    RichDocument.FromModelOutput("```html\n<p><b class=\"x\">a</b></p>\n```").ToHtml() == "<p><b>a</b></p>",
    "Model output parsing failed.");
Require(
    RichDocument.FromModelOutput("a\nb").ToPlainText() == $"a{Environment.NewLine}b",
    "Plain model output lost its line breaks.");
Require(
    RichDocument.FromModelOutput("<p>We read *The Great Gatsby* by **Friday**, 2 * 3 * 4.</p>").ToHtml()
        == "<p>We read <i>The Great Gatsby</i> by <b>Friday</b>, 2 * 3 * 4.</p>",
    "Markdown emphasis from the model must become formatting, leaving other asterisks alone.");
Require(
    RichDocument.FromModelOutput("first\n\n**second**").ToHtml() == "<p>first</p>\n<p><br></p>\n<p><b>second</b></p>",
    "Markdown in plain model output must keep its blank lines.");
Require(
    RichDocument.ParseHtml("<p>a</p><p><br></p><p>b</p>").ToPlainText()
        == $"a{Environment.NewLine}{Environment.NewLine}b",
    "A blank paragraph must survive the model round trip.");

const string clipboardHtml =
    "Version:0.9\r\nStartHTML:0\r\n<html><head><style>p{}</style></head><body><!--StartFragment-->"
    + "<p class=MsoNormal><span style='font-family:Arial;font-size:14pt'>old</span></p>"
    + "<!--EndFragment--></body></html>";
Require(
    !RichDocument.FromClipboard("  plain  text", clipboardHtml).HasFormatting
    && RichDocument.FromClipboard("  plain  text", clipboardHtml).ToPlainText() == "  plain  text",
    "Unformatted selections must keep their exact text.");
Require(
    RichDocument.ContainsUnsupportedContent("<!--StartFragment--><img src=a><!--EndFragment-->")
    && !RichDocument.ContainsUnsupportedContent(clipboardHtml),
    "Unsupported content detection failed.");

var replacement = RichDocument.ParseHtml("<p><b>नमस्ते</b> café</p>").ToClipboardHtml(clipboardHtml);
var replacementBytes = Encoding.UTF8.GetBytes(replacement);
var fragmentStart = int.Parse(Regex.Match(replacement, @"StartFragment:(\d+)").Groups[1].Value);
var fragmentEnd = int.Parse(Regex.Match(replacement, @"EndFragment:(\d+)").Groups[1].Value);
Require(
    Encoding.UTF8.GetString(replacementBytes, fragmentStart, fragmentEnd - fragmentStart)
        == "<p class=MsoNormal><span style=\"font-family:Arial;font-size:14pt\"><b>नमस्ते</b> café</span></p>"
    && replacement.Contains("<style>p{}</style>", StringComparison.Ordinal),
    "Clipboard HTML offsets or source styles are wrong.");
Require(
    RichDocument.ParseHtml("<p><b>a</b></p>")
        .ToClipboardHtml("<html><body><!--StartFragment--><span>old</span><!--EndFragment--></body></html>")
        .Contains("<!--StartFragment--><b>a</b><!--EndFragment-->", StringComparison.Ordinal),
    "A selection ending mid-paragraph must paste inline.");

var flowFailure = default(Exception);
var flowThread = new Thread(() =>
{
    try
    {
        var html = "<h2>Title</h2>\n<p>a <b>b</b> <i>c</i> <u>d</u> <a href=\"https://example.com\">e</a><br>f <span style=\"color:#c00000\">i</span> <span style=\"background-color:#ffff00\"><b>j</b></span></p>\n<ul>\n<li>g</li>\n</ul>\n<ol>\n<li>h</li>\n</ol>";
        var flow = RichFlowDocument.ToFlowDocument(RichDocument.ParseHtml(html));
        Require(RichFlowDocument.ToRichDocument(flow).ToHtml() == html, "Overlay document round trip failed.");

        var clipboardSource = new System.Windows.DataObject();
        clipboardSource.SetData(System.Windows.DataFormats.UnicodeText, "original");
        var snapshot = ClipboardSnapshot.CopyFormats(clipboardSource);
        clipboardSource.SetData(System.Windows.DataFormats.UnicodeText, "changed");
        Require(
            snapshot?.GetData(System.Windows.DataFormats.UnicodeText) as string == "original",
            "A clipboard snapshot must not follow later clipboard changes.");
        Require(
            ClipboardSnapshot.CopyFormats(new System.Windows.DataObject()) is null,
            "An empty clipboard must snapshot as empty.");

        const string excludeFromHistory = "ExcludeClipboardContentFromMonitorProcessing";
        var secret = new System.Windows.DataObject();
        secret.SetData(System.Windows.DataFormats.UnicodeText, "secret");
        secret.SetData(excludeFromHistory, new MemoryStream());
        Require(
            ClipboardSnapshot.CopyFormats(secret)?.GetDataPresent(excludeFromHistory) == true,
            "A clipboard snapshot must keep the clipboard history exclusion.");

        var metafileOnly = new System.Windows.DataObject();
        metafileOnly.SetData("EnhancedMetafile", new MemoryStream(new byte[] { 1 }), false);
        Require(
            ClipboardSnapshot.CopyFormats(metafileOnly) is not null,
            "A format must be kept when nothing else can stand in for it.");
    }
    catch (Exception exception)
    {
        flowFailure = exception;
    }
});
flowThread.SetApartmentState(ApartmentState.STA);
flowThread.Start();
flowThread.Join();
if (flowFailure is not null)
{
    throw flowFailure;
}

var openAi = OnlineProvider.Find("openai");
var fakeServer = new FakeServer(
    ": OPENROUTER PROCESSING\n\n"
    + """data: {"choices":[{"delta":{"role":"assistant"}}]}""" + "\n\n"
    + """data: {"choices":[{"delta":{"content":" Do"}}]}""" + "\n\n"
    + """data: {"choices":[{"delta":{"content":"ne. "},"finish_reason":"stop"}]}""" + "\n\n"
    + "data: [DONE]\n\n");
var openAiBackend = new OpenAiCompatibleBackend(openAi, "test-key", "test-model", new HttpClient(fakeServer));
var streamedText = new List<string>();
Require(
    await openAiBackend.CompleteAsync(new ChatPrompt("rules", "text"), streamedText.Add) == "Done.",
    "An OpenAI-style stream was not read.");
Require(
    streamedText.SequenceEqual([" Do", " Done. "]),
    "Streaming must report all the text so far, as it arrives.");
Require(
    fakeServer.LastUrl == "https://api.openai.com/v1/chat/completions"
    && fakeServer.LastAuthorization == "Bearer test-key"
    && fakeServer.LastBody.Contains("\"model\":\"test-model\"", StringComparison.Ordinal)
    && fakeServer.LastBody.Contains("\"stream\":true", StringComparison.Ordinal)
    && fakeServer.LastBody.Contains("\"role\":\"system\"", StringComparison.Ordinal)
    && !fakeServer.LastBody.Contains("temperature", StringComparison.Ordinal),
    "An OpenAI-style request had the wrong shape.");

var busyThenFine = new FakeServer(
    """data: {"choices":[{"delta":{"content":"Recovered"}}]}""" + "\n\n",
    failuresFirst: 1);
Require(
    await new OpenAiCompatibleBackend(openAi, "key", "m", new HttpClient(busyThenFine))
        .CompleteAsync(new ChatPrompt("a", "b")) == "Recovered"
    && busyThenFine.RequestCount == 2,
    "A brief server error must be retried with a fresh request.");

var alwaysBusy = new FakeServer(
    """{"error":{"message":"The model test-secret-key is overloaded."}}""",
    System.Net.HttpStatusCode.ServiceUnavailable);
try
{
    await new OpenAiCompatibleBackend(openAi, "test-secret-key", "m", new HttpClient(alwaysBusy))
        .CompleteAsync(new ChatPrompt("a", "b"));
    throw new InvalidOperationException("Expected a server error.");
}
catch (InvalidOperationException exception)
{
    Require(
        exception.Message.StartsWith("OpenAI is having problems (503): The model *** is overloaded.", StringComparison.Ordinal)
        && exception.Message.Contains("pick another model", StringComparison.Ordinal)
        && alwaysBusy.RequestCount == 3,
        $"A lasting server error must give up and show the reason: {exception.Message}");
}

var silentServer = new SilentServer();
var silentBackend = new OpenAiCompatibleBackend(
    openAi,
    "key",
    "busy-model",
    new HttpClient(silentServer),
    firstResponseTimeout: TimeSpan.FromMilliseconds(100));
var silentClock = System.Diagnostics.Stopwatch.StartNew();
try
{
    await silentBackend.CompleteAsync(new ChatPrompt("a", "b"));
    throw new InvalidOperationException("Expected an overloaded model to give up.");
}
catch (AiUnavailableException exception)
{
    Require(
        exception.Message.Contains("busy-model may be overloaded", StringComparison.Ordinal)
        && silentServer.RequestCount == 1
        && silentClock.Elapsed < TimeSpan.FromSeconds(5),
        $"A silent model must give up once with a useful message: {exception.Message}");
}

using (var stopNow = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
{
    var stopWasReported = false;
    try
    {
        await new OpenAiCompatibleBackend(openAi, "key", "m", new HttpClient(new SilentServer()))
            .CompleteAsync(new ChatPrompt("a", "b"), cancellationToken: stopNow.Token);
    }
    catch (OperationCanceledException)
    {
        stopWasReported = true;
    }

    Require(stopWasReported, "Pressing Stop must not be reported as a provider failure.");
}

var mixedModels = await new OpenAiCompatibleBackend(
    OnlineProvider.Find("gemini"),
    "key",
    string.Empty,
    new HttpClient(new FakeServer("""{"data":[{"id":"models/gemini-3.5-flash-lite"},{"id":"models/gemini-3.8-live"},{"id":"models/deep-research-preview"},{"id":"models/gemini-2.5-computer-use-preview"},{"id":"models/antigravity-preview"}]}""")))
    .ListModelsAsync();
Require(
    mixedModels.SequenceEqual(["gemini-3.5-flash-lite"]),
    "Models that cannot rewrite text must not be offered.");

var failureLog = File.ReadAllText(AppLog.FilePath);
Require(
    failureLog.Contains("attempt 1, retrying", StringComparison.Ordinal)
    && failureLog.Contains("HTTP 503: The model *** is overloaded.", StringComparison.Ordinal)
    && !failureLog.Contains("test-secret-key", StringComparison.Ordinal),
    "The log must record retries and failures but never the API key.");

var rejectedKey = new FakeServer(
    """{"error":{"message":"Incorrect API key provided: partial-key-echo"}}""",
    System.Net.HttpStatusCode.Unauthorized);
await RequireError<AiUnavailableException>(rejectedKey, "rejected your API key", "partial-key-echo");
Require(
    File.ReadAllText(AppLog.FilePath) is var rejectedLog
    && rejectedLog.Contains("HTTP 401", StringComparison.Ordinal)
    && !rejectedLog.Contains("partial-key-echo", StringComparison.Ordinal),
    "A rejected key's error text must not reach the log.");

AppLog.Info(new string('x', 1024 * 1024));
AppLog.Info("after rotation");
Require(
    File.Exists(Path.ChangeExtension(AppLog.FilePath, ".previous.log"))
    && new FileInfo(AppLog.FilePath).Length < 1024
    && File.ReadAllText(AppLog.FilePath).Contains("after rotation", StringComparison.Ordinal),
    "The log must rotate instead of growing forever.");

var claudeWithoutKey = new AnthropicBackend(OnlineProvider.Find("anthropic"), string.Empty, "m", "http://127.0.0.1:9");
try
{
    await claudeWithoutKey.CompleteAsync(new ChatPrompt("a", "b"));
    throw new InvalidOperationException("Expected a connection error.");
}
catch (AiUnavailableException)
{
    // An empty key must reach this friendly error, not crash the error logging.
}

var workingLogPath = AppLog.FilePath;
AppLog.FilePath = Path.Combine(logFolder, "bad\0name", "paraphraser.log");
AppLog.Error("must not throw", new InvalidOperationException("test"));
AppLog.FilePath = workingLogPath;

var notRetried = new FakeServer("{}", System.Net.HttpStatusCode.TooManyRequests);
await RequireError<InvalidOperationException>(notRetried, "rate limit", "unknown");
Require(notRetried.RequestCount == 1, "Rate limits must not be retried.");

await RequireStreamError(
    """data: {"error":{"message":"provider overloaded"}}""" + "\n\n",
    "provider overloaded");
await RequireStreamError(
    """data: {"choices":[{"delta":{"content":"half a sen"},"finish_reason":"length"}]}""" + "\n\n",
    "cut off");
await RequireStreamError("data: [DONE]\n\n", "empty response");
await RequireStreamError("data: {not json\n\n", "could not be read");

Require(
    RichDocument.FromPartialModelOutput("```html\n<p>Hello <b>wor").ToHtml() == "<p>Hello <b>wor</b></p>",
    "Partial output must drop the code fence and survive an open tag.");
Require(
    RichDocument.FromPartialModelOutput("<p>Hello <span sty").ToHtml() == "<p>Hello</p>",
    "A half-written tag must not appear as text.");
Require(
    RichDocument.FromPartialModelOutput("<p>Done</p>\n```").ToHtml() == "<p>Done</p>",
    "A closing fence must not appear as text.");

var keyCheckServer = new FakeServer("{}", System.Net.HttpStatusCode.Unauthorized);
var keyCheck = await new OpenAiCompatibleBackend(
    OnlineProvider.Find("openrouter"),
    "bad-key",
    "model",
    new HttpClient(keyCheckServer)).CheckStatusAsync();
Require(
    !keyCheck.IsReady && keyCheckServer.LastUrl == "https://openrouter.ai/api/v1/key",
    "OpenRouter's public model list must not be used to check a key.");

var geminiModels = await new OpenAiCompatibleBackend(
    OnlineProvider.Find("gemini"),
    "key",
    string.Empty,
    new HttpClient(new FakeServer("""{"data":[{"id":"models/zeta"},{"id":"models/alpha"},{"id":"text-embedding-3"},{"id":"whisper-1"}]}""")))
    .ListModelsAsync();
Require(
    geminiModels.SequenceEqual(["alpha", "zeta"]),
    "Model lists must drop non-chat models and the models/ prefix.");

await RequireError<AiUnavailableException>(
    new FakeServer("""{"error":{"message":"Incorrect API key provided: test-sec***"}}""", System.Net.HttpStatusCode.Unauthorized),
    "rejected your API key",
    "test-sec");
await RequireError<AiUnavailableException>(
    new FakeServer("""[{"error":{"message":"API key not valid. Please pass a valid API key."}}]""", System.Net.HttpStatusCode.BadRequest),
    "rejected your API key",
    "valid API key");
await RequireError<AiUnavailableException>(
    new FakeServer("{}", System.Net.HttpStatusCode.NotFound),
    "could not find model",
    "unknown");
await RequireError<InvalidOperationException>(
    new FakeServer("{}", System.Net.HttpStatusCode.TooManyRequests),
    "rate limit",
    "unknown");
await RequireError<InvalidOperationException>(
    new FakeServer("<html>bad gateway</html>", System.Net.HttpStatusCode.BadGateway),
    "having problems",
    "html");

var stubOnline = new StubBackend(isLocal: false, """Here you go: ```json {"bold":["Friday"]} ```""");
var stubOnlineResult = await new ChatTextTransformer(stubOnline).TransformAsync(new RewriteRequest(
    "hello there",
    RewriteMode.Rewrite,
    Context: new RewriteContext("OUTLOOK", "Salary review for Priya.docx")));
Require(
    !stubOnline.LastPrompt.User.Contains("Salary review", StringComparison.Ordinal)
    && !stubOnline.LastPrompt.User.Contains("OUTLOOK", StringComparison.Ordinal)
    && stubOnline.LastPrompt.User.Contains("hello there", StringComparison.Ordinal),
    "Window titles must not leave this computer.");
Require(stubOnlineResult.Provider == "Stub · model", "Results must name their provider.");

var stubLocal = new StubBackend(isLocal: true, "reply");
await new ChatTextTransformer(stubLocal).TransformAsync(new RewriteRequest(
    "hello there",
    RewriteMode.Rewrite,
    Context: new RewriteContext("OUTLOOK", "Salary review for Priya.docx")));
Require(
    stubLocal.LastPrompt.User.Contains("Salary review", StringComparison.Ordinal),
    "Local models should still get the window context.");

var fencedPlan = await new ChatTextTransformer(stubOnline).SuggestFormattingAsync("By Friday", "format");
Require(
    fencedPlan.Plan.Bold?.SequenceEqual(["Friday"]) == true && stubOnline.LastPrompt.ExpectJson,
    "Formatting JSON wrapped in other text must still be read.");

var unreadablePlan = false;
try
{
    await new ChatTextTransformer(new StubBackend(isLocal: false, "no json here")).SuggestFormattingAsync("text", "format");
}
catch (InvalidOperationException)
{
    unreadablePlan = true;
}

Require(unreadablePlan, "Unreadable formatting must fail with a readable error.");

var settingsFolder = Path.Combine(Path.GetTempPath(), $"paraphraser-smoke-{Guid.NewGuid():N}");
var settingsPath = Path.Combine(settingsFolder, "settings.json");
try
{
    const string secretKey = "sk-smoke-test-value-1234567890";
    var savedSettings = new AppSettings { IsOnline = true, OnlineProviderId = "gemini" };
    savedSettings.SetApiKey("gemini", secretKey);
    savedSettings.GetProvider("gemini").Model = "alpha";
    savedSettings.Save(settingsPath);

    Require(
        !File.ReadAllText(settingsPath).Contains(secretKey, StringComparison.Ordinal),
        "An API key must never be written to disk in plain text.");

    var loadedSettings = AppSettings.Load(settingsPath);
    Require(
        loadedSettings.GetApiKey("gemini") == secretKey
        && loadedSettings.CreateBackend().Label == "Google Gemini · alpha"
        && !loadedSettings.CreateBackend().IsLocal,
        "Saved online settings did not load.");

    loadedSettings.SetApiKey("gemini", null);
    Require(!loadedSettings.HasApiKey("gemini"), "Removing a key must clear it.");

    loadedSettings.GetProvider("gemini").EncryptedApiKey = "not encrypted";
    Require(loadedSettings.GetApiKey("gemini") is null, "An unreadable key must not crash the app.");

    File.WriteAllText(settingsPath, "{ damaged");
    Require(!AppSettings.Load(settingsPath).IsOnline, "A damaged settings file must fall back to defaults.");
    Require(AppSettings.Load(settingsPath).CreateBackend().IsLocal, "Offline must be the default.");
}
finally
{
    if (Directory.Exists(settingsFolder))
    {
        Directory.Delete(settingsFolder, recursive: true);
    }
}

if (string.Equals(
    Environment.GetEnvironmentVariable("PARAPHRASER_RUN_LOCAL_AI_TESTS"),
    "1",
    StringComparison.Ordinal))
{
    var localAi = new ChatTextTransformer(new OllamaBackend());
    var hindi = await localAi.TransformAsync(
        new RewriteRequest(
            "मैं कल रिपोर्ट भेज दूंगा लेकिन थोड़ा देर हो सकता है",
            RewriteMode.Grammar,
            "Correct grammar and make this polite.",
            Tone: RewriteTone.Polite));
    RequireEnglish(hindi.Text, "Local AI did not translate Hindi into English.");

    var hinglish = await localAi.TransformAsync(
        new RewriteRequest(
            "kal client ko update bhej dunga but thoda delay ho sakta hai",
            RewriteMode.Rewrite,
            "Translate this into polished, professional English.",
            Tone: RewriteTone.Professional));
    RequireEnglish(hinglish.Text, "Local AI did not translate Hinglish into English.");

    var conflictingInstruction = await localAi.TransformAsync(
        new RewriteRequest(
            "I will send the final report tomorrow morning.",
            RewriteMode.Rewrite,
            "Translate this into Hindi.",
            Tone: RewriteTone.Professional));
    RequireEnglish(
        conflictingInstruction.Text,
        "A conflicting instruction overrode the English-only output rule.");

    Console.WriteLine($"Hindi: {hindi.Text}");
    Console.WriteLine($"Hinglish: {hinglish.Text}");
    Console.WriteLine($"Conflicting instruction: {conflictingInstruction.Text}");
}

Directory.Delete(logFolder, recursive: true);

Console.WriteLine("All ParaPhraser.Core smoke tests passed.");
return;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void RequireEnglish(string text, string message)
{
    Require(
        WritingLanguageResolver.Resolve(text, WritingLanguage.Auto) == WritingLanguage.English
        && !text.Any(character => character is >= '\u0900' and <= '\u097F'),
        message);
}

static async Task RequireStreamError(string stream, string expected)
{
    var backend = new OpenAiCompatibleBackend(
        OnlineProvider.Find("openai"),
        "key",
        "test-model",
        new HttpClient(new FakeServer(stream)));

    try
    {
        var text = await backend.CompleteAsync(new ChatPrompt("rules", "text"));
        throw new InvalidOperationException($"Expected a stream error, got text: {text}");
    }
    catch (InvalidOperationException exception) when (exception.Message.Contains(expected, StringComparison.Ordinal))
    {
    }
}

static async Task RequireError<TException>(FakeServer server, string expected, string forbidden)
    where TException : Exception
{
    var backend = new OpenAiCompatibleBackend(
        OnlineProvider.Find("openai"),
        "key",
        "test-model",
        new HttpClient(server));

    try
    {
        await backend.CompleteAsync(new ChatPrompt("rules", "text"));
    }
    catch (TException exception)
    {
        Require(
            exception.Message.Contains(expected, StringComparison.Ordinal)
            && !exception.Message.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            $"Wrong error message: {exception.Message}");
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class FakeServer(
    string body,
    System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK,
    int failuresFirst = 0) : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    public string? LastUrl { get; private set; }

    public string? LastAuthorization { get; private set; }

    public string LastBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        LastUrl = request.RequestUri?.ToString();
        LastAuthorization = request.Headers.Authorization?.ToString();
        LastBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        return RequestCount <= failuresFirst
            ? new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") }
            : new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}

sealed class SilentServer : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("Unreachable.");
    }
}

sealed class StubBackend(bool isLocal, string reply) : IChatBackend
{
    public ChatPrompt LastPrompt { get; private set; } = new(string.Empty, string.Empty);

    public string ProviderName => "Stub";

    public string Label => "Stub \u00B7 model";

    public bool IsLocal => isLocal;

    public Task<AiStatus> CheckStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiStatus(true, "ready"));

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(["model"]);

    public Task<string> CompleteAsync(
        ChatPrompt prompt,
        Action<string>? onText = null,
        CancellationToken cancellationToken = default)
    {
        LastPrompt = prompt;
        onText?.Invoke(reply);
        return Task.FromResult(reply);
    }
}
