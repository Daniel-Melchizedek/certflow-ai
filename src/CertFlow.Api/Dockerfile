FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/CertFlow.Api/CertFlow.Api.csproj", "src/CertFlow.Api/"]
COPY ["src/CertFlow.Application/CertFlow.Application.csproj", "src/CertFlow.Application/"]
COPY ["src/CertFlow.Domain/CertFlow.Domain.csproj", "src/CertFlow.Domain/"]
COPY ["src/CertFlow.Contracts/CertFlow.Contracts.csproj", "src/CertFlow.Contracts/"]
COPY ["src/CertFlow.Infrastructure/CertFlow.Infrastructure.csproj", "src/CertFlow.Infrastructure/"]
COPY ["src/CertFlow.Agent/CertFlow.Agent.csproj", "src/CertFlow.Agent/"]
RUN dotnet restore "src/CertFlow.Api/CertFlow.Api.csproj"
COPY . .
WORKDIR "/src/src/CertFlow.Api"
RUN dotnet publish "CertFlow.Api.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CertFlow.Api.dll"]
