# The build context is the repository root so CI, the local release script and Docker use the same image definition.
FROM node:26-alpine AS build
WORKDIR /app

COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend/ ./
RUN npm run build

# Official unprivileged NGINX image: listens on 8080 and runs without root.
FROM nginx/nginx-unprivileged:1.31.7-alpine3.24
COPY frontend/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/ /usr/share/nginx/html/
RUN nginx -t

EXPOSE 8080
