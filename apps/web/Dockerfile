# Dev-mode Vite for the compose stack. Production build ships from CI, not this.
FROM node:22-alpine

WORKDIR /app

# Install first so node_modules is cached across code changes.
# --legacy-peer-deps because TypeScript 6.x is still ahead of some peer
# declarations (e.g. openapi-typescript demands ^5.x) and workspace-linked
# @bruin/api-types would otherwise fail resolution.
COPY package.json package-lock.json* ./
RUN npm install --no-audit --no-fund --legacy-peer-deps

COPY . .

EXPOSE 5173
CMD ["npm", "run", "dev"]
