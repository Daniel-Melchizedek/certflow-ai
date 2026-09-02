FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/CertFlow.Portal.Blazor/CertFlow.Portal.Blazor.csproj", "src/CertFlow.Portal.Blazor/"]
RUN dotnet restore "src/CertFlow.Portal.Blazor/CertFlow.Portal.Blazor.csproj"
COPY . .
WORKDIR "/src/src/CertFlow.Portal.Blazor"
RUN dotnet publish "CertFlow.Portal.Blazor.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CertFlow.Portal.Blazor.dll"]
