# The build context is the repository root, like for the other services:
#   docker build -t antifraud:dev -f src/antifraud-service/Dockerfile .

FROM python:3.12-slim AS build
COPY --from=ghcr.io/astral-sh/uv:0.13.4 /uv /bin/uv
WORKDIR /app
# Bytecode compiled at build time, so the first requests do not wait for it; files copied, not hard-linked,
# since the cache stays in this stage; the Python of the image, never a downloaded one.
ENV UV_COMPILE_BYTECODE=1 UV_LINK_MODE=copy UV_PYTHON_DOWNLOADS=never
# Dependencies from the lock file alone: the layer is reused until uv.lock changes.
COPY src/antifraud-service/pyproject.toml src/antifraud-service/uv.lock ./
RUN uv sync --frozen --no-dev --no-install-project
COPY src/antifraud-service/antifraud ./antifraud

# The same base image as the build stage: the venv refers to /usr/local/bin/python3.12.
FROM python:3.12-slim
RUN useradd --system --uid 10001 --no-create-home antifraud
WORKDIR /app
COPY --from=build /app/.venv ./.venv
COPY --from=build /app/antifraud ./antifraud
ENV PATH="/app/.venv/bin:$PATH" \
    PYTHONUNBUFFERED=1
USER antifraud
EXPOSE 8000
# Exec form: uvicorn is PID 1 and gets SIGTERM from Kubernetes itself, so it finishes the requests in flight.
# One process per pod: Kubernetes scales with replicas, not with uvicorn workers.
CMD ["uvicorn", "antifraud.main:app", "--host", "0.0.0.0", "--port", "8000"]
