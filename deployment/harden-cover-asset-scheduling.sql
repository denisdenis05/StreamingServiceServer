START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017154900_HardenCoverAssetScheduling') THEN
    ALTER TABLE "CoverAssetJobs" ADD "AssetIdentity" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017154900_HardenCoverAssetScheduling') THEN
    ALTER TABLE "CoverAssetJobs" ADD "InheritedSourceRevision" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017154900_HardenCoverAssetScheduling') THEN
    ALTER TABLE "CoverAssetJobs" ADD "OriginalIdentity" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017154900_HardenCoverAssetScheduling') THEN
    ALTER TABLE "CoverAssetJobs" ADD "Priority" integer NOT NULL DEFAULT 0;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017154900_HardenCoverAssetScheduling') THEN
    ALTER TABLE "CoverAssetJobs" ADD "SourceRevision" text NOT NULL DEFAULT '';
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017154900_HardenCoverAssetScheduling') THEN
    ALTER TABLE "CoverAssetJobs" ADD "Suspended" boolean NOT NULL DEFAULT FALSE;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017154900_HardenCoverAssetScheduling') THEN
    CREATE TABLE "CoverMetadataRefreshes" (
        "Id" text NOT NULL,
        "Cursor" uuid,
        "Pending" boolean NOT NULL,
        "CompletedReleases" bigint NOT NULL,
        "RequestedAt" timestamp with time zone NOT NULL,
        "CompletedAt" timestamp with time zone,
        "Version" uuid NOT NULL,
        "LastFailure" text NOT NULL,
        CONSTRAINT "PK_CoverMetadataRefreshes" PRIMARY KEY ("Id")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017154900_HardenCoverAssetScheduling') THEN
    CREATE INDEX "IX_CoverAssetJobs_AssetIdentity" ON "CoverAssetJobs" ("AssetIdentity");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017154900_HardenCoverAssetScheduling') THEN
    CREATE INDEX "IX_CoverAssetJobs_OriginalIdentity" ON "CoverAssetJobs" ("OriginalIdentity");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017154900_HardenCoverAssetScheduling') THEN
    CREATE INDEX "IX_CoverAssetJobs_Suspended_CompletedAt_Priority_NextAttemptAt" ON "CoverAssetJobs" ("Suspended", "CompletedAt", "Priority", "NextAttemptAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017154900_HardenCoverAssetScheduling') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261017154900_HardenCoverAssetScheduling', '9.0.7');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017155038_IndexCoverBackfill') THEN
    CREATE INDEX "IX_Releases_DateAdded_Id" ON "Releases" ("DateAdded", "Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017155038_IndexCoverBackfill') THEN
    CREATE INDEX "IX_Recordings_DateAdded_Id" ON "Recordings" ("DateAdded", "Id");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017155038_IndexCoverBackfill') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261017155038_IndexCoverBackfill', '9.0.7');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017155348_PersistCoverIntegritySchedule') THEN
    CREATE TABLE "CoverAssetIntegrityStates" (
        "Identity" text NOT NULL,
        "NextVerificationAt" timestamp with time zone NOT NULL,
        CONSTRAINT "PK_CoverAssetIntegrityStates" PRIMARY KEY ("Identity")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017155348_PersistCoverIntegritySchedule') THEN
    CREATE INDEX "IX_CoverAssetIntegrityStates_NextVerificationAt" ON "CoverAssetIntegrityStates" ("NextVerificationAt");
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017155348_PersistCoverIntegritySchedule') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261017155348_PersistCoverIntegritySchedule', '9.0.7');
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017155458_PersistCoverBackfillCursor') THEN
    CREATE TABLE "CoverBackfillStates" (
        "EntityKind" integer NOT NULL,
        "PositionAddedAt" timestamp with time zone,
        "PositionIdentifier" uuid,
        "NextRestartAt" timestamp with time zone NOT NULL,
        "Version" uuid NOT NULL,
        CONSTRAINT "PK_CoverBackfillStates" PRIMARY KEY ("EntityKind")
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261017155458_PersistCoverBackfillCursor') THEN
    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
    VALUES ('20261017155458_PersistCoverBackfillCursor', '9.0.7');
    END IF;
END $EF$;
COMMIT;
