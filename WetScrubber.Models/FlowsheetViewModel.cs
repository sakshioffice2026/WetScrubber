using System.ComponentModel.DataAnnotations;

namespace WetScrubber.Models
{
    public class FlowsheetFormViewModel
    {
        public int Id { get; set; }

        [Required, MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(1000)]
        public string? Description { get; set; }

        [Required]
        public int ProjectId { get; set; }
    }

    public class UnitOperationFormViewModel
    {
        public int Id { get; set; }

        [Required]
        public int FlowsheetId { get; set; }

        [Required, MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        // scrubber | cooler | separator | precooler | misteliminator
        [Required]
        public string Type { get; set; } = "cooler";

        public int SequenceOrder { get; set; }

        // JSON of numeric fields, e.g. {"CoolingDutyKW":50}
        public string? ConfigJson { get; set; }
    }

    public class StreamConnectionFormViewModel
    {
        public int Id { get; set; }

        [Required]
        public int FlowsheetId { get; set; }

        [Required]
        public int SourceUnitId { get; set; }

        [Required]
        public int SinkUnitId { get; set; }

        // gas | liquid | recycle
        [Required]
        public string StreamType { get; set; } = "gas";
    }

    public class FlowsheetRunFormViewModel
    {
        public int Id { get; set; }
        public double ActualFlowM3Hr { get; set; } = 10000;
        public double TemperatureC { get; set; } = 50;
        public double PressurePa { get; set; } = 101325;

        // "SO2:500,H2S:200"
        public string Pollutants { get; set; } = "";
    }
}