FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["nuget.config", "."]
COPY ["LocalPackages/", "LocalPackages/"]

COPY ["ClassroomService/src/ClassroomService.Api/ClassroomService.Api.csproj", "ClassroomService.Api/"]
COPY ["ClassroomService/src/ClassroomService.Application/ClassroomService.Application.csproj", "ClassroomService.Application/"]
COPY ["ClassroomService/src/ClassroomService.Domain/ClassroomService.Domain.csproj", "ClassroomService.Domain/"]
COPY ["ClassroomService/src/ClassroomService.Infrastructure/ClassroomService.Infrastructure.csproj", "ClassroomService.Infrastructure/"]
COPY ["ClassroomService/src/ClassroomService.Presentation/ClassroomService.Presentation.csproj", "ClassroomService.Presentation/"]

RUN dotnet restore "ClassroomService.Api/ClassroomService.Api.csproj"

COPY ClassroomService/src/. .

WORKDIR "/src/ClassroomService.Api"
RUN dotnet publish "ClassroomService.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update && \
    apt-get install -y libgssapi-krb5-2 && \
    rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ClassroomService.Api.dll"]