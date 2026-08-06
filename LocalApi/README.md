# Canopy local API

Run `canopy-local-api` and copy the randomly generated bearer token printed at startup.
Every request requires `Authorization: Bearer <token>`. The server binds only to
`127.0.0.1`; it exposes `GET /health`, `POST /scans`, `GET /scans/{id}`, and
`DELETE /scans/{id}`. It has no file-deletion or other destructive endpoint.
