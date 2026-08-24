# Build context is the repository root, because the middleware references two sibling projects.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Directory.Build.props ./
COPY src/LLM.Abstraction/LLM.Abstraction.csproj src/LLM.Abstraction/
COPY src/LLM.FakeProvider/LLM.FakeProvider.csproj src/LLM.FakeProvider/
COPY src/Middleware/Middleware.csproj src/Middleware/
RUN dotnet restore src/Middleware/Middleware.csproj

COPY src/ src/
RUN dotnet publish src/Middleware/Middleware.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./

# No credentials are baked in: the connection string arrives from the environment, per the
# constitution's constraint on tracked configuration.
ENTRYPOINT ["dotnet", "Middleware.dll"]
