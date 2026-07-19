# OllMem HTTP API container — used by `install.sh --deploy-firebase` (Cloud Run
# builds this via `gcloud run deploy --source .`) and usable standalone:
#   docker build -t ollmem-api . && docker run -p 8080:8080 \
#     -e OllMem__Firestore__ProjectId=<project> \
#     -e OllMem__MasterKeyBase64=<key> ollmem-api
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/OllMem.Api -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Cloud Run sends traffic to $PORT (8080 by default).
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OllMem.Api.dll"]
