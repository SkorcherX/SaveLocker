# LocalGameSync Dashboard

React + TypeScript + Vite web UI for the LocalGameSync server.

Built by the Docker `web` stage and served from `src/Server/wwwroot/`. Run `npm run dev` for local development against a running server instance.

## API types

`src/types.ts` aliases the contract in `src/api-types.ts`, which is generated from
the server's OpenAPI document — so the dashboard's types can't drift from the C# DTOs.
Both files are committed (Docker builds need no server or spec).

After changing the server API, regenerate:

1. Run the server and refresh the checked-in spec:
   `curl http://localhost:5179/openapi/v1.json -o ../src/Server/openapi.json`
   (the server also serves an interactive explorer at `/swagger`).
2. `npm run gen:api` — regenerates `src/api-types.ts` from that spec.

A mismatch between the new contract and the code surfaces as a `tsc` build error.
