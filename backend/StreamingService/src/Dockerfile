FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/StreamingService.Api/StreamingService.Api.csproj", "StreamingService.Api/"]
COPY ["src/StreamingService.Application/StreamingService.Application.csproj", "StreamingService.Application/"]
COPY ["src/StreamingService.Domain/StreamingService.Domain.csproj", "StreamingService.Domain/"]
COPY ["src/StreamingService.Infrastructure/StreamingService.Infrastructure.csproj", "StreamingService.Infrastructure/"]
COPY ["src/StreamingService.Presentation/StreamingService.Presentation.csproj", "StreamingService.Presentation/"]

RUN dotnet restore "StreamingService.Api/StreamingService.Api.csproj"
COPY src/. .
WORKDIR "/src/StreamingService.Api"
RUN dotnet publish "StreamingService.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "StreamingService.Api.dll"]