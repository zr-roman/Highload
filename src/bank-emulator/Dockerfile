# The build context is the repository root, like for the other services:
#   docker build -t bank-emulator:dev -f src/bank-emulator/Dockerfile .

# Alpine is enough to build: CGO is off, the binary needs no C library.
FROM golang:1.24-alpine AS build
WORKDIR /src
COPY src/bank-emulator/go.mod ./
RUN go mod download
COPY src/bank-emulator/ ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /out/bank-emulator .

# Distroless static: only the binary, CA certificates and tzdata; nonroot is uid 65532.
FROM gcr.io/distroless/static-debian12:nonroot
COPY --from=build /out/bank-emulator /bank-emulator
ENV BANK_ADDR=:5300
EXPOSE 5300
ENTRYPOINT ["/bank-emulator"]
