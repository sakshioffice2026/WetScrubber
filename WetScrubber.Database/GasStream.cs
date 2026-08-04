using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WetScrubber.Database
{
    public class GasStream
    {
        [Key]
        public int GasStreamId { get; set; }

        public int DesignId { get; set; }

        public double ActualFlowRate { get; set; }
        public double NormalFlowRate { get; set; }
        public double InletTemperature { get; set; }
        public double InletPressure { get; set; } = 101325;
        public double MoistureContent { get; set; }
        public double GasDensity { get; set; } = 1.2;
        public double GasViscosity { get; set; } = 0.0000185;

        // ── Navigation ───────────────────────────────────────────
        public ScrubberDesign Design { get; set; } = null!;

        public ICollection<PollutantStream> Pollutants { get; set; } = new List<PollutantStream>();
    }

}
