# Контекст сборки — корень репозитория: docker build -f src/MedSlot.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo

# Сначала только файлы проектов — слой restore кэшируется, пока не меняются зависимости.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/MedSlot.Domain/MedSlot.Domain.csproj src/MedSlot.Domain/
COPY src/MedSlot.Application/MedSlot.Application.csproj src/MedSlot.Application/
COPY src/MedSlot.Infrastructure/MedSlot.Infrastructure.csproj src/MedSlot.Infrastructure/
COPY src/MedSlot.Api/MedSlot.Api.csproj src/MedSlot.Api/
RUN dotnet restore src/MedSlot.Api/MedSlot.Api.csproj

COPY src/ src/
RUN dotnet publish src/MedSlot.Api/MedSlot.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "MedSlot.Api.dll"]
