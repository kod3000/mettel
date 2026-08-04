# One image, two roles: HTTP API (default) or background worker (--worker arg).
# Compose sets the arg on the worker service.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Bruin.Api.csproj ./
RUN dotnet restore Bruin.Api.csproj

COPY . .
RUN dotnet publish Bruin.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Bruin.Api.dll"]
