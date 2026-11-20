# Контекст сборки — корень репозитория (#130): веб (web/) собирается шагом Node, его результат кладётся в wwwroot API.
# Сборка: из server/ — docker compose build (context: ..), вручную — docker build -f server/Dockerfile . из корня.
# Что не попадает в контекст — .dockerignore в корне

# Веб: esbuild из зависимостей корня (package-lock.json), браузеры Playwright не нужны
FROM node:22-alpine AS web
WORKDIR /repo
COPY package.json package-lock.json ./
RUN PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 npm ci --no-audit --no-fund
COPY scripts/build-web.mjs scripts/
COPY web/ web/
RUN npm run build:web -- --out /web

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY server/nuget.config server/HhAutoApplier.slnx server/Directory.Build.props ./
COPY server/src/HhAutoApplier.Domain/*.csproj src/HhAutoApplier.Domain/
COPY server/src/HhAutoApplier.Application/*.csproj src/HhAutoApplier.Application/
COPY server/src/HhAutoApplier.Infrastructure/*.csproj src/HhAutoApplier.Infrastructure/
COPY server/src/HhAutoApplier.Api/*.csproj src/HhAutoApplier.Api/
RUN dotnet restore src/HhAutoApplier.Api/HhAutoApplier.Api.csproj
COPY server/src/ src/
COPY --from=web /web/ src/HhAutoApplier.Api/wwwroot/
# Коммит сборки попадает в GET /api/version: в образе нет .git
ARG GIT_COMMIT=unknown
RUN dotnet publish src/HhAutoApplier.Api/HhAutoApplier.Api.csproj -c Release -o /app --no-restore -p:SourceRevisionId=$GIT_COMMIT

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "HhAutoApplier.Api.dll"]
