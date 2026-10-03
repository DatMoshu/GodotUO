using GUO.Localization;
using GUO.Game.Data;
using System.Net;
using System.Text.Json;

int passed = 0;
async Task Check(string name, Func<Task> test)
{
    await test();
    Console.WriteLine("PASS " + name);
    passed++;
}
void Require(bool value, string message = "Assertion failed")
{
    if (!value) throw new Exception(message);
}
async Task<TranslationResult> Take(JournalTranslationSession session)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (true)
    {
        if (session.TryTake(out var result)) return result;
        await Task.Delay(5, deadline.Token);
    }
}

await Check("language catalogue, aliases and distinct provider directions", () =>
{
    Require(TranslationLanguages.All.Count == 54);
    foreach (var language in TranslationLanguages.All)
    {
        Require(TranslationLanguages.TryResolve(language.Code.ToUpperInvariant(), out var resolved));
        Require(resolved == language);
        using var provider = new OllamaJournalTranslator("http://127.0.0.1:11434/api/chat", "model",
            language.Code, language.Code == "en" ? "it" : "en", null);
    }
    foreach (var pair in new[] { ("pt", "pt-BR"), ("ZH_tw", "zh-Hant"), ("zh-CN", "zh-Hans"),
        ("es-MX", "es"), ("hi_IN", "hi"), ("fil", "tl") })
        Require(TranslationLanguages.TryResolve(pair.Item1, out var value) && value.Code == pair.Item2);
    foreach (var invalid in new[] { "xx", "en-instructions", "zh-invalid", "", "pt-XX" })
        Require(!TranslationLanguages.TryResolve(invalid, out _));
    bool rejected = false;
    try { using var same = new OllamaJournalTranslator("http://127.0.0.1:11434/api/chat", "model", "en-US", "en", null); }
    catch (ArgumentException) { rejected = true; }
    Require(rejected);
    return Task.CompletedTask;
});

