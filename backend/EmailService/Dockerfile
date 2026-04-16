FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/EmailService.Api/EmailService.Api.csproj", "EmailService.Api/"]
COPY ["src/EmailService.Application/EmailService.Application.csproj", "EmailService.Application/"]
COPY ["src/EmailService.Infrastructure/EmailService.Infrastructure.csproj", "EmailService.Infrastructure/"]
COPY ["src/EmailService.Contracts/EmailService.Contracts.csproj", "EmailService.Contracts/"]

RUN dotnet restore "EmailService.Api/EmailService.Api.csproj"
COPY src/. .
WORKDIR "/src/EmailService.Api"
RUN dotnet publish "EmailService.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "EmailService.Api.dll"]
