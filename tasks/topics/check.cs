#:sdk Microsoft.NET.Sdk.Web
#:project C:/Users/aRRma99/source/repos/ME/AgentsTracker-topics/src/AgentsTracker.Gateway
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Проверка тем без Telegram: состояние тем, полосы, замок папки, /stop, беседа.
using System.Collections.Concurrent;
using AgentsTracker.Agents;
using AgentsTracker.Agents.Claude;
using AgentsTracker.Channels;
using AgentsTracker.Channels.Telegram;
using AgentsTracker.Gateway.Domain;
using AgentsTracker.Gateway.Features.Approvals;
using AgentsTracker.Gateway.Features.Chat;
using AgentsTracker.Gateway.Infrastructure;
using AgentsTracker.Gateway.Infrastructure.Audit;
using AgentsTracker.Gateway.Infrastructure.Configuration;
using AgentsTracker.Gateway.Infrastructure.Git;
using AgentsTracker.Gateway.Infrastructure.Monitoring;
using AgentsTracker.Gateway.Infrastructure.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {what}"); if (!ok) failures++; }

var root = Path.Combine(Path.GetTempPath(), "at-topics-harness");
if (Directory.Exists(root)) Directory.Delete(root, true);
Directory.CreateDirectory(root);
AppPaths.UseDirectory(Path.Combine(root, "data"));
var projA = Directory.CreateDirectory(Path.Combine(root, "projA")).FullName;
var projB = Directory.CreateDirectory(Path.Combine(root, "projB")).FullName;

var options = Options.Create(new GatewayOptions
{
    ProjectPath = projA,
    Model = "opus",
    PermissionMode = "default",
    Question = new QuestionOptions { Model = "sonnet" },
});

// --- ключи адреса ---
var feed = new TelegramChatId(100);
var t1 = new TelegramChatId(100, 7);
var t2 = new TelegramChatId(100, 8);
Check(feed.Key == "telegram:100" && t1.Key == "telegram:100/7", "ключи ленты и темы");
Check(TelegramChatId.TryParse("telegram:100", out var p0) && p0 == feed, "старый ключ без темы разбирается");
Check(TelegramChatId.TryParse("telegram:100/7", out var p1) && p1 == t1 && p1!.IsTopic, "ключ темы разбирается");
Check(!TelegramChatId.TryParse("telegram:100/x", out _), "битая тема не разбирается");

// --- состояние тем ---
var store = new SessionStore(options, NullLogger<SessionStore>.Instance);
var sf = store.For(feed);
var s1 = store.For(t1);
Check(!sf.IsTopic && !sf.IsTalk && ProjectCatalog.Same(sf.ProjectPath, projA), "лента — проект из конфига");
Check(s1.IsNewTopic && s1.IsTalk && SessionStore.IsTalkFolder(s1.ProjectPath), "новая тема — беседа в своей папке");
Check(s1.EffectiveModel == "sonnet" && sf.EffectiveModel == "opus", "модель беседы — вопросная, ленты — из конфига");

s1.SetProjectPath(projA);
Check(!s1.IsNewTopic && !s1.IsTalk && ProjectCatalog.Same(s1.ProjectPath, projA), "тема привязана к проекту из конфига (полный путь)");
Check(store.Snapshot().Topics["telegram:100/7"].ProjectPath is not null, "проект темы хранится, а не null");
Check(store.Snapshot().ProjectPath is null, "лента не тронута");

sf.SetSessionId("feed-sess");
s1.SetSessionId("topic-sess");
Check(sf.SessionId == "feed-sess" && s1.SessionId == "topic-sess", "сессии ленты и темы на одном проекте разные");

s1.SetModel("fable");
Check(s1.EffectiveModel == "fable" && sf.EffectiveModel == "opus", "модель темы своя");

var s2 = store.For(t2);
s2.SetModel("haiku");
Check(s2.IsNewTopic && s2.EffectiveModel == "haiku", "/model в новой теме: модель запомнена, тип не выбран");
s2.SetProjectPath(null);
Check(s2.IsTalk && !s2.IsNewTopic && s2.EffectiveModel == "haiku", "выбор «беседа» — тип выбран, модель осталась");

// TrySetSessionId — только в своём ключе
Check(!s1.TrySetSessionId(projA, "x", onlyIfActive: "feed-sess"), "тема не меняет сессию по ленточной");
Check(s1.TrySetSessionId(projA, "topic-2", onlyIfActive: "topic-sess") && s1.SessionId == "topic-2" && sf.SessionId == "feed-sess", "тема меняет свою сессию");

// RegisterSession делает активной в ключе темы
s1.RegisterSession(projA, "привет", "reg-1");
Check(s1.SessionId == "reg-1" && sf.SessionId == "feed-sess", "новая сессия темы активна только в теме");

// Тема сменила проект — у нового проекта своя сессия, назад — прежняя
s1.SetProjectPath(projB);
Check(s1.SessionId is null, "у нового проекта темы сессии нет");
s1.SetProjectPath(projA);
Check(s1.SessionId == "reg-1", "вернулись к сессии темы");

// Очистка списка проекта снимает активные сессии и ленты, и тем
sf.RegisterSession(projA, "лента", "feed-reg");
var removed = sf.ForgetSessions();
Check(removed >= 1 && sf.SessionId is null && s1.SessionId == "reg-1", "очистка в ленте не трогает активную сессию темы");
Check(store.Session("reg-1") is not null && store.Session("feed-reg") is null, "запись активной в теме сессии осталась");

// Правила «всегда» — по проекту чата
store.AddAlwaysAllow(projA, "Bash(ls)");
Check(s1.AlwaysAllowRules().Count == 1 && s2.AlwaysAllowRules().Count == 0, "правила — проекта темы");

// Запуски по чатам и старое поле
store.BeginRun(new ActiveRun { ChatKey = t1.Key, Prompt = "a" });
store.BeginRun(new ActiveRun { ChatKey = feed.Key, Prompt = "b" });

// Переживает перезапуск
var reloaded = new SessionStore(options, NullLogger<SessionStore>.Instance);
Check(reloaded.For(t1).EffectiveModel == "fable" && ProjectCatalog.Same(reloaded.For(t1).ProjectPath, projA), "темы пережили перезапуск");
Check(reloaded.For(t2).IsTalk && !reloaded.For(t2).IsNewTopic, "беседа пережила перезапуск");
var interrupted = reloaded.TakeInterruptedRuns();
Check(interrupted.Count == 2, "прерванные запуски всех чатов");
Check(reloaded.TakeInterruptedRuns().Count == 0, "прерванные снимаются");

// Старый state.json с одним ActiveRun
var legacyDir = Path.Combine(root, "legacy");
Directory.CreateDirectory(legacyDir);
File.WriteAllText(Path.Combine(legacyDir, "state.json"), """{"ActiveRun":{"ChatKey":"telegram:100","Prompt":"старое"},"ActiveSessions":{"C:\\x":"s"},"Topics":{"telegram:100/5":{}}}""");
AppPaths.UseDirectory(legacyDir);
var legacy = new SessionStore(options, NullLogger<SessionStore>.Instance);
Check(legacy.TakeInterruptedRuns() is [{ Prompt: "старое" }], "старое поле ActiveRun читается");
Check(!legacy.For(new TelegramChatId(100, 5)).IsNewTopic, "запись темы прошлой версии — тип выбран");
AppPaths.UseDirectory(Path.Combine(root, "data"));

// Папки бесед
Check(!SessionStore.IsTalkFolder(SessionStore.TalkRoot), "корень бесед — не папка беседы");
Check(!SessionStore.IsTalkFolder(Path.Combine(SessionStore.TalkRoot, "..")), "выход из корня — не папка беседы");
Check(!SessionStore.IsTalkFolder(SessionStore.TalkRoot + "-evil"), "соседняя папка — не папка беседы");
Check(SessionStore.IsTalkFolder(Path.Combine(SessionStore.TalkRoot, "x")), "папка внутри корня — беседа");

// --- воркер ---
store.TakeInterruptedRuns();
var channel = new FakeChannel();
var backend = new FakeBackend();
var broker = new ApprovalBroker(channel, options, NullLogger<ApprovalBroker>.Instance);
var audit = new NullAudit();
var inbox = new AttachmentInbox(channel, store, audit, options, NullLogger<AttachmentInbox>.Instance);
var trust = new ProjectTrustGate(backend, store, broker, channel, audit, NullLogger<ProjectTrustGate>.Instance);
var monitor = new RunMonitor();
var worker = new ChatWorker(channel, backend, new NoAgentLimits(), broker, inbox, trust, store,
    new GitRepository(NullLogger<GitRepository>.Instance), options, audit, monitor, NullLogger<ChatWorker>.Instance);
using var cts = new CancellationTokenSource();
await worker.StartAsync(cts.Token);

var user = new TelegramUserId(100);
var t3 = new TelegramChatId(100, 9);
store.For(t3).SetProjectPath(projB);

// Две темы на разных проектах — параллельно
backend.Hold = true;
worker.Enqueue(t1, user, "задача A");
worker.Enqueue(t3, user, "задача B");
await Until(() => backend.Running.Count == 2, "две темы на разных проектах идут разом");
Check(worker.IsBusy(t1) && worker.IsBusy(t3) && !worker.IsBusy(feed), "IsBusy по чату");
Check(store.Snapshot().ActiveRuns.Count == 2, "ActiveRuns на каждый чат");
Check(monitor.Current.Run is not null, "монитор показывает запуск");

// Лента на том же проекте, что тема 1, — ждёт замка
worker.Enqueue(feed, user, "задача ленты");
await Until(() => channel.Sent.Any(s => s.Chat == feed && s.Text.Contains("В проекте идёт другой запуск")), "лента ждёт замок проекта и говорит об этом");
Check(backend.Running.Count == 2, "третий запуск не начался");
Check(worker.IsBusy(feed), "ждущая замка лента — занята");

// /stop в теме 3 — только она
Check(worker.Stop(t3), "/stop в теме вернул true");
await Until(() => backend.Running.Count == 1, "остановилась только тема 3");
Check(backend.Running.ContainsKey(t1.Key), "тема 1 идёт дальше");

// Конец темы 1 отпускает замок — лента стартует
backend.Release(t1.Key);
await Until(() => backend.Running.ContainsKey(feed.Key), "после темы 1 стартовала лента");
backend.Release(feed.Key);
await Until(() => backend.Running.IsEmpty && !worker.IsBusy(feed), "лента закончилась");

// /stop в ленте останавливает всё
worker.Enqueue(t1, user, "ещё A");
worker.Enqueue(t3, user, "ещё B");
await Until(() => backend.Running.Count == 2, "снова две темы");
Check(worker.Stop(feed), "/stop в ленте вернул true");
await Until(() => backend.Running.IsEmpty, "/stop в ленте остановил все темы");

// Беседа: WebOnly, без картинок, своя пустая папка
backend.Hold = false;
var talkFolder = store.For(t2).ProjectPath;
Directory.CreateDirectory(Path.Combine(talkFolder, ".claude"));
File.WriteAllText(Path.Combine(talkFolder, ".claude", "settings.json"), "{}");
worker.Enqueue(t2, user, "привет, беседа");
await Until(() => backend.Requests.Any(r => r.Prompt == "привет, беседа"), "беседа запустилась");
var talkRun = backend.Requests.Last(r => r.Prompt == "привет, беседа");
Check(talkRun.WebOnly && talkRun.AttachmentsPath is null && talkRun.Model == "haiku", "беседа: WebOnly, без вложений, модель темы");
Check(ProjectCatalog.Same(talkRun.ProjectPath, talkFolder) && !File.Exists(Path.Combine(talkFolder, ".claude", "settings.json")), "папка беседы очищена до запуска");
await Until(() => store.For(t2).SessionId is not null, "у беседы своя сессия");

worker.Enqueue(t1, user, "задача в проекте");
await Until(() => backend.Requests.Any(r => r.Prompt == "задача в проекте"), "задача в проекте запустилась");
var projRun = backend.Requests.Last(r => r.Prompt == "задача в проекте");
Check(!projRun.WebOnly && projRun.AttachmentsPath is not null && projRun.Model == "fable", "проект: не WebOnly, модель темы");

// Ответы ушли в свои темы
await Until(() => channel.Sent.Any(s => s.Chat == t2 && s.Text.Contains("ответ на «привет, беседа»")), "ответ беседы — в её тему");

