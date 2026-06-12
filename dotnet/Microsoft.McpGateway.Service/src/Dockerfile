FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish Microsoft.McpGateway.Service/src/Microsoft.McpGateway.Service.csproj \
    -c Release -r linux-arm64 --no-self-contained \
    -p:UseAppHost=false \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled-arm64v8
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8000
ENTRYPOINT ["dotnet", "Microsoft.McpGateway.Service.dll"]
