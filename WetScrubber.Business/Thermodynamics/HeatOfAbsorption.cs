namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Heat of physical absorption (enthalpy of solution), kJ/mol, negative = exothermic.
    /// GetByPollutantCode returns kJ/kmol. Reaction heat (NaOH, limestone) is NOT included.
    ///
    /// SO2 : -26.97 kJ/mol (Goldberg and Parker, 1985), i.e. -dH/R = 3244 K.
    /// NH3 : -34.1 kJ/mol from d ln H/d(1/T) = 4100 K (Sander 2023). CONFIRM against source.
    /// H2S : -17.5 kJ/mol from d ln H/d(1/T) = 2100 K (Sander 2023). CONFIRM against source.
    /// HCl : -72.0 kJ/mol, UNVERIFIED.
    /// Cl2 : no verified value; returns null (solver reports ThermalCouplingSkipped).
    /// </summary>
    public static class HeatOfAbsorption
    {
        public const double SO2_Water_KJPerMol = -26.97;
        public const double HCl_Water_KJPerMol = -72.0;   // unverified
        public const double NH3_Water_KJPerMol = -34.1;   // confirm
        public const double H2S_Water_KJPerMol = -17.5;   // confirm

        public static double? TryGetKJPerMol(string code) => code switch
        {
            "SO2" => SO2_Water_KJPerMol,
            "HCl" => HCl_Water_KJPerMol,
            "NH3" => NH3_Water_KJPerMol,
            "H2S" => H2S_Water_KJPerMol,
            _ => null
        };

        /// <summary>Heat of absorption, kJ/kmol (negative = exothermic); 0 if unknown.</summary>
        public static double GetByPollutantCode(string code)
            => (TryGetKJPerMol(code) ?? 0.0) * 1000.0;
    }
}