// Хук, положенный соседом, пока тема ждала замок, — снова карточка доверия, а не запуск
var t4 = new TelegramChatId(100, 10);
store.For(t4).SetProjectPath(projB);
backend.Hold = true;
worker.Enqueue(t3, user, "h1-сосед");
await Until(() => backend.Running.ContainsKey("h1-сосед"), "сосед по проекту идёт");
worker.Enqueue(t4, user, "h1-ждёт");
await Until(() => channel.Sent.Any(s => s.Chat == t4 && s.Text.Contains("В проекте идёт другой запуск")), "тема ждёт замок соседа");
Directory.CreateDirectory(Path.Combine(projB, ".claude"));
File.WriteAllText(Path.Combine(projB, ".claude", "settings.json"), "{}");
backend.Release("h1-сосед");
await Until(() => channel.Sent.Any(s => s.Chat == t4 && s.Text.Contains("Доверять проекту")), "после замка — карточка доверия к новому хуку");
Check(!backend.Requests.Any(r => r.Prompt == "h1-ждёт"), "без доверия запуск не начался");
Check(worker.Stop(t4), "/stop снимает карточку доверия");
await Until(() => !worker.IsBusy(t4), "тема освободилась");
Check(!backend.Requests.Any(r => r.Prompt == "h1-ждёт"), "после /stop запуска не было");
File.Delete(Path.Combine(projB, ".claude", "settings.json"));

// /new, пока тема ждала замок, — запуск идёт новой сессией
store.For(t4).SetSessionId("old-t4");
worker.Enqueue(t3, user, "m2-сосед");
await Until(() => backend.Running.ContainsKey("m2-сосед"), "сосед снова идёт");
var waitsBefore = channel.Sent.Count(s => s.Chat == t4 && s.Text.Contains("В проекте идёт другой запуск"));
worker.Enqueue(t4, user, "m2-ждёт");
await Until(() => channel.Sent.Count(s => s.Chat == t4 && s.Text.Contains("В проекте идёт другой запуск")) > waitsBefore, "тема снова ждёт замок");
store.For(t4).SetSessionId(null);
backend.Release("m2-сосед");
await Until(() => backend.Running.ContainsKey("m2-ждёт"), "после замка тема запустилась");
Check(backend.Requests.Last(r => r.Prompt == "m2-ждёт").ResumeSessionId is null, "запуск после /new — новой сессией");
backend.Release("m2-ждёт");
await Until(() => !worker.IsBusy(t4), "тема закончила");

// Удалённая тема: статус не дошёл — запуска нет, очередь темы снята, «внутренней ошибки» нет
backend.Hold = false;
var t5 = new TelegramChatId(100, 11);
store.For(t5).SetProjectPath(projA);
channel.Dead = t5;
var requestsBefore = backend.Requests.Count();
worker.Enqueue(t5, user, "в удалённую 1");
worker.Enqueue(t5, user, "в удалённую 2");
await Until(() => worker.QueueLength(t5) == 0 && !worker.IsBusy(t5), "удалённая тема разобрана");
await Task.Delay(200);
Check(backend.Requests.Count() == requestsBefore, "в удалённую тему запусков не было");
Check(channel.DeadAttempts == 1, "в удалённую тему — одна попытка: ни «внутренней ошибки», ни второй задачи");
channel.Dead = null;
Check(monitor.Current.Queue.Count == 0, "очередь монитора пуста, когда все полосы разобраны");

await cts.CancelAsync();
try { await worker.StopAsync(CancellationToken.None); } catch { }

Console.WriteLine(failures == 0 ? "ВСЁ ХОРОШО" : $"ОШИБОК: {failures}");
return failures == 0 ? 0 : 1;

async Task Until(Func<bool> condition, string what)
{
    for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
    Check(condition(), what);
}

sealed class FakeBackend : IAgentBackend
{
    public bool Hold;
    public readonly ConcurrentDictionary<string, TaskCompletionSource> Running = new();
    public readonly ConcurrentQueue<AgentRunRequest> RequestQueue = new();
    public IEnumerable<AgentRunRequest> Requests => RequestQueue;
    private readonly ConcurrentDictionary<string, string> _chatOfRun = new();

    public string Id => "fake";
    public string DisplayName => "Fake";
    public AgentCapabilities Capabilities => ClaudeCapabilities.Instance;
    public AgentProbe Probe() => new("fake", "1");
    public ProjectSettings? InspectProject(string projectPath) =>
        File.Exists(Path.Combine(projectPath, ".claude", "settings.json")) ? new ProjectSettings("fp000000", ["x"]) : null;

