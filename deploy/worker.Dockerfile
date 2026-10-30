# Образ воркера. Контекст сборки — корень репозитория:
#   docker build -f deploy/worker.Dockerfile --build-arg WORKER=Notifications .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG WORKER
WORKDIR /repo

# Сначала только файлы проектов — слой restore кэшируется, пока не меняются зависимости.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/MedSlot.Contracts/MedSlot.Contracts.csproj src/MedSlot.Contracts/
COPY src/MedSlot.Workers.Common/MedSlot.Workers.Common.csproj src/MedSlot.Workers.Common/
COPY src/MedSlot.Workers.${WORKER}/MedSlot.Workers.${WORKER}.csproj src/MedSlot.Workers.${WORKER}/
RUN dotnet restore src/MedSlot.Workers.${WORKER}/MedSlot.Workers.${WORKER}.csproj

COPY src/MedSlot.Contracts/ src/MedSlot.Contracts/
COPY src/MedSlot.Workers.Common/ src/MedSlot.Workers.Common/
COPY src/MedSlot.Workers.${WORKER}/ src/MedSlot.Workers.${WORKER}/
RUN dotnet publish src/MedSlot.Workers.${WORKER}/MedSlot.Workers.${WORKER}.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
ARG WORKER
ENV WORKER_ASSEMBLY=MedSlot.Workers.${WORKER}.dll
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["sh", "-c", "exec dotnet \"$WORKER_ASSEMBLY\""]
