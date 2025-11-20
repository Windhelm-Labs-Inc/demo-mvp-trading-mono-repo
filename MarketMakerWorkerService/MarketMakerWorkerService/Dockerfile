# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files
COPY ["MarketMakerWorkerService/MarketMakerWorkerService.csproj", "MarketMakerWorkerService/"]
COPY ["sdk/Hiero/Hiero/Hiero.csproj", "sdk/Hiero/Hiero/"]

# Restore dependencies
RUN dotnet restore "MarketMakerWorkerService/MarketMakerWorkerService.csproj"

# Copy all source code
COPY . .

# Build the application
WORKDIR "/src/MarketMakerWorkerService"
RUN dotnet build "MarketMakerWorkerService.csproj" -c Release -o /app/build

# Stage 2: Publish
FROM build AS publish
RUN dotnet publish "MarketMakerWorkerService.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS final
WORKDIR /app

# Create non-root user
RUN useradd -m -u 1000 marketmaker && chown -R marketmaker:marketmaker /app
USER marketmaker

# Copy published app
COPY --from=publish --chown=marketmaker:marketmaker /app/publish .

# Environment variables (override these at runtime)
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_EnableDiagnostics=0

# Run the application
ENTRYPOINT ["dotnet", "MarketMakerWorkerService.dll"]
