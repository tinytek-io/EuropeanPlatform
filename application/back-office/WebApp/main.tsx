/**
 * SPA marker for the scaffold phase of this system. The shared SPA configuration walks up from
 * the assembly location at startup looking for the WebApp project (main.tsx or dist/index.html);
 * every API host needs the marker to exist or startup fails. The real back-office SPA moves in
 * at the cutover, replacing this file with the rsbuild entry point and its built dist bundle.
 */
export {};
