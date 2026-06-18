namespace FleetWise.ViewModels
{
    // Rendered by GET /Schedule — the weekly bulk planner grid.
    public class ScheduleViewModel
    {
        public DateTime WeekStart { get; set; }
        public DateTime WeekEnd { get; set; }
        public List<DateTime> Days { get; set; } = new();          // 7 dates
        public List<string> Shifts { get; set; } = new() { "Morning", "Afternoon", "Evening" };

        public List<RouteOption> Routes { get; set; } = new();
        public List<VehicleOption> Vehicles { get; set; } = new();
        public List<DriverOption> Drivers { get; set; } = new();

        // key = "routeId|shift|yyyy-MM-dd" -> one OR MORE trips (multiple buses
        // can run the same route/shift/day).
        public Dictionary<string, List<ScheduleCell>> Cells { get; set; } = new();

        public string PrevWeekStart { get; set; }
        public string NextWeekStart { get; set; }
    }

    public class ScheduleCell
    {
        public string TripId { get; set; }
        public string VehicleId { get; set; }
        public int DriverId { get; set; }
        public string TripStatus { get; set; }   // locked if Active/Completed
    }

    // Posted by POST /Schedule/Save
    public class SaveScheduleRequest
    {
        public List<ScheduleCellInput> Cells { get; set; } = new();
        public string WeekStart { get; set; }
        public string WeekEnd { get; set; }
    }

    public class ScheduleCellInput
    {
        public string TripId { get; set; }     // null/empty = new trip to insert
        public int RouteId { get; set; }
        public string Shift { get; set; }
        public string Date { get; set; }       // yyyy-MM-dd
        public string VehicleId { get; set; }
        public int DriverId { get; set; }
    }
}
