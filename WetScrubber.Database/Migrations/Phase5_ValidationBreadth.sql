-- Apply once to existing MySQL databases before deploying the Phase 5 code.
-- Fresh databases created from the EF model receive these columns automatically.
ALTER TABLE `ScrubberDesigns` ADD COLUMN `PackingCode` varchar(30) NULL;

ALTER TABLE `ScrubbingLiquidSpecs`
    ADD COLUMN `IsLimestoneSlurry` tinyint(1) NOT NULL DEFAULT 0,
    ADD COLUMN `SolidsLoadingWtPercent` double NOT NULL DEFAULT 0,
    ADD COLUMN `LimestoneParticleDiameterMicron` double NOT NULL DEFAULT 50;

ALTER TABLE `ScrubberGeometries`
    ADD COLUMN `PackingCode` varchar(30) NULL,
    ADD COLUMN `PackingSizingMethod` varchar(100) NULL,
    ADD COLUMN `IsLimestoneSlurry` tinyint(1) NOT NULL DEFAULT 0,
    ADD COLUMN `SolidsLoadingWtPercent` double NOT NULL DEFAULT 0,
    ADD COLUMN `SlurryApparentViscosityMPas` double NOT NULL DEFAULT 0;
