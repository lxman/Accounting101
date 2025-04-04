# Stage 1: Build the Angular application
FROM node:22.14.0 AS build1
LABEL authors="Michael Jordan"
WORKDIR /app
COPY package*.json ./
COPY deploy/accounting101.client.pem /root/.aspnet/https/accounting101.client.pem
COPY deploy/accounting101.client.key /root/.aspnet/https/accounting101.client.key
COPY deploy/default /etc/nginx/sites-available/default
RUN npm install
COPY . .
RUN npm run build:azure

FROM node:22.14.0 AS azure
# Install .NET SDK
RUN apt-get update && \
    apt-get install -y apt-transport-https && \
    wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    rm packages-microsoft-prod.deb && \
    apt-get update && \
    apt-get install -y dotnet-sdk-8.0 && \
    apt-get install -y nginx

LABEL authors="Michael Jordan"
WORKDIR /app
COPY package*.json ./
COPY deploy/accounting101.client.pem /root/.aspnet/https/accounting101.client.pem
COPY deploy/accounting101.client.key /root/.aspnet/https/accounting101.client.key
COPY deploy/default /etc/nginx/sites-available/default
RUN npm install

COPY --from=build1 /app/dist/azure/accounting101.client /var/www/html
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