    public void Release(string chatKey) { if (Running.TryRemove(chatKey, out var t)) t.TrySetResult(); }

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, IAgentRunObserver observer, CancellationToken ct)
    {
        RequestQueue.Enqueue(request);
        observer.SessionStarted(request.ResumeSessionId ?? request.NewSessionId);
        var key = ChatKeyOf(request);
        if (Hold)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Running[key] = gate;
            try { await gate.Task.WaitAsync(ct); }
            catch (OperationCanceledException)
            {
                Running.TryRemove(key, out _);
                return new AgentRunResult { Ok = false, Cancelled = true, Text = "остановлено" };
            }
        }

        return new AgentRunResult { Ok = true, Text = $"ответ на «{request.Prompt}»", SessionId = request.ResumeSessionId ?? request.NewSessionId };
    }

    // Чат запуска — по тексту задачи: харнесу хватает.
    private static string ChatKeyOf(AgentRunRequest r) => r.Prompt switch
    {
        "задача A" or "ещё A" => "telegram:100/7",
        "задача B" or "ещё B" => "telegram:100/9",
        "задача ленты" => "telegram:100",
        _ => r.Prompt,
    };
}

sealed class FakeChannel : IChatChannel
{
    public readonly ConcurrentQueue<(ChatId Chat, string Text)> SentQueue = new();
    public IEnumerable<(ChatId Chat, string Text)> Sent => SentQueue;
    private int _id;

    public string Id => "telegram";
    public string DisplayName => "Fake";
    public ChannelLimits Limits { get; } = new(3800, 64, 64, 50_000_000, 10_000_000, 1000, 20_000_000);
    public IReadOnlyCollection<UserId> AllowedUsers { get; } = [new TelegramUserId(100)];
    public ChatId? DirectChat(UserId user) => new TelegramChatId(((TelegramUserId)user).Value);
    public ChatId? ParseChat(string key) => TelegramChatId.TryParse(key, out var c) ? c : null;
    public IReadOnlyList<string> Validate() => [];
    public Task<string> ConnectAsync(CancellationToken ct) => Task.FromResult("@fake");
    public Task PublishCommandsAsync(IReadOnlyList<ChatCommand> commands, CancellationToken ct) => Task.CompletedTask;
    public Task ListenAsync(IChatInbound sink, CancellationToken ct) => Task.CompletedTask;

    public ChatId? Dead;
    public int DeadAttempts;

    public Task<MessageRef> SendAsync(ChatId chat, OutgoingMessage message, CancellationToken ct)
    {
        if (chat == Dead && Interlocked.Increment(ref DeadAttempts) > 0) throw new ChannelRequestException("message thread not found", ChannelFailure.CannotReach);
        SentQueue.Enqueue((chat, message.Text));
        return Task.FromResult(new MessageRef(chat, Interlocked.Increment(ref _id).ToString()));
    }

    public Task<MessageRef> SendFileAsync(ChatId chat, string fileName, string text, MessageRef? replyTo, CancellationToken ct) => SendAsync(chat, new(text), ct);
    public Task<MessageRef> SendDocumentAsync(ChatId chat, string fileName, Stream content, string? caption, MessageRef? replyTo, CancellationToken ct) => SendAsync(chat, new(fileName), ct);
    public Task<MessageRef> SendPhotoAsync(ChatId chat, string fileName, Stream content, string? caption, MessageRef? replyTo, CancellationToken ct) => SendAsync(chat, new(fileName), ct);
    public Task DownloadAttachmentAsync(IncomingAttachment attachment, Stream destination, CancellationToken ct) => throw new NotSupportedException();
    public Task EditAsync(MessageRef message, OutgoingMessage content, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteAsync(MessageRef message, CancellationToken ct) => Task.CompletedTask;
    public Task AcknowledgeAsync(ButtonPress press, string? toast, CancellationToken ct) => Task.CompletedTask;
    public Task IndicateTypingAsync(ChatId chat, CancellationToken ct) => Task.CompletedTask;
}

sealed class NullAudit : IAuditLog
{
    public void Write(AuditEvent entry) { }
    public IReadOnlyList<AuditEvent> Tail(int count) => [];
}
