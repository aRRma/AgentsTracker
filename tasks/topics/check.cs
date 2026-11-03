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
s2.SetPermissionMode("plan");
Check(s2.IsNewTopic && s2.PermissionMode == "plan", "/mode в новой теме запомнен");
s2.SetProjectPath(null);
Check(s2.IsTalk && !s2.IsNewTopic && s2.EffectiveModel == "haiku", "выбор «беседа» — тип выбран, модель осталась");
Check(s2.PermissionMode is null && s2.EffectivePermissionMode == "default", "режим беседы — из конфига, сохранённый plan не действует");
Check(store.Snapshot().Topics[t2.Key].PermissionMode is null, "выбор беседы стёр режим: проект потом не получит старый plan");

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
Check(sf.IsActiveElsewhere("reg-1") && !s1.IsActiveElsewhere("reg-1"), "сессия темы для ленты — активна в другом месте, для самой темы — нет");

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

// --- лог: файл и маска ---
var logDir = Path.Combine(root, "logs");
Directory.CreateDirectory(logDir);
var today = DateOnly.FromDateTime(DateTime.Now);
var oldLog = Path.Combine(logDir, $"gateway-{today.AddDays(-LogFile.KeptDays - 1):yyyy-MM-dd}.log");
var freshLog = Path.Combine(logDir, $"gateway-{today.AddDays(-3):yyyy-MM-dd}.log");
var foreignLog = Path.Combine(logDir, "gateway-old.log");
foreach (var f in new[] { oldLog, freshLog, foreignLog }) File.WriteAllText(f, "x");
var ring = new RingBufferLog();
using (var logProvider = new RingBufferLoggerProvider(ring, new LogFile(logDir)))
{
    var log = logProvider.CreateLogger("AgentsTracker.Gateway.Harness");
    const string botToken = "123456789:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    log.LogWarning("вывод CLI: token=supersecret42\nвторая строка");
    log.LogError(new InvalidOperationException($"сбой {botToken}"), "с исключением");
    log.LogDebug("отладка {Secret}", "debug-only");
}
var todayLog = Path.Combine(logDir, $"gateway-{today:yyyy-MM-dd}.log");
var logText = File.Exists(todayLog) ? File.ReadAllText(todayLog) : "";
var ringText = string.Join("\n", ring.Tail(10, LogLevel.Information).Select(e => e.Message + e.Exception));
Check(logText.Contains("WRN Harness: вывод CLI") && logText.Contains("ERR Harness: с исключением"), "лог: записи в файле дня, категория без префикса");
Check(!logText.Contains("supersecret42") && !logText.Contains("AAAAAAAAAAAAAAAAAAAA") && logText.Contains("***"), "лог: секрет в тексте и в исключении замаскирован в файле");
Check(!ringText.Contains("supersecret42") && !ringText.Contains("AAAAAAAAAAAAAAAAAAAA"), "лог: секрет замаскирован в кольце монитора");
Check(!logText.Contains("debug-only") && !ringText.Contains("debug-only"), "лог: Debug не пишется");
Check(logText.Contains("\n    вторая строка") && logText.Contains("\n    System.InvalidOperationException"), "лог: продолжение записи — с отступом");
Check(!File.Exists(oldLog) && File.Exists(freshLog) && File.Exists(foreignLog), "лог: старше срока удалён, свежий и чужой остались");

