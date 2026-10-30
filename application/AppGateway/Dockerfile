FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled

WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
COPY ./AppGateway/publish .

USER app

ENTRYPOINT ["dotnet", "AppGateway.dll"]
