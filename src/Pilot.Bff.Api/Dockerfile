FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY ["src/Pilot.Bff.Api/Pilot.Bff.Api.csproj", "src/Pilot.Bff.Api/"]
RUN dotnet restore "src/Pilot.Bff.Api/Pilot.Bff.Api.csproj"

COPY . .
RUN dotnet publish "src/Pilot.Bff.Api/Pilot.Bff.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Pilot.Bff.Api.dll"]
