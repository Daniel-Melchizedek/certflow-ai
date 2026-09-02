FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/CertFlow.Worker/CertFlow.Worker.csproj", "src/CertFlow.Worker/"]
COPY ["src/CertFlow.Application/CertFlow.Application.csproj", "src/CertFlow.Application/"]
COPY ["src/CertFlow.Domain/CertFlow.Domain.csproj", "src/CertFlow.Domain/"]
COPY ["src/CertFlow.Contracts/CertFlow.Contracts.csproj", "src/CertFlow.Contracts/"]
COPY ["src/CertFlow.Infrastructure/CertFlow.Infrastructure.csproj", "src/CertFlow.Infrastructure/"]
COPY ["src/CertFlow.Agent/CertFlow.Agent.csproj", "src/CertFlow.Agent/"]
RUN dotnet restore "src/CertFlow.Worker/CertFlow.Worker.csproj"
COPY . .
WORKDIR "/src/src/CertFlow.Worker"
RUN dotnet publish "CertFlow.Worker.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "CertFlow.Worker.dll"]
