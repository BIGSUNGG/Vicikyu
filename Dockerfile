FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY VolleyballClub.Domain/*.csproj VolleyballClub.Domain/
COPY VolleyballClub.Application/*.csproj VolleyballClub.Application/
COPY VolleyballClub.Infrastructure/*.csproj VolleyballClub.Infrastructure/
COPY VolleyballClub.Web/*.csproj VolleyballClub.Web/
RUN dotnet restore VolleyballClub.Web/VolleyballClub.Web.csproj

COPY . .
RUN dotnet publish VolleyballClub.Web/VolleyballClub.Web.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "VolleyballClub.Web.dll"]
