FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["ClassroomService.Api/ClassroomService.Api.csproj", "ClassroomService.Api/"]
COPY ["ClassroomService.Application/ClassroomService.Application.csproj", "ClassroomService.Application/"]
COPY ["ClassroomService.Domain/ClassroomService.Domain.csproj", "ClassroomService.Domain/"]
COPY ["ClassroomService.Infrastructure/ClassroomService.Infrastructure.csproj", "ClassroomService.Infrastructure/"]
COPY ["ClassroomService.Presentation/ClassroomService.Presentation.csproj", "ClassroomService.Presentation/"]

RUN dotnet restore "ClassroomService.Api/ClassroomService.Api.csproj"
COPY . .
WORKDIR "/src/ClassroomService.Api"
RUN dotnet publish "ClassroomService.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ClassroomService.Api.dll"]