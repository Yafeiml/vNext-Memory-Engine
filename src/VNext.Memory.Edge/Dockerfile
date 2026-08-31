FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/VNext.Memory.Domain/VNext.Memory.Domain.csproj src/VNext.Memory.Domain/
COPY src/VNext.Memory.Edge/VNext.Memory.Edge.csproj src/VNext.Memory.Edge/
RUN dotnet restore src/VNext.Memory.Edge/VNext.Memory.Edge.csproj

COPY src/ src/
RUN dotnet publish src/VNext.Memory.Edge/VNext.Memory.Edge.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/data && chown -R $APP_UID:$APP_UID /app
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "VNext.Memory.Edge.dll"]
