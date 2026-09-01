using ParaPhraser.Core;

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

if (string.Equals(
    Environment.GetEnvironmentVariable("PARAPHRASER_RUN_LOCAL_AI_TESTS"),
    "1",
    StringComparison.Ordinal))
{
    var localAi = new OllamaTextTransformer();
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
