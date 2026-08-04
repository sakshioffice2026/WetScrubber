namespace WetScrubber.Database.Enums
{
    // ── Scrubber types ────────────────────────────────────────────
    public enum ScrubberType
    {
        PackedTower = 1,
        VenturiScrubber = 2,
        SprayTower = 3,
        WetCyclone = 4,
        JetEjectorScrubber = 5,
        ImpingementPlate = 6
    }



    // ── Construction materials ────────────────────────────────────
    public enum ConstructionMaterial
    {
        FRP = 1,
        PP = 2,
        HDPE = 3,
        PVC = 4,
        SS316 = 5,
        HastelloyC = 6,
        CarbonSteel = 7
    }

    // ── Project status ────────────────────────────────────────────
    public enum ProjectStatus
    {
        Draft = 1,
        InProgress = 2,
        Completed = 3,
        Archived = 4
    }

    // ── Unit system ───────────────────────────────────────────────
    public enum UnitSystem
    {
        Metric = 1,
        Imperial = 2
    }

    // ── User roles ────────────────────────────────────────────────
    public enum UserRole
    {
        Admin = 1,
        Engineer = 2,
        Viewer = 3
    }

    // ── AI narrative report status ────────────────────────
    // Draft            -> template numbers generated, narrative may or may
    //                     not be drafted yet.
    // NarrativeDrafted -> AI (or template-only) narrative attached, still
    //                     requires human review before it can be exported.
    // Approved         -> a human reviewed and approved; export unlocked.
    // Rejected         -> reviewer rejected the draft narrative.
    public enum ReportStatus
    {
        Draft = 1,
        NarrativeDrafted = 2,
        Approved = 3,
        Rejected = 4
    }

    // Where the narrative text in a report came from. Kept alongside the
    // report so nobody can mistake AI prose for a human-authored summary
    // (or vice-versa) later on.
    public enum NarrativeSource
    {
        TemplateOnly = 1,   // zero AI — canned sentence templates
        AiDrafted = 2    // LLM phrased the prose around the same hard numbers
    }

    // ── chemical property prediction (GNN / Chemprop-style) ──
    // Every predicted row must pass through this pipeline before it can
    // touch the Pollutant or ChemicalReaction master tables.
    public enum PredictionStatus
    {
        Parsed = 1,   // SMILES parsed / structure confirmed
        Predicted = 2,   // model returned a value + confidence
        PendingHumanReview = 3,   // editable fields shown to engineer
        Approved = 4,   // engineer approved -> may write to catalog
        Rejected = 5
    }

    // Confidence is reported as similarity to the model's training data,
    // never as an accuracy claim — that distinction matters enough to be
    // its own type rather than a free-text string.
    public enum ConfidenceBand
    {
        HighSimilarity = 1,  // close to well-represented training structures
        ModerateSimilarity = 2,
        LowSimilarity = 3   // novel scaffold, treat prediction as a rough estimate
    }

    // ──  PE review / sign-off workflow ─────────────────────
    public enum DesignReviewStatus
    {
        Draft = 1,
        UnderReview = 2,
        Approved = 3,   // locked — no further edits without a new revision
        ChangesRequested = 4
    }

    // ── field-performance data capture (for a future
    // calibration model — no model exists yet, this just captures data) ──
    public enum OutcomeDataSource
    {
        FieldMeasurement = 1,
        CommissioningTest = 2,
        ClientReported = 3,
        Other = 4
    }
}