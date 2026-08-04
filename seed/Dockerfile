# Built and run only when `make seed` fires — one-shot, no long-lived container.
# Runs inside the compose network so it can resolve `pg-primary` and sidestep
# the host-port conflict when a native Postgres is already bound to 5432.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY Seed.csproj ./
RUN dotnet restore Seed.csproj

COPY . .
RUN dotnet publish Seed.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENTRYPOINT ["dotnet", "bruin-seed.dll"]
