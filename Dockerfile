# Шлюз и агент живут в одном контейнере: агент запускается как дочерний процесс, а
# подтверждения ходят по loopback внутри него.
#
# Что умеет агент внутри — ровно то, что стоит в этом образе. Здесь минимум: git и .NET SDK
# для проектов на C#. Нужны другие языки — добавляйте слой, это осознанный выбор, а не
# недостаток образа.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала только csproj: слой с restore переживает правку кода. Новый проект в src/ надо
# добавить и сюда — без его csproj restore падает на ссылке из Gateway.csproj.
COPY AgentsTracker.slnx ./
COPY src/AgentsTracker.Agents.Abstractions/*.csproj src/AgentsTracker.Agents.Abstractions/
COPY src/AgentsTracker.Agents.Claude/*.csproj src/AgentsTracker.Agents.Claude/
COPY src/AgentsTracker.Channels.Abstractions/*.csproj src/AgentsTracker.Channels.Abstractions/
COPY src/AgentsTracker.Channels.Telegram/*.csproj src/AgentsTracker.Channels.Telegram/
COPY src/AgentsTracker.Gateway/*.csproj src/AgentsTracker.Gateway/
RUN dotnet restore src/AgentsTracker.Gateway/AgentsTracker.Gateway.csproj

COPY src/ src/
RUN dotnet publish src/AgentsTracker.Gateway/AgentsTracker.Gateway.csproj -c Release -o /app --no-restore \
    && rm -f /app/appsettings.Local.json

# SDK, а не aspnet: агенту нужен инструмент для проектов, которые он собирает.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# curl — для установщика агента, git — чтобы агент видел историю и делал коммиты,
# ripgrep — им пользуется поиск внутри Claude Code.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl git ripgrep ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    # Тома монтируются от имени app: создаём точки заранее, иначе Docker сделает их от root
    # и шлюз не сможет писать ни состояние, ни логин агента.
    && mkdir -p /data /projects \
    && chown app:app /data /projects

# Непривилегированный пользователь app (uid 1654) есть в самом образе Microsoft — заводить
# своего не нужно, достаточно отдать ему папки. Агент правит только смонтированное.
USER app
ENV HOME=/home/app

# Инсталлятор кладёт бинарник в ~/.local/bin — там его находит ClaudeCliLocator.
RUN curl -fsSL https://claude.ai/install.sh | bash
ENV PATH="$HOME/.local/bin:$PATH"

COPY --from=build --chown=app:app /app /app
WORKDIR /app

# Тома: данные шлюза и домашняя папка агента с логином. Проекты монтируются в /projects.
ENV Gateway__DataDirectory=/data \
    Gateway__MonitorBind=any \
    Gateway__ProjectsRoot=/projects \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 5100
ENTRYPOINT ["/app/AgentsTracker.Gateway"]
