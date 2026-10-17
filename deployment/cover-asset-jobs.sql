START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261016190024_AddCoverAssetJobs') THEN
    CREATE TABLE "CoverAssetJobs" (
        "EntityKind" integer NOT NULL,
        "EntityIdentifier" uuid NOT NULL,
        "SourceAddress" text NOT NULL,
        "Version" uuid NOT NULL,
        "LeaseToken" uuid,
        "LeaseUntil" timestamp with time zone,
        "Attempts" integer NOT NULL,
        "NextAttemptAt" timestamp with time zone NOT NULL,
        "EnqueuedAt" timestamp with time zone NOT NULL,
        "CompletedAt" timestamp with time zone,
        "LastFailure" text NOT NULL,
        "AssetManifest" text NOT NULL,
        CONSTRAINT "PK_CoverAssetJobs" PRIMARY KEY ("EntityKind", "EntityIdentifier")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261016190024_AddCoverAssetJobs') THEN
    CREATE INDEX "IX_CoverAssetJobs_CompletedAt_NextAttemptAt" ON "CoverAssetJobs" ("CompletedAt", "NextAttemptAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261016190024_AddCoverAssetJobs') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261016190024_AddCoverAssetJobs', '9.0.7');
    END IF;
END $EF$;
COMMIT;
