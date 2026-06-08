FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["nuget.config", "."]
COPY ["LocalPackages/", "LocalPackages/"]

COPY ["UserManagementService/src/UserManagementService.Api/UserManagementService.Api.csproj", "UserManagementService.Api/"]
COPY ["UserManagementService/src/UserManagementService.Application/UserManagementService.Application.csproj", "UserManagementService.Application/"]
COPY ["UserManagementService/src/UserManagementService.Domain/UserManagementService.Domain.csproj", "UserManagementService.Domain/"]
COPY ["UserManagementService/src/UserManagementService.Infrastructure/UserManagementService.Infrastructure.csproj", "UserManagementService.Infrastructure/"]
COPY ["UserManagementService/src/UserManagementService.Presentation/UserManagementService.Presentation.csproj", "UserManagementService.Presentation/"]

RUN dotnet restore "UserManagementService.Api/UserManagementService.Api.csproj"

COPY UserManagementService/src/. .

WORKDIR "/src/UserManagementService.Api"
RUN dotnet publish "UserManagementService.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update && \
    apt-get install -y libgssapi-krb5-2 && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

RUN mkdir -p Logs && chmod 777 Logs

ENTRYPOINT ["dotnet", "UserManagementService.Api.dll"]