await Check("only opted-in public speech; private channels, ignores, clilocs and commands excluded", () =>
{
    foreach (MessageType type in new[] { MessageType.Regular, MessageType.Emote, MessageType.Yell })
    {
        Require(JournalTranslationPolicy.CanTranslate(type, "ciao", true, true, false));
        Require(!JournalTranslationPolicy.CanTranslate(type, "ciao", false, true, false));
        Require(!JournalTranslationPolicy.CanTranslate(type, "ciao", true, false, false));
        Require(!JournalTranslationPolicy.CanTranslate(type, "ciao", true, true, true));
        Require(!JournalTranslationPolicy.CanTranslate(type, " [bank", true, true, false));
        Require(!JournalTranslationPolicy.CanTranslate(type, "/command", true, true, false));
    }
    foreach (MessageType type in new[] { MessageType.Whisper, MessageType.Guild, MessageType.Alliance,
        MessageType.Party, MessageType.System, MessageType.Label, MessageType.Spell, MessageType.Command })
        Require(!JournalTranslationPolicy.CanTranslate(type, "ciao", true, true, false));
    return Task.CompletedTask;
});
await Check("bounded outstanding work and immutable message ids", async () =>
{
    var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var session = new JournalTranslationSession(new FakeProvider((r, c) => gate.Task.WaitAsync(c)), TimeSpan.FromSeconds(3), 2);
    Require(session.TryQueue(new(41, "ciao", "Renval")));
    Require(session.TryQueue(new(42, "salve", "Renval")));
    Require(!session.TryQueue(new(43, "overflow", "Renval")));
    gate.SetResult("hello");
    var first = await Take(session);
    var second = await Take(session);
    Require(first.Id == 41 && second.Id == 42 && first.Text == "hello");
    Require(session.TryQueue(new(44, "new", "Renval")));
});
await Check("timeout preserves original and later work proceeds", async () =>
{
    using var session = new JournalTranslationSession(new FakeProvider(async (r, c) =>
    {
        if (r.Id == 1) await Task.Delay(Timeout.Infinite, c);
        return "hello";
    }), TimeSpan.FromMilliseconds(40));
    session.TryQueue(new(1, "ciao", "A"));
    session.TryQueue(new(2, "salve", "A"));
    var first = await Take(session);
    Require(first.Status == "timeout" && first.Text == null);
    Require((await Take(session)).Status == "translated");
});
await Check("disconnect cancels active work and suppresses late results", async () =>
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var session = new JournalTranslationSession(new FakeProvider(async (r, c) =>
    {
        started.SetResult();
        try { await Task.Delay(Timeout.Infinite, c); }
        finally { cancelled.SetResult(); }
        return "unexpected";
    }), TimeSpan.FromSeconds(3));
    session.TryQueue(new(9, "ciao", "A"));
    await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
    session.Dispose();
    await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    await session.Completion;
    Require(!session.TryTake(out _) && !session.TryQueue(new(10, "no", "A")));
    session.Dispose();
});
await Check("provider failures and overlong output fall back", async () =>
{
    using var session = new JournalTranslationSession(new FakeProvider((r, c) => r.Id == 1
        ? Task.FromException<string>(new HttpRequestException()) : Task.FromResult(new string('x', 2049))), TimeSpan.FromSeconds(1));
    session.TryQueue(new(1, "ciao", "A"));
    session.TryQueue(new(2, "ciao", "A"));
    Require((await Take(session)).Status == "unavailable");
    Require((await Take(session)).Status == "unavailable");
    Require(!session.TryQueue(new(3, new string('x', 1025), "A")));
});
await Check("remote endpoints and unsupported languages rejected", () =>
{
    foreach (string endpoint in new[] { "https://example.com/api/chat", "http://127.0.0.1.evil.test/api/chat", "http://127.0.0.1/api/chat?key=x" })
    {
        bool rejected = false;
        try { using var p = new OllamaJournalTranslator(endpoint, "model", "it", "en", null); }
        catch (ArgumentException) { rejected = true; }
        Require(rejected);
    }
    bool badLocale = false;
    try { using var p = new OllamaJournalTranslator("http://127.0.0.1:11434/api/chat", "model", "xx", "en", null); }
    catch (ArgumentException) { badLocale = true; }
    Require(badLocale);
    return Task.CompletedTask;
});
await Check("actual HTTP serialization and protected terms with emote wrapper", async () =>
{
    var handler = new FakeHttp(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
        Require(!body.RootElement.GetProperty("stream").GetBoolean());
        string text = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
        Require(text.Contains("Renval") && text.Contains("Black Moon") && !text.Contains("Luna Nera"));
        return "Renval enters Black Moon.";
    });
    using var provider = new OllamaJournalTranslator("http://127.0.0.1:11434/api/chat", "model", "it", "en",
        new Dictionary<string, string> { ["Luna Nera"] = "Black Moon" }, handler);
    string translated = await provider.TranslateAsync(new(1, "*Renval entra nella Luna Nera.*", "Renval"), default);
    Require(translated == "*Renval enters Black Moon.*", translated);
});
await Check("missing, changed or duplicated protected terms rejected", async () =>
{
    foreach (string output in new[] { "name lost", "Renvale", "Renval Renval" })
    {
        using var provider = new OllamaJournalTranslator("http://127.0.0.1:11434/api/chat", "model", "it", "en", null,
            new FakeHttp(_ => Task.FromResult(output)));
        bool rejected = false;
        try { await provider.TranslateAsync(new(1, "Ciao Renval", "Renval"), default); }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected);
    }
});
await Check("literal glossary output accepted only with exact occurrence counts", async () =>
{
    using var provider = new OllamaJournalTranslator("http://127.0.0.1:11434/api/chat", "model", "en", "it",
        new Dictionary<string, string> { ["Black Moon"] = "Luna Nera" },
        new FakeHttp(_ => Task.FromResult("Incontrami alla Luna Nera, Renval.")));
    Require(await provider.TranslateAsync(new(1, "Meet me at Black Moon, Renval.", "Renval"), default)
        == "Incontrami alla Luna Nera, Renval.");
});
await Check("stub Ollama over loopback HTTP end to end; absent Ollama degrades to unavailable", async () =>
{
    int port;
    using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
    {
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
    }
    // Nothing listens on the port yet: the session must report unavailable, not throw or hang.
    using (var absent = new JournalTranslationSession(
        new OllamaJournalTranslator($"http://127.0.0.1:{port}/api/chat", "model", "it", "en", null), TimeSpan.FromSeconds(5)))
    {
        Require(absent.TryQueue(new(1, "Ciao", "Renval")));
        var result = await Take(absent);
        Require(result.Status == "unavailable" && result.Text == null, result.Status);
    }
    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    var server = Task.Run(async () =>
    {
        var context = await listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream);
        using var body = JsonDocument.Parse(await reader.ReadToEndAsync());
        Require(context.Request.Url!.AbsolutePath == "/api/chat");
        string reply = JsonSerializer.Serialize(new
        {
            done = true,
            message = new { content = JsonSerializer.Serialize(new { translation = "Hello" }) }
        });
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(reply);
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    });
    using var session = new JournalTranslationSession(
        new OllamaJournalTranslator($"http://127.0.0.1:{port}/api/chat", "model", "it", "en", null), TimeSpan.FromSeconds(5));
    Require(session.TryQueue(new(7, "Ciao", "Renval")));
    var ok = await Take(session);
    Require(ok.Status == "translated" && ok.Text == "Hello" && ok.Id == 7, ok.Status);
    await server;
});
Console.WriteLine($"{passed} localization tests passed");

if (args.Contains("--live"))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    using var local = new OllamaJournalTranslator(Environment.GetEnvironmentVariable("GUO_TRANSLATION_TEST_ENDPOINT") ?? "http://127.0.0.1:11434/api/chat", "qwen3:4b", "it", "en",
        new Dictionary<string, string> { ["Luna Nera"] = "Black Moon", ["Renval"] = "Renval" });
    Console.WriteLine(await local.TranslateAsync(new(1, "Ci vediamo alla Luna Nera, Renval.", "Localizer"), timeout.Token));
    using var italian = new OllamaJournalTranslator(Environment.GetEnvironmentVariable("GUO_TRANSLATION_TEST_ENDPOINT") ?? "http://127.0.0.1:11434/api/chat", "qwen3:4b", "en", "it",
        new Dictionary<string, string> { ["Black Moon"] = "Luna Nera", ["Renval"] = "Renval" }, new SyntheticDiagnosticHttp());
    Console.WriteLine(await italian.TranslateAsync(new(2, "Meet me at the Black Moon, Renval.", "Localizer"), timeout.Token));
    Console.WriteLine(await italian.TranslateAsync(new(3, "*Opens the door and greets Renval.*", "Localizer"), timeout.Token));
}

if (args.Contains("--languages-live"))
{
    string endpoint = Environment.GetEnvironmentVariable("GUO_TRANSLATION_TEST_ENDPOINT") ?? "http://127.0.0.1:11434/api/chat";
    foreach (var language in TranslationLanguages.All.Where(l => l.Code != "en"))
    {
        using var provider = new OllamaJournalTranslator(endpoint, "qwen3:4b", "en", language.Code, null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            string result = await provider.TranslateAsync(new(1, "Welcome to our town. The tavern is near the river.", ""), timeout.Token);
            Require(!string.IsNullOrWhiteSpace(result));
            Console.WriteLine(JsonSerializer.Serialize(new { target = language.Code, status = "response-only-unreviewed", text = result }));
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { target = language.Code, status = "failed", error = error.GetType().Name }));
        }
    }
}

sealed class FakeProvider(Func<TranslationRequest, CancellationToken, Task<string>> action) : IJournalTranslationProvider
{
    public Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellation) => action(request, cancellation);
}
// Only used by --live with the synthetic sentences above, never with player chat.
sealed class SyntheticDiagnosticHttp() : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (json.RootElement.TryGetProperty("message", out var message)) Console.WriteLine(message.GetProperty("content").GetString());
        return response;
    }
}
sealed class FakeHttp(Func<HttpRequestMessage, Task<string>> action) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { done = true,
                message = new { content = JsonSerializer.Serialize(new { translation = await action(request) }) } }))
        };
}
