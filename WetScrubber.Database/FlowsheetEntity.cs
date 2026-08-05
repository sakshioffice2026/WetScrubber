using System;
using System.Collections.Generic;

namespace WetScrubber.Database
{
    public sealed class FlowsheetEntity
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int ProjectId { get; set; }
        public Project Project { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public ICollection<UnitOperationEntity> UnitOperations { get; set; } = new List<UnitOperationEntity>();
        public ICollection<StreamConnectionEntity> StreamConnections { get; set; } = new List<StreamConnectionEntity>();
    }

    public sealed class UnitOperationEntity
    {
        public int Id { get; set; }
        public int FlowsheetId { get; set; }
        public FlowsheetEntity Flowsheet { get; set; }
        public string Name { get; set; }
        public string Type { get; set; } // "scrubber", "cooler", "separator"
        public int SequenceOrder { get; set; }
        public string ConfigJson { get; set; } // JSON: TowerHeightM, TowerAreaM2, LiquidFlowKgS, etc.
    }

    public sealed class StreamConnectionEntity
    {
        public int Id { get; set; }
        public int FlowsheetId { get; set; }
        public FlowsheetEntity Flowsheet { get; set; }
        public int SourceUnitId { get; set; }
        public int SinkUnitId { get; set; }
        public string StreamType { get; set; } // "gas", "liquid", "recycle"
    }
}