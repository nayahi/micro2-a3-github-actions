# PaymentService.gRPC/Dockerfile · se construye desde ESTA carpeta (context: ../PaymentService.gRPC)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY PaymentService.gRPC.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 7004 5004
ENTRYPOINT ["dotnet", "PaymentService.gRPC.dll"]
