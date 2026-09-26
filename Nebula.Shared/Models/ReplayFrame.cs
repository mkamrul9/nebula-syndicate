// File: Nebula.Shared/Models/ReplayFrame.cs
namespace Nebula.Shared.Models
{
    public class ReplayFrame
    {
        public int Tick { get; set; }
        
        // Key = PlayerId. Value = [Credits, Ironium, Plasma, FeDrones, PlDrones, EmpExpiration]
        public Dictionary<string, double[]> PlayerStats { get; set; } = new();
        
        // Key = ResourceName. Value = Current Price
        public Dictionary<string, decimal> Market { get; set; } = new();
    }
}
