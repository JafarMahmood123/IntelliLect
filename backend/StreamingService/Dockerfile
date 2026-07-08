FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["nuget.config", "."]
COPY ["LocalPackages/", "LocalPackages/"]

COPY ["IntelliLect.Contracts/", "/IntelliLect.Contracts/"]

COPY ["StreamingService/src/StreamingService.Api/StreamingService.Api.csproj", "StreamingService.Api/"]
COPY ["StreamingService/src/StreamingService.Application/StreamingService.Application.csproj", "StreamingService.Application/"]
COPY ["StreamingService/src/StreamingService.Domain/StreamingService.Domain.csproj", "StreamingService.Domain/"]
COPY ["StreamingService/src/StreamingService.Infrastructure/StreamingService.Infrastructure.csproj", "StreamingService.Infrastructure/"]
COPY ["StreamingService/src/StreamingService.Presentation/StreamingService.Presentation.csproj", "StreamingService.Presentation/"]

RUN dotnet restore "StreamingService.Api/StreamingService.Api.csproj" --disable-parallel \
     && dotnet restore "StreamingService.Api/StreamingService.Api.csproj" -v m

COPY StreamingService/src/. .

WORKDIR "/src/StreamingService.Api"
RUN dotnet publish "StreamingService.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update && \
    apt-get install -y libgssapi-krb5-2 && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "StreamingService.Api.dll"]