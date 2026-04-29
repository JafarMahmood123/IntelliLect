FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["nuget.config", "."]
COPY ["LocalPackages/", "LocalPackages/"]

COPY ["EmailService/src/EmailService.Api/EmailService.Api.csproj", "EmailService.Api/"]
COPY ["EmailService/src/EmailService.Application/EmailService.Application.csproj", "EmailService.Application/"]
COPY ["EmailService/src/EmailService.Infrastructure/EmailService.Infrastructure.csproj", "EmailService.Infrastructure/"]

RUN dotnet restore "EmailService.Api/EmailService.Api.csproj"
COPY EmailService/src/. .
WORKDIR "/src/EmailService.Api"
RUN dotnet publish "EmailService.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "EmailService.Api.dll"]
