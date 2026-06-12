using Microsoft.AspNetCore.Mvc.Rendering;

namespace FleetWise.Models
{
    public class DashboardViewModel
    {
        // ── Stat cards ───────────────────────────────────────────
        public int ActiveTrips { get; set; }
        public int FlaggedVehicles { get; set; }
        public int TotalPassengers { get; set; }

        /// <summary>Change vs yesterday (positive = up, negative = down, 0 = hide delta).</summary>
        public int PassengerDelta { get; set; }

        public decimal TotalRevenue { get; set; }

        /// <summary>Change vs yesterday in pesos (positive = up, negative = down, 0 = hide delta).</summary>
        public decimal RevenueDelta { get; set; }

        // ── Passenger demand chart ────────────────────────────────
        /// <summary>X-axis hour labels, e.g. ["8:00 AM", "9:00 AM", ...]</summary>
        public List<string> ChartLabels { get; set; } = new();

        /// <summary>Passenger counts matching each label.</summary>
        public List<int> ChartData { get; set; } = new();

        /// <summary>Y-axis maximum (defaults to 400).</summary>
        public int ChartYMax { get; set; } = 400;

        /// <summary>Y-axis step size (defaults to 100).</summary>
        public int ChartYStep { get; set; } = 100;

        // ── Route dropdown ────────────────────────────────────────
        /// <summary>Populated from the Routes table; each item is Value=RouteId, Text=RouteName.</summary>
        public List<SelectListItem> Routes { get; set; } = new();
    }
}
