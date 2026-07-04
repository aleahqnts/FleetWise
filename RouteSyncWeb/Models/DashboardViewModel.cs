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

        /// <summary>Passenger counts matching each label. null = future hour (no data yet).</summary>
        public List<int?> ChartData { get; set; } = new();

        /// <summary>Y-axis maximum (defaults to 400).</summary>
        public int ChartYMax { get; set; } = 400;

        /// <summary>Y-axis step size (defaults to 100).</summary>
        public int ChartYStep { get; set; } = 100;

        /// <summary>Today's date (PH), for the header badge.</summary>
        public DateTime Today { get; set; }

        /// <summary>Per-active-trip passenger breakdown (drives the expandable card panel).</summary>
        public List<ActiveTripRow> ActiveTripBreakdown { get; set; } = new();

        // ── Route dropdown ────────────────────────────────────────
        /// <summary>Populated from the Routes table; each item is Value=RouteId, Text=RouteName.</summary>
        public List<SelectListItem> Routes { get; set; } = new();

        // ── Active filter state ───────────────────────────────────
        /// <summary>Currently selected RouteId (null = All Routes).</summary>
        public int? SelectedRouteId { get; set; }

        /// <summary>Display name of the active route filter ("All Routes" when none selected).</summary>
        public string SelectedRouteName => SelectedRouteId.HasValue
            ? Routes.FirstOrDefault(r => r.Value == SelectedRouteId.ToString())?.Text ?? "All Routes"
            : "All Routes";
    }

    /// <summary>One active trip's live passenger line in the Total Passengers breakdown panel.</summary>
    public class ActiveTripRow
    {
        public string TripId { get; set; } = "";
        public string RouteName { get; set; } = "";
        public string VehicleId { get; set; } = "";
        public string ShiftType { get; set; } = "";
        public string Status { get; set; } = "";
        public int Passengers { get; set; }
    }
}
