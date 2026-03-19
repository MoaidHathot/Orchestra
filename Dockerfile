# Dockerfile for Orchestra Web Playground
# Targets the ASP.NET Core Web playground (Linux-compatible)

FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY OrchestrationEngine.slnx .
COPY Directory.Build.props .
COPY Directory.Packages.props .
COPY src/Orchestra.Engine/Orchestra.Engine.csproj src/Orchestra.Engine/
COPY src/Orchestra.Host/Orchestra.Host.csproj src/Orchestra.Host/
COPY src/Orchestra.Copilot/Orchestra.Copilot.csproj src/Orchestra.Copilot/
COPY playground/Hosting/Orchestra.Playground.Copilot.Web/Orchestra.Playground.Copilot.Web.csproj playground/Hosting/Orchestra.Playground.Copilot.Web/

# Restore dependencies
RUN dotnet restore playground/Hosting/Orchestra.Playground.Copilot.Web/Orchestra.Playground.Copilot.Web.csproj

# Copy remaining source files
COPY src/ src/
COPY playground/Hosting/Orchestra.Playground.Copilot.Web/ playground/Hosting/Orchestra.Playground.Copilot.Web/

# Build and publish
RUN dotnet publish playground/Hosting/Orchestra.Playground.Copilot.Web/Orchestra.Playground.Copilot.Web.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Create data directories
RUN mkdir -p /app/runs /app/results /app/uploads

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Orchestra.Playground.Copilot.Web.dll"]
