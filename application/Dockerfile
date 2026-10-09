# Dockerfile for the JavaScript application monorepo. Aspire uses this instead of generating a
# default one when publishing (e.g. `aspire deploy` builds it to run the `build` script).
#
# A default generated Dockerfile runs `npm ci` after copying only the root package.json — that
# only works for single-package apps. This is an npm workspaces monorepo: `npm ci` at the root
# resolves the full dependency tree from EVERY workspace package.json, so the whole source tree
# must be present first (a stale copy with missing workspace manifests breaks `tsx`/esbuild with
# a TransformError). The .dockerignore keeps the copied context small; the npm cache mount keeps
# repeated installs fast.
FROM node:24

WORKDIR /app

COPY . .

RUN --mount=type=cache,target=/root/.npm npm ci

# The monorepo build: bundles all SPAs, renders the email templates, and compiles translations.
RUN npm run build
