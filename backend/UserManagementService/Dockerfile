# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy all csproj files to restore dependencies (for layer caching)
COPY ["src/UserManagementService.Api/UserManagementService.Api.csproj", "UserManagementService.Api/"]
COPY ["src/UserManagementService.Application/UserManagementService.Application.csproj", "UserManagementService.Application/"]
COPY ["src/UserManagementService.Domain/UserManagementService.Domain.csproj", "UserManagementService.Domain/"]
COPY ["src/UserManagementService.Infrastructure/UserManagementService.Infrastructure.csproj", "UserManagementService.Infrastructure/"]
COPY ["src/UserManagementService.Presentation/UserManagementService.Presentation.csproj", "UserManagementService.Presentation/"]

RUN dotnet restore "UserManagementService.Api/UserManagementService.Api.csproj"

# Copy the rest of the source code
COPY . .

# Build and Publish
WORKDIR "/src/src/UserManagementService.Api"
RUN dotnet publish "UserManagementService.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update && \
    apt-get install -y libgssapi-krb5-2 && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Create the Logs directory and give permissions (Important for Ubuntu/Linux)
RUN mkdir -p Logs && chmod 777 Logs

ENTRYPOINT ["dotnet", "UserManagementService.Api.dll"]