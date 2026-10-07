namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Heat of physical absorption (enthalpy of solution) for pollutant-water pairs.
    /// Exothermic, so negative. Constants are in kJ/mol; GetByPollutantCode returns
    /// kJ/kmol (kJ/mol x 1000) so it matches the *KJKmol fields used by the solvers.
    ///
    /// SO2: -26.97 kJ/mol (Goldberg and Parker, 1985; consistent with Sander 2023,
    ///      d ln H / d(1/T) = 3100 K).
    /// Other species: carried forward from the previous table, NOT yet verified
    /// against a primary source. Reaction heat (e.g. neutralisation with NaOH) is
    /// not included here.
    /// </summary>
    public static class HeatOfAbsorption
    {
        public const double SO2_Water_KJPerMol = -26.97;
        public const double HCl_Water_KJPerMol = -72.0;   // unverified
        public const double NH3_Water_KJPerMol = -42.0;   // unverified
        public const double H2S_Water_KJPerMol = -40.0;   // unverified
        public const double Cl2_Water_KJPerMol = -45.0;   // unverified

        /// <summary>Heat of absorption, kJ/mol (negative = exothermic); null if unknown.</summary>
        public static double? TryGetKJPerMol(string code) => code switch
        {
            "SO2" => SO2_Water_KJPerMol,
            "HCl" => HCl_Water_KJPerMol,
            "NH3" => NH3_Water_KJPerMol,
            "H2S" => H2S_Water_KJPerMol,
            "Cl2" => Cl2_Water_KJPerMol,
            _ => null
        };

        /// <summary>Heat of absorption, kJ/kmol (negative = exothermic); 0 if unknown.</summary>
        public static double GetByPollutantCode(string code)
            => (TryGetKJPerMol(code) ?? 0.0) * 1000.0;
    }
}