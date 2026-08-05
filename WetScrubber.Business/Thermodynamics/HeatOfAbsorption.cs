namespace WetScrubber.Business.Thermodynamics
{
    /// <summary>
    /// Heat of absorption (ΔH_abs, exothermic, negative) for pollutant-solvent pairs.
    /// Used in energy balance: Q = n_absorbed * |ΔH_abs|.
    /// Values in kJ/kmol — all exothermic (negative).
    /// </summary>
    public static class HeatOfAbsorption
    {
        // SO2 + H2O: ~67 kJ/mol (moderately exothermic)
        public const double SO2_Water = -67.0;

        // HCl + H2O: ~72 kJ/mol (highly exothermic)
        public const double HCl_Water = -72.0;

        // NH3 + H2O: ~42 kJ/mol (exothermic, weaker than acid gases)
        public const double NH3_Water = -42.0;

        // H2S + H2O: ~40 kJ/mol
        public const double H2S_Water = -40.0;

        // Cl2 + H2O: ~45 kJ/mol (includes hydrolysis)
        public const double Cl2_Water = -45.0;

        public static double GetByPollutantCode(string code) => code switch
        {
            "SO2" => SO2_Water,
            "HCl" => HCl_Water,
            "NH3" => NH3_Water,
            "H2S" => H2S_Water,
            "Cl2" => Cl2_Water,
            _ => 0.0
        };
    }
}