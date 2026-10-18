# Compose migrations

## Implemented

- Added a console runner that calls `StreamingDbContext.Database.MigrateAsync()` against the migrations in the Data project.
- The runner requires the connection string from its environment and exits unsuccessfully on migration failure.
- Added a dedicated Docker build target without API startup, background workers or media mounts.
- Compose runs the migration service before starting the server and requires successful completion.
- Updated deployment instructions with migration logs, reruns and database permissions.

## Deployment

Run `docker compose up --build -d` with the existing private `.env`. Inspect `docker compose logs migrations` if startup is blocked. Schema updates require database permissions and an intact EF migration history. Existing running containers are not stopped automatically on migration failure during an update.

## Verification

The migration runner published successfully in Release configuration. Compose configuration validation passed with temporary placeholder inputs. No test suites were added or run for this deployment change.

No production database was accessed or migrated while implementing this change. Local settings and credentials were left untouched.

## Runtime dependency repair

The first deployed runner failed while loading migration types: the Data project compiled against Relational 9.0.7 through its private Design dependency, while the runner resolved Relational 9.0.1 through the PostgreSQL provider. Added an explicit Relational 9.0.7 runtime reference to the Data project so consumers resolve the same assembly version.

The migration Docker target now invokes `--verify-migrations` during its build. This loads and enumerates the published EF migration types without connecting to a database, and fails the image build if assemblies cannot load or no migrations are found.

Release publication passed after the repair. The published runner successfully loaded all 32 migrations with `--verify-migrations`, and its dependency manifest resolves Relational 9.0.7. No database connection or schema change was performed.