// --- воркер ---
store.TakeInterruptedRuns();
var channel = new FakeChannel();
var backend = new FakeBackend();
var broker = new ApprovalBroker(channel, options, NullLogger<ApprovalBroker>.Instance);
var audit = new NullAudit();
var inbox = new AttachmentInbox(channel, store, audit, options, NullLogger<AttachmentInbox>.Instance);
var trust = new ProjectTrustGate(backend, store, broker, channel, audit, NullLogger<ProjectTrustGate>.Instance);
var monitor = new RunMonitor();
var limits = new FakeLimits();
var worker = new ChatWorker(channel, backend, limits, broker, inbox, trust, store,
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

await Until(() => audit.Entries.Any(e => e.Kind == AuditKinds.RunEnd && e.ChatKey == t3.Key), "run.end темы 3 записан");
Check(audit.Entries.Last(e => e.Kind == AuditKinds.RunEnd && e.ChatKey == t3.Key) is { Outcome: "cancel" } stopEnd && stopEnd.Summary.EndsWith(", /stop"),
    "run.end после /stop: причина — /stop");

// Конец темы 1 отпускает замок — лента стартует; ждала дольше секунды — замок в run.start
await Task.Delay(1100);
backend.Release(t1.Key);
await Until(() => backend.Running.ContainsKey(feed.Key), "после темы 1 стартовала лента");
Check(audit.Entries.Last(e => e.Kind == AuditKinds.RunStart && e.ChatKey == feed.Key).Summary.Contains(", ждал другой запуск 1 с"), "run.start ленты: ожидание замка записано");
Check(!audit.Entries.Last(e => e.Kind == AuditKinds.RunStart && e.ChatKey == t1.Key).Summary.Contains("ждал"), "run.start без ожидания: ожидания нет");
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

// Картинка без подписи в беседе: отказ в чат и в журнал, запуска нет
var enqueue = new ChatEnqueueTextHandler(channel, worker, inbox, store, audit);
var talkRuns = backend.Requests.Count();
await enqueue.TryHandleAsync(new IncomingMessage(t2, user, "", ChatKind.Direct, [new IncomingAttachment("f1", AttachmentKind.Photo, null, null, 10)]), CancellationToken.None);
Check(channel.Sent.Any(s => s.Chat == t2 && s.Text.Contains("Беседа не принимает картинки")), "беседа: отказ картинке — в чат");
Check(audit.Entries.Any(e => e.Kind == AuditKinds.FileReceive && e.ChatKey == t2.Key && e.Outcome == "refused"), "беседа: отказ картинке — в журнал");
await Task.Delay(200);
Check(backend.Requests.Count() == talkRuns, "беседа: картинка без подписи не запускает агента");

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

// «⏳» не удалилось (сбой сети) — замок папки всё равно не теряется
worker.Enqueue(t3, user, "del-сосед");
await Until(() => backend.Running.ContainsKey("del-сосед"), "сосед держит папку");
worker.Enqueue(t4, user, "del-ждёт");
await Until(() => worker.IsBusy(t4) && !backend.Running.ContainsKey("del-ждёт"), "тема ждёт замок");
channel.FailDelete = true;
backend.Release("del-сосед");
await Until(() => backend.Running.ContainsKey("del-ждёт"), "после сбоя удаления «⏳» тема запустилась");
await Until(() => channel.Sent.Any(s => s.Chat == t3 && s.Text.Contains("ответ на «del-сосед»")), "сбой удаления статуса не теряет ответ");
backend.Release("del-ждёт");
channel.FailDelete = false;
worker.Enqueue(t3, user, "del-после");
await Until(() => backend.Running.ContainsKey("del-после"), "папка свободна: следующий запуск начался");
backend.Release("del-после");
await Until(() => !worker.IsBusy(t3), "сосед закончил");

// Монитор снимает из очереди превью взятой задачи, а не первое в списке
worker.Enqueue(t1, user, "mon-x");
await Until(() => backend.Running.ContainsKey("mon-x"), "тема 1 занята");
worker.Enqueue(t1, user, "mon-a");
worker.Enqueue(t3, user, "mon-b");
await Until(() => backend.Running.ContainsKey("mon-b"), "тема 3 взяла свою задачу");
Check(monitor.Current.Queue is ["mon-a"], "в очереди монитора — ждущая задача темы 1, а не взятая темы 3");
backend.Release("mon-x");
await Until(() => backend.Running.ContainsKey("mon-a"), "тема 1 взяла следующую");
backend.Release("mon-a");
backend.Release("mon-b");
await Until(() => !worker.IsBusy(t1) && !worker.IsBusy(t3), "темы 1 и 3 закончили");

// /stop во время проверки лимита — процесс не запускается
limits.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
limits.Entered = false;
worker.Enqueue(t3, user, "lim-ждёт");
await Until(() => limits.Entered, "запуск проверяет лимит");
Check(worker.Stop(t3), "/stop во время проверки лимита вернул true");
var gateOpen = limits.Gate;
limits.Gate = null;
gateOpen.SetResult();
await Until(() => !worker.IsBusy(t3), "тема освободилась после проверки лимита");
Check(!backend.Requests.Any(r => r.Prompt == "lim-ждёт"), "после /stop во время проверки лимита запуска не было");

// Сессия в worktree, /new во время ожидания его замка — запуск идёт новой сессией в проекте
var projW = Directory.CreateDirectory(Path.Combine(root, "projW")).FullName;
var t6 = new TelegramChatId(100, 12);
var t7 = new TelegramChatId(100, 13);
store.For(t6).SetProjectPath(projB);
store.For(t6).RegisterSession(projB, "w", "w-sess");
store.RecordWorkingDirectory("w-sess", projW);
store.For(t7).SetProjectPath(projW);
Check(ProjectCatalog.Same(store.SessionFolder(projB, "w-sess"), projW), "сессия темы 6 — в worktree");
worker.Enqueue(t7, user, "w-сосед");
await Until(() => backend.Running.ContainsKey("w-сосед"), "сосед держит worktree");
worker.Enqueue(t6, user, "w-ждёт");
await Until(() => channel.Sent.Any(s => s.Chat == t6 && s.Text.Contains("В проекте идёт другой запуск")), "тема 6 ждёт замок worktree");
store.For(t6).SetSessionId(null);
backend.Release("w-сосед");
await Until(() => backend.Running.ContainsKey("w-ждёт"), "тема 6 запустилась");
Check(backend.Requests.Last(r => r.Prompt == "w-ждёт").ResumeSessionId is null, "после /new — новая сессия, а не старая из worktree");
backend.Release("w-ждёт");
await Until(() => !worker.IsBusy(t6), "тема 6 закончила");

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
Check(audit.Entries.Any(e => e.Kind == AuditKinds.Delivery && e.ChatKey == t5.Key && e.Outcome == "unreachable" && e.Summary.EndsWith("снято из очереди: 1")),
    "удалённая тема — в журнал: delivery unreachable и сколько снято");
channel.Dead = null;
Check(monitor.Current.Queue.Count == 0, "очередь монитора пуста, когда все полосы разобраны");

// Удалённая тема с недоверенным проектом: отказ на карточке доверия — тоже одна попытка, очередь снята
var projX = Directory.CreateDirectory(Path.Combine(root, "projX")).FullName;
Directory.CreateDirectory(Path.Combine(projX, ".claude"));
File.WriteAllText(Path.Combine(projX, ".claude", "settings.json"), "{}");
var t8 = new TelegramChatId(100, 14);
store.For(t8).SetProjectPath(projX);
channel.Dead = t8;
channel.DeadAttempts = 0;
worker.Enqueue(t8, user, "в удалённую с карточкой 1");
worker.Enqueue(t8, user, "в удалённую с карточкой 2");
await Until(() => worker.QueueLength(t8) == 0 && !worker.IsBusy(t8), "удалённая тема с карточкой разобрана");
await Task.Delay(200);
Check(channel.DeadAttempts == 1 && !backend.Requests.Any(r => r.Prompt.StartsWith("в удалённую с карточкой")),
    "карточка доверия в удалённую тему: одна попытка, запусков нет");
channel.Dead = null;

// Вторая карточка доверия: её время вышло, но доверие уже дали по соседней — запуск идёт
var projT = Directory.CreateDirectory(Path.Combine(root, "projT")).FullName;
Directory.CreateDirectory(Path.Combine(projT, ".claude"));
File.WriteAllText(Path.Combine(projT, ".claude", "settings.json"), "{}");
var quickOptions = Options.Create(new GatewayOptions { ProjectPath = projA, PermissionMode = "default", ApprovalTimeoutMinutes = 0 });
var quickChannel = new FakeChannel { OnSend = text => { if (text.Contains("Доверять проекту")) store.TrustProject(projT, "fp000000"); } };
var quickBroker = new ApprovalBroker(quickChannel, quickOptions, NullLogger<ApprovalBroker>.Instance);
var quickTrust = new ProjectTrustGate(backend, store, quickBroker, quickChannel, audit, NullLogger<ProjectTrustGate>.Instance);
Check(await quickTrust.EnsureAsync(t1, null, projT, CancellationToken.None) == ProjectTrustGate.Outcome.Trusted,
    "время второй карточки вышло, но доверие уже дали по соседней — проект доверен");

// run.end: расход, причина ошибки и отмены; внутренняя ошибка и лимит — в журнал
backend.Hold = false;
async Task<AuditEvent> EndOf(string prompt)
{
    var ends = audit.Entries.Count(e => e.Kind == AuditKinds.RunEnd && e.ChatKey == t1.Key);
    worker.Enqueue(t1, user, prompt);
    await Until(() => audit.Entries.Count(e => e.Kind == AuditKinds.RunEnd && e.ChatKey == t1.Key) > ends, $"run.end «{prompt}» записан");
    return audit.Entries.Last(e => e.Kind == AuditKinds.RunEnd && e.ChatKey == t1.Key);
}
var spentEnd = await EndOf("с расходом");
Check(spentEnd.Outcome == "ok" && spentEnd.Summary.Contains("ходов 5, токенов 12.3k/1.8k, кэш 45.6k/7.1k, opus-5-5"), "run.end: ходы, токены, кэш и ответившая модель");
var timeoutEnd = await EndOf("таймаут");
Check(timeoutEnd.Outcome == "cancel" && timeoutEnd.Summary.EndsWith(", предел времени"), "run.end: отмена без /stop — предел времени");
var errorEnd = await EndOf("ошибка");
Check(errorEnd.Outcome == "error" && errorEnd.Summary.Contains("claude завершился с кодом 1") && !errorEnd.Summary.Contains("abc12345secret"),
    "run.end: причина ошибки, секрет замаскирован");
var gatewayErrors = audit.Entries.Count(e => e.Kind == AuditKinds.Gateway && e.Outcome == "error");
worker.Enqueue(t1, user, "бросок");
await Until(() => audit.Entries.Count(e => e.Kind == AuditKinds.Gateway && e.Outcome == "error") > gatewayErrors, "внутренняя ошибка — в журнал");
Check(audit.Entries.Last(e => e.Kind == AuditKinds.Gateway && e.Outcome == "error").Summary.Contains("InvalidOperationException: сломалось"), "внутренняя ошибка: тип и сообщение");
await Until(() => channel.Sent.Any(s => s.Chat == t1 && s.Text.StartsWith("Внутренняя ошибка шлюза")), "внутренняя ошибка — в чат");
Check(!channel.Sent.Last(s => s.Chat == t1 && s.Text.StartsWith("Внутренняя ошибка шлюза")).Text.Contains("abc12345secret"), "внутренняя ошибка в чате: секрет замаскирован");
limits.Refusal = "🚦 Лимит тарифа исчерпан: окно «5 часов» выбрано полностью. Сброс в 18:00.\n\nЗадача не запущена — кредиты шлюз не тратит.";
var refusals = audit.Entries.Count(e => e.Kind == AuditKinds.LimitRefused);
worker.Enqueue(t1, user, "на лимите");
await Until(() => audit.Entries.Count(e => e.Kind == AuditKinds.LimitRefused) > refusals, "отказ по лимиту записан");
limits.Refusal = null;
var refusal = audit.Entries.Last(e => e.Kind == AuditKinds.LimitRefused).Summary;
Check(refusal.Contains("fable") && refusal.Contains("окно «5 часов»") && !refusal.Contains("кредиты"), "limit.refused: модель и окно, без абзаца про кредиты");
await Until(() => !worker.IsBusy(t1), "тема 1 свободна");

// Ответ не дошёл до чата — run.end «ok», но в журнале есть delivery
channel.FailText = "ответ на «не-дойдёт»";
await EndOf("не-дойдёт");
await Until(() => audit.Entries.Any(e => e.Kind == AuditKinds.Delivery && e.ChatKey == t1.Key), "недоставка ответа — в журнал");
channel.FailText = null;
Check(audit.Entries.Last(e => e.Kind == AuditKinds.Delivery && e.ChatKey == t1.Key) is { Outcome: "lost" } lost && lost.Summary == "ответ: частей не дошло 1 из 1",
    "delivery: сколько частей не дошло, исход lost");
await EndOf("дойдёт");
await Task.Delay(100);
Check(audit.Entries.Count(e => e.Kind == AuditKinds.Delivery && e.ChatKey == t1.Key) == 1, "дошедший ответ delivery не пишет");

// Остановка шлюза посреди запуска (docker stop, Ctrl+C): запуск остаётся в state.json — следующий
// старт скажет «прервано» и допишет run.end; своего run.end нет, второго не будет
backend.Hold = true;
worker.Enqueue(t1, user, "стоп-шлюза");
await Until(() => backend.Running.ContainsKey("стоп-шлюза"), "запуск идёт перед остановкой шлюза");
var runEndsBefore = audit.Entries.Count(e => e.Kind == AuditKinds.RunEnd);
await cts.CancelAsync();
try { await worker.StopAsync(CancellationToken.None); } catch { }
Check(store.Snapshot().ActiveRuns.ContainsKey(t1.Key), "остановка шлюза: запуск остался в state.json для «прервано»");
Check(audit.Entries.Count(e => e.Kind == AuditKinds.RunEnd) == runEndsBefore, "остановка шлюза: своего run.end нет — его напишет следующий старт");
Check(new SessionStore(options, NullLogger<SessionStore>.Instance).TakeInterruptedRuns().Any(r => r.Prompt == "стоп-шлюза"), "остановка шлюза: следующий экземпляр видит прерванный запуск");

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
        switch (request.Prompt)
        {
            case "таймаут": return new AgentRunResult { Ok = false, Cancelled = true, Text = "Превышен предел" };
            case "ошибка": return new AgentRunResult { Ok = false, Text = "claude завершился с кодом 1: token=abc12345secret" };
            case "бросок": throw new InvalidOperationException("сломалось token=abc12345secret");
            case "с расходом":
                return new AgentRunResult
                {
                    Ok = true, Text = "готово", SessionId = request.ResumeSessionId ?? request.NewSessionId,
                    Usage = new RunUsage { Turns = 5, InputTokens = 12_300, OutputTokens = 1_800, CacheReadTokens = 45_600, CacheWriteTokens = 7_100, PrimaryModel = "opus-5-5" },
                };
        }
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
    public Action<string>? OnSend;

    public volatile string? FailText;

    public Task<MessageRef> SendAsync(ChatId chat, OutgoingMessage message, CancellationToken ct)
    {
        if (chat == Dead && Interlocked.Increment(ref DeadAttempts) > 0) throw new ChannelRequestException("message thread not found", ChannelFailure.CannotReach);
        if (FailText is { } fail && message.Text.Contains(fail)) throw new ChannelRequestException("Bad Request", ChannelFailure.Unknown);
        OnSend?.Invoke(message.Text);
        SentQueue.Enqueue((chat, message.Text));
        return Task.FromResult(new MessageRef(chat, Interlocked.Increment(ref _id).ToString()));
    }

    public Task<MessageRef> SendFileAsync(ChatId chat, string fileName, string text, MessageRef? replyTo, CancellationToken ct) => SendAsync(chat, new(text), ct);
    public Task<MessageRef> SendDocumentAsync(ChatId chat, string fileName, Stream content, string? caption, MessageRef? replyTo, CancellationToken ct) => SendAsync(chat, new(fileName), ct);
    public Task<MessageRef> SendPhotoAsync(ChatId chat, string fileName, Stream content, string? caption, MessageRef? replyTo, CancellationToken ct) => SendAsync(chat, new(fileName), ct);
    public Task DownloadAttachmentAsync(IncomingAttachment attachment, Stream destination, CancellationToken ct) => throw new NotSupportedException();
    public Task EditAsync(MessageRef message, OutgoingMessage content, CancellationToken ct) => Task.CompletedTask;
    public volatile bool FailDelete;
    // Отменённый токен — отмена, как у HttpClient под Telegram.Bot: так ведёт себя остановка шлюза.
    public Task DeleteAsync(MessageRef message, CancellationToken ct) =>
        ct.IsCancellationRequested ? Task.FromCanceled(ct)
        : FailDelete ? Task.FromException(new HttpRequestException("сеть")) : Task.CompletedTask;
    public Task AcknowledgeAsync(ButtonPress press, string? toast, CancellationToken ct) => Task.CompletedTask;
    public Task IndicateTypingAsync(ChatId chat, CancellationToken ct) => Task.CompletedTask;
}

sealed class FakeLimits : IAgentLimits
{
    private readonly NoAgentLimits _none = new();

    /// <summary>Не null — проверка лимита ждёт, пока харнес её не отпустит.</summary>
    public volatile TaskCompletionSource? Gate;
    public volatile bool Entered;
    public volatile string? Refusal;

    public async Task<string?> RefusalAsync(string? model, CancellationToken ct)
    {
        if (Gate is { } gate)
        {
            Entered = true;
            await gate.Task;
        }

        return Refusal;
    }

    public Task<LimitsSnapshot> GetAsync(CancellationToken ct) => _none.GetAsync(ct);
    public Task<string> ShortSummaryAsync(string? model, CancellationToken ct) => _none.ShortSummaryAsync(model, ct);
    public Task<LimitsView> ViewAsync(string? model, CancellationToken ct) => _none.ViewAsync(model, ct);
}

sealed class NullAudit : IAuditLog
{
    public readonly ConcurrentQueue<AuditEvent> Entries = new();
    public void Write(AuditEvent entry) => Entries.Enqueue(entry);
    public IReadOnlyList<AuditEvent> Tail(int count) => [];
}
