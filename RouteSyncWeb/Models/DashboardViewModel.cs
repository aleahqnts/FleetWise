using Microsoft.AspNetCore.Mvc.Rendering;

namespace FleetWise.Models
{
    public class DashboardViewModel
    {
        // Stat cards.
        public int ActiveTrips { get; set; }
        public int FlaggedVehicles { get; set; }
        public int TotalPassengers { get; set; }

        /// <summary>Change against yesterday. Zero hides the indicator.</summary>
        public int PassengerDelta { get; set; }

        public decimal TotalRevenue { get; set; }

        /// <summary>Change against yesterday in pesos. Zero hides the indicator.</summary>
        public decimal RevenueDelta { get; set; }

        // Passenger demand chart.
        /// <summary>Hour labels along the horizontal axis.</summary>
        public List<string> ChartLabels { get; set; } = new();

        /// <summary>Passenger counts against each label. A null entry is an hour still to come.</summary>
        public List<int?> ChartData { get; set; } = new();

        /// <summary>Y-axis maximum (defaults to 400).</summary>
        public int ChartYMax { get; set; } = 400;

        /// <summary>Y-axis step size (defaults to 100).</summary>
        public int ChartYStep { get; set; } = 100;

        /// <summary>Today's date in Philippine time, for the header.</summary>
        public DateTime Today { get; set; }

        /// <summary>Passenger figures per active trip, shown in the expandable panel.</summary>
        public List<ActiveTripRow> ActiveTripBreakdown { get; set; } = new();

        // Route dropdown.
        /// <summary>The available routes, as identifier and name pairs.</summary>
        public List<SelectListItem> Routes { get; set; } = new();

        // Active filter state.
        /// <summary>The selected route, or null for all routes.</summary>
        public int? SelectedRouteId { get; set; }

        /// <summary>The active route filter's display name.</summary>
        public string SelectedRouteName => SelectedRouteId.HasValue
            ? Routes.FirstOrDefault(r => r.Value == SelectedRouteId.ToString())?.Text ?? "All Routes"
            : "All Routes";
    }

    /// <summary>One active trip's passenger figures in the breakdown panel.</summary>
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
