---
paths: application/shared-webapp/*/rslib.config.ts,application/*/WebApp/rsbuild.config.ts,application/shared-webapp/build/plugin/*.ts,application/package.json
description: How the frontend build system is wired (rslib libraries, rsbuild apps, module federation) and which build-tool upgrades are intentionally deferred
---

# Frontend Build System

The shared libraries under `application/shared-webapp/` (`@repo/ui`, `@repo/infrastructure`, `@repo/utils`,
`@repo/build`) are built with **rslib** (ESM, bundleless) and consumed from their `dist` output. The three
SPAs (`main/WebApp`, `account/WebApp`, `back-office/WebApp`) are built with **rsbuild**, and **module
federation lives only in the WebApp rsbuild builds** — rslib builds plain libraries, it does not produce
federation remotes. Understand these constraints before changing any `rslib.config.ts`, an app's
`rsbuild.config.ts`, or the toolchain versions in `application/package.json`.

## Implementation

1. **Build shared libraries with rslib, consume from `dist`.** Each library has an `rslib.config.ts`
   (`bundle: false`, `format: "esm"`, `dts: true`). Their `package.json` `exports` map subpaths into
   `dist`; there is no `source` export condition and the WebApps do not use `pluginSourceBuild`. Turbo's
   `^build` ordering builds libraries before the apps.

2. **Run the Lingui macro transform inside any rslib library that uses macros.** Because a library is
   pre-built (not transformed by the consuming app), its `rslib.config.ts` must include `LinguiPlugin()`
   (which injects `@lingui/swc-plugin`) in `plugins`, or `<Trans>` / `` t` ` `` macros ship untransformed
   and throw `Trans is not defined` / `t is not defined` at runtime. Today only `@repo/ui` uses macros, so
   only its config needs it — add `LinguiPlugin()` to any other library that introduces Lingui macros.
   `@lingui/swc-plugin` runs against rspack's embedded SWC (now in both the WebApp rsbuild builds and this
   rslib build), and the SWC plugin API is not semver-stable — it's pinned to an exact version, so re-check
   compatibility (plugins.swc.rs) whenever `@rspack/binding` or `@lingui/swc-plugin` is bumped, or the build
   fails with `LayoutError` / "failed to run Wasm plugin transform".

3. **Give rslib the asset extension for images imported by library components.** Raster/vector assets a
   library component imports (e.g. `@repo/ui` `Logo.tsx` importing `../images/*.png`) must have their
   extension in the library's rslib `source.entry.index` glob (`./**/*.png`, `./**/*.webp`, `./**/*.svg`)
   so rslib emits an asset module and rewrites the import correctly. Package-path imports from apps
   (`@repo/ui/images/x.webp`) resolve through the `./*.webp` / `./*.png` `exports`; rslib emits the raw
   assets there unhashed via `output.distPath.image: "images"` + `output.filename.image: "[name][ext]"` (no
   hand-rolled copy step). Node-side build code must be ESM-safe (`import.meta.dirname`,
   not `__dirname`; read JSON with `fs`, not `require()`).

4. **Keep `react`, `react-dom`, `@lingui/core`, `@lingui/react`, `@tanstack/react-router`, and
   `@tanstack/react-query` as module-federation `shared` singletons**
   (`application/shared-webapp/build/plugin/ModuleFederationPlugin.ts`). The translation system depends on a
   single shared Lingui `i18n` across remotes — see [translations](/.claude/rules/frontend/translations.md).
   Router instances and route objects cross the federation boundary (account contributes its route subtree
   to Main's single router), and federated components resolve their QueryClient from the host's provider,
   so those modules must also bind to one instance.

5. **TypeScript 7 is intentionally deferred — stay on the 6.x line.** TS 7's native compiler removed the
   classic programmatic API (its `package.json` exposes no main export, only `./unstable/*`), so
   `openapi-typescript` (the `swagger` codegen) and rslib's `dts` generator both fail to load it, and it
   cannot be the root `typescript`. Only the `tsc` CLI works. Revisit when those tools support the native
   compiler (≈ TS 7.1). If a TS 7 typecheck-only gate is wanted before then, run it in parallel via `tsgo`
   from `@typescript/native-preview` (a separate binary, no `typescript` peer conflict) — do **not** bump
   the root `typescript` to 7.

6. **The Rust React Compiler is deferred to a follow-up PR.** It is the official React Compiler ported to
   Rust and integrated via SWC, shipped in **Rspack/Rsbuild 2.1**; this repo is on 2.0.x. To adopt: bump
   `@rsbuild/core` and `@rspack/binding` to 2.1, enable it on `@rsbuild/plugin-react` (or via
   `builtin:swc-loader` `jsc.transform.reactCompiler`), and validate the component set. The federation
   singletons in step 4 are already the prerequisite.

7. **After changing rules under `.claude/`, run `dotnet run --project developer-cli -- sync-ai-rules --quiet`.**

## Examples

### Example 1 - Lingui macros in an rslib library config

```ts
// ✅ DO: a library that uses <Trans>/t must run the Lingui SWC plugin during its own build
import { LinguiPlugin } from "@repo/build/plugin/LinguiPlugin";
import { pluginReact } from "@rsbuild/plugin-react";
import { defineConfig } from "@rslib/core";

export default defineConfig({
  source: { entry: { index: ["./**/*.tsx", "./**/*.ts", "./**/*.svg", "./**/*.css", "./**/*.png", "!rslib.config.ts", "!node_modules/**", "!dist/**"] } },
  lib: [{ bundle: false, dts: true, format: "esm" }],
  output: { target: "web" },
  plugins: [pluginReact(), LinguiPlugin()]
});

// ❌ DON'T: omit LinguiPlugin() — macros ship untransformed → "Trans is not defined" at runtime
```

### Example 2 - Enabling the React Compiler later (Rsbuild/Rspack 2.1)

```ts
// After bumping @rsbuild/core and @rspack/binding to 2.1:
pluginReact({ reactCompiler: true });
```
