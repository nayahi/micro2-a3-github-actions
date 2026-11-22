# NotificationService/Dockerfile · se construye desde ESTA carpeta (context: ../NotificationService)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY NotificationService.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5003
ENTRYPOINT ["dotnet", "NotificationService.dll"]
