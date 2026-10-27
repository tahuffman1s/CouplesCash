# syntax=docker/dockerfile:1.7
# ---- build -----------------------------------------------------------------------------------
# The build runs natively on the build host ($BUILDPLATFORM): its output and the production dependencies are plain
# JavaScript, so only the runtime stage targets the Pi. Building under arm64 emulation (QEMU) hung the image publish.
FROM --platform=$BUILDPLATFORM node:22-alpine AS build
WORKDIR /app
RUN corepack enable && corepack prepare pnpm@10 --activate
COPY package.json pnpm-lock.yaml pnpm-workspace.yaml .npmrc ./
RUN --mount=type=cache,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile
COPY . .
# ORIGIN/API_URL are runtime variables; nothing from .env is baked into the bundle.
RUN pnpm build && pnpm prune --prod

# ---- runtime ---------------------------------------------------------------------------------
FROM node:22-alpine AS runtime
ENV NODE_ENV=production \
    PORT=3000 \
    HOST=0.0.0.0 \
    BODY_SIZE_LIMIT=1M
# ORIGIN   – public URL of the app, e.g. https://cash.example.com (required for form actions)
# API_URL  – internal URL of the .NET API, e.g. http://api:8080
WORKDIR /app
COPY --from=build --chown=node:node /app/build ./build
COPY --from=build --chown=node:node /app/package.json ./package.json
COPY --from=build --chown=node:node /app/node_modules ./node_modules
USER node
EXPOSE 3000
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s \
  CMD wget -qO- http://127.0.0.1:3000/offline >/dev/null || exit 1
CMD ["node", "build"]
