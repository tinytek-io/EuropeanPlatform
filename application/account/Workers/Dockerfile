FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra

WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production
COPY ./Workers/publish .

USER app

ENTRYPOINT ["dotnet", "Account.Workers.dll"]
