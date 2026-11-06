FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra

WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
COPY ./Api/publish .

USER app

ENTRYPOINT ["dotnet", "BackOffice.Api.dll"]
