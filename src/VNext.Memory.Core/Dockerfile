FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/VNext.Memory.Domain/VNext.Memory.Domain.csproj src/VNext.Memory.Domain/
COPY src/VNext.Memory.Application/VNext.Memory.Application.csproj src/VNext.Memory.Application/
COPY src/VNext.Memory.Infrastructure.Postgres/VNext.Memory.Infrastructure.Postgres.csproj src/VNext.Memory.Infrastructure.Postgres/
COPY src/VNext.Memory.Core/VNext.Memory.Core.csproj src/VNext.Memory.Core/
RUN dotnet restore src/VNext.Memory.Core/VNext.Memory.Core.csproj

COPY src/ src/
RUN dotnet publish src/VNext.Memory.Core/VNext.Memory.Core.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "VNext.Memory.Core.dll"]
