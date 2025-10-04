FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build-env

WORKDIR /app
COPY ./ /app

RUN dotnet restore

RUN dotnet publish -c Release -o /out ./EverybodyIsJohn/EverybodyIsJohn.csproj

FROM mcr.microsoft.com/dotnet/aspnet:9.0

# RUN apk add --no-cache icu-libs
# RUN apk add --no-cache icu-data-full

WORKDIR /app

COPY --from=build-env /out .

EXPOSE 30000
EXPOSE 11111

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV ASPNETCORE_HTTP_PORTS=8080
ENTRYPOINT ["dotnet", "EverybodyIsJohn.dll"]

