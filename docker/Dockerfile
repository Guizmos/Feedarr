FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Feedarr.Api/Feedarr.Api.csproj src/Feedarr.Api/
RUN dotnet restore src/Feedarr.Api/Feedarr.Api.csproj

COPY src/Feedarr.Api/ src/Feedarr.Api/
RUN dotnet publish src/Feedarr.Api/Feedarr.Api.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ARG FEEDARR_VERSION
ENV FEEDARR_VERSION=$FEEDARR_VERSION

COPY --from=build /out ./

EXPOSE 8080
ENTRYPOINT ["dotnet", "Feedarr.Api.dll"]
