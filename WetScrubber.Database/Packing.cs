using System;

namespace WetScrubber.Database
{
    /// <summary>
    /// Packed-column geometry library — replaces the hardcoded
    /// DefaultSurfaceArea / DefaultNominalPackingSizeM constants in
    /// ScrubberCalculationEngine. Each row is a packing type with its
    /// manufacturer/published specific area, nominal size, etc. — data
    /// for the Onda correlation.
    /// </summary>
    public class Packing
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";           // e.g., "PallRing50" / "BerlSaddle25"
        public string ManufacturerType { get; set; } = ""; // "Pall Ring" / "Berl Saddle" / "Raschig Ring"
        public double NominalSizeM { get; set; }          // Characteristic dimension (m)
        public double SpecificAreaM2M3 { get; set; }      // aT, surface area per volume of packed bed
        public double CriticalSurfaceTensionNM { get; set; } // sigma_c, material-dependent (water wetting)
        public string Material { get; set; } = "";        // "Plastic" / "Metal" / "Ceramic"
        public double VoidageFraction { get; set; }       // Holdup, typically 0.7-0.95
        public bool IsActive { get; set; } = true;
        public bool ValidatedFlag { get; set; } = false;  // Sourced from vendor datasheets?
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }

        public string DisplayName { get; set; }
    }
}