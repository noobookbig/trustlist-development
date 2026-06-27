# Trustlist App (C# .NET 8) — local Docker Compose

A locally-runnable **Trust List directory** for the *Verifiable Credential
Ecosystem In Thailand* project. Built end-to-end in **C# .NET 8**:

- **Backend** — ASP.NET Core **Web API** (`Trustlist.Api`), EF Core + **MS SQL
  Express**, **JWT login/register** via ASP.NET Core Identity.
- **Frontend** — ASP.NET Core **Blazor Server** (`Trustlist.Web`) that talks to
  the API over HTTP.
- **Database** — Microsoft SQL Server **Express** edition in a container.
- **Orchestration** — `docker compose` brings up all three together. Local only;
  no cloud deploy.

Implements `MAS-673` (delegated from `MAS-672`).
Aligns the data model + role-keyed routes with the canonical directory
contract — `MAS-676`.

## What a "Trust List" entity is

The directory holds registered **Issuers**, **Verifiers** and **Wallet
Providers** in the Thai VC ecosystem, each with a lifecycle **status**
(`Valid` / `Suspended` / `Withdrawn` / `Applied` / `Vetted` / `Expired`),
jurisdiction, certification reference and scope. The data model is grounded in
the canonical directory contract
(`openapi-trustlist-directory.yaml`, ETSI TS 119 612 + MAS-335 §3.3) — the
role-specific record shape (`IssuerRecord`, `VerifierRecord`,
`WalletProviderRecord`) and revocation references (`status_list_endpoint` for
Issuers, `wia_status_list_uri` for Wallet Providers) live there.

## Run it

Requirements: Docker + Docker Compose v2.

Secrets are **not** committed. Copy the example env file and fill in real
values before bringing the stack up:

```bash
cd trustlist-app
cp .env.example .env

# generate strong values (>=32 bytes for JWT key, >=16 chars for sa password)
sed -i "s|^MSSQL_SA_PASSWORD=.*|MSSQL_SA_PASSWORD=$(openssl rand -base64 24)|"   .env
sed -i "s|^JWT_SIGNING_KEY=.*|JWT_SIGNING_KEY=$(openssl rand -base64 48)|"       .env

docker compose up -d --build
```

`docker compose` will refuse to start if `MSSQL_SA_PASSWORD` or
`JWT_SIGNING_KEY` are missing, and the API will fail-closed at boot if the
JWT key is shorter than 32 bytes or the DB connection string is empty.

`.env` is gitignored — never commit real secrets.

The DB has a healthcheck; the API waits for it, applies EF migrations and seeds
data on startup. First boot pulls the MS SQL image (~1.5 GB) and may take a
couple of minutes.

### Generating EF Core migrations

Migrations live in `src/Trustlist.Api/Data/Migrations/`. When you generate them
inside a container (`dotnet ef migrations add ...`), the EF tooling defaults to
running as `root`, so the generated `.cs` files land **root-owned** and the
build user can no longer edit or delete them. This previously produced a stale,
undeletable `Data/_quarantine_root_old/` copy.

To keep generated files **user-owned**, always pass `--user $(id -u):$(id -g)`
when invoking the EF tooling in a container, e.g.:

```bash
docker run --rm \
  --user "$(id -u):$(id -g)" \
  -v "$PWD/src/Trustlist.Api:/src" -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet ef migrations add <Name> -o Data/Migrations
```

(If a root-owned file ever sneaks in again, delete it with a throwaway root
container bind-mounting the directory — no host `sudo` required:
`docker run --rm -v "$PWD/src/Trustlist.Api/Data:/work" --user 0:0 busybox rm -rf /work/<path>`.)

### Building & testing locally (run as the workspace user)

You can build and test the solution **in place** with the local .NET 8 SDK:

```bash
cd trustlist-app
dotnet build Trustlist.sln
dotnet test  Trustlist.sln
```

**Always run builds and tests as the same OS user that owns the working tree**
(the `big`/workspace user here) — never as `root` and never through a
container that runs `dotnet` as `root`. A containerized build that writes
`src/**/obj` and `bin/` as `root` leaves those directories **root-owned**, after
which the workspace user's next `dotnet build` fails at restore with:

```
Access to the path '.../src/Trustlist.Api/obj/<guid>.tmp' is denied. Permission denied
```

`bin/` and `obj/` are already gitignored, so they are build artifacts only — if
ownership ever skews again, delete the offending dirs with a throwaway root
container (no host `sudo` needed) and rebuild as the workspace user:

```bash
docker run --rm -v "$PWD/src/Trustlist.Api:/work" --user 0:0 busybox \
  rm -rf /work/obj /work/bin
dotnet build Trustlist.sln
```

If you must build inside a container, pass `--user "$(id -u):$(id -g)"` so the
artifacts stay user-owned (same rule as the EF tooling above).

### Ports

| Service | URL | Notes |
|---|---|---|
| Frontend (Blazor) | http://localhost:5080 | Directory + Login + Manage |
| Backend Web API | http://localhost:8080 | REST + Swagger at `/swagger` |
| MS SQL Express | localhost:1433 | `sa` / value of `MSSQL_SA_PASSWORD` from your local `.env` |

### Seeded login

A default admin user is created on first run:

| Email | Password |
|---|---|
| `admin@trustlist.local` | `Admin#12345` |

You can also self-register from the Login page (or `POST /api/auth/register`).

## API surface

The API exposes two parallel surfaces:

- **`/v1/{role}/{entity-id}`** — role-keyed, snake_case, **matches the canonical
  OpenAPI directory contract** (`openapi-trustlist-directory.yaml`).
  This is the public, Verifier- and Wallet-facing surface.
- **`/api/trustlist`** — flat admin surface used by the Blazor frontend
  (kept for backward compatibility with the MAS-673 cut).

### Public directory surface (canonical)

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/v1/issuers` | List issuers (`?status=&since=&jurisdiction=&page=&limit=`) |
| `GET` | `/v1/issuers/{entity-id}` | Single Issuer record (IssuerRecord shape) |
| `GET` | `/v1/issuers/{entity-id}/status` | Lightweight `active` boolean + minimal cert metadata |
| `GET` | `/v1/verifiers` | List verifiers |
| `GET` | `/v1/verifiers/{entity-id}` | Single Verifier record (carries `client_identifiers[]`) |
| `GET` | `/v1/verifiers/{entity-id}/status` | Lightweight status + primary client identifier |
| `GET` | `/v1/wallet-providers` | List wallet providers |
| `GET` | `/v1/wallet-providers/{entity-id}` | Single WP record (carries `trust_anchors[]` + WIA URIs) |
| `GET` | `/v1/wallet-providers/{entity-id}/status` | Lightweight status + cert metadata |

`{entity-id}` is the canonical URL-form identifier (e.g.
`https://issuer.mhesi.go.th`); encode `:` as `%3A` and `/` as `%2F` in the path
segment.

### Admin / manage surface

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `POST` | `/api/auth/register` | public | Create user, returns JWT |
| `POST` | `/api/auth/login` | public | Login, returns JWT |
| `GET` | `/api/trustlist` | public | Flat list (`?role=&status=&jurisdiction=&q=`) |
| `GET` | `/api/trustlist/{id}` | public | Get one entity (by numeric id) |
| `POST` | `/api/trustlist` | **JWT** | Create entity (accepts role-specific key material — see below) |
| `PUT` | `/api/trustlist/{id}` | **JWT** | Update entity (role-specific fields overwrite only when supplied) |
| `DELETE` | `/api/trustlist/{id}` | **JWT** | Delete entity |
| `GET` | `/health` | public | Liveness |

The create/update body and the **Manage** page now carry the role-specific key
material the canonical spec requires (MAS-687):

- **Issuer** / **Wallet Provider** — `trust_anchors[]` (the published signing keys /
  "pubkey": `kid` + `format` + `status` + `jwk` or `x5c`).
- **Verifier** — `client_identifiers[]` (OpenID4VP §5.9 identity / key binding:
  `prefix` + `value`).
- **Wallet Provider** — WIA fields: `wia_status_list_uri`,
  `wia_revocation_maintenance_period_days`, `wia_attestation_format[]`.

### Quick smoke (after `compose up`)

```bash
curl http://localhost:8080/health

# canonical /v1 surface
curl http://localhost:8080/v1/issuers
curl 'http://localhost:8080/v1/issuers/https%3A%2F%2Fissuer.mhesi.go.th'
curl 'http://localhost:8080/v1/issuers/https%3A%2F%2Fissuer.mhesi.go.th/status'
curl http://localhost:8080/v1/verifiers
curl http://localhost:8080/v1/wallet-providers

# legacy /api surface + login
curl http://localhost:8080/api/trustlist
TOKEN=$(curl -s -X POST http://localhost:8080/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@trustlist.local","password":"Admin#12345"}' \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')
curl -X POST http://localhost:8080/api/trustlist \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"role":"Issuer","entity_id":"https://issuer.demo.go.th","entity_name":"Demo","jurisdiction":"TH","status":"Valid"}'
```

## Layout

```
trustlist-app/
├── docker-compose.yml         # db (MS SQL Express) + api + web
└── src/
    ├── Trustlist.Api/         # ASP.NET Core Web API + EF Core + Identity/JWT
    │   ├── Controllers/       # AuthController, TrustlistController,
    │   │                      #   IssuersController, VerifiersController,
    │   │                      #   WalletProvidersController
    │   ├── Domain/            # TrustlistEntity + enums
    │   │                      #   (EntityRole, EntityStatus, TrustAnchorFormat,
    │   │                      #    TrustAnchorStatus, ClientIdentifierPrefix)
    │   ├── Data/              # AppDbContext, DbSeeder, Migrations/
    │   ├── Auth/              # JwtOptions, TokenService
    │   └── Dtos/              # Auth DTOs, TrustlistEntityDto,
    │                          #   RoleRecords (IssuerRecord, VerifierRecord,
    │                          #   WalletProviderRecord), StatusWireFormat
    └── Trustlist.Web/         # Blazor Server frontend
        └── Components/Pages/  # Directory, Login, Manage
```

## Canonical contract

The directory's wire format follows
`trustlist-research/repo/src/PRD-1.0/openapi-trustlist-directory.yaml`:

- JSON property names are `snake_case` everywhere (both `/v1/...` and `/api/...`).
- Enum values are lowercase strings (e.g. `"valid"`, `"suspended"`, `"jwk"`,
  `"x509"`).
- `entity_id` is a URL form like `https://issuer.example.go.th`; clients URL-
  encode `:` as `%3A` and `/` as `%2F` in path segments.
- Status `410 Gone` is returned for entities that have been `withdrawn` —
  distinct from `404 Not Found` so a Verifier can distinguish "never existed"
  from "existed and was revoked".
- The role-specific records (`IssuerRecord`, `VerifierRecord`,
  `WalletProviderRecord`) are populated end-to-end on first boot by the
  seeder — see [DbSeeder.cs](src/Trustlist.Api/Data/DbSeeder.cs) for the
  shape.

## Notes / not-yet (tracked as follow-up issues)

- **Secrets are injected via `.env`** (gitignored); `.env.example` ships with
  `CHANGE_ME` placeholders and `openssl` generation hints. The API fails to
  start when the JWT key is missing or shorter than 32 bytes
  (`MAS-674`).
- HTTPS/TLS termination, refresh tokens, role-based authorization, and the
  full W3C VC / OpenID4VCI credential flows are out of scope for this first
  local cut and tracked as child issues of `MAS-673`.
- **Signed directory response (JWS-compact)** is deferred — the directory
  currently returns plain JSON. When ETDA publisher signing keys are
  available, the response will be wrapped in a JWS per
  `SignedRoleRecord` in the OpenAPI yaml. Key custody is a one-way door;
  needs CTO + SecurityEngineer sign-off before code lands.
- **Token Status List endpoint server** — the directory stores the *URI* of
  each issuer's status list endpoint. The actual TSL bitstring service is a
  separate component.
```bash
docker compose down -v   # stop and wipe the DB volume
```
