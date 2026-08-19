using System.ComponentModel.DataAnnotations;

namespace FleetWise.ViewModels
{
    /// <summary>The weekly planner grid.</summary>
    public class ScheduleViewModel
    {
        public DateTime WeekStart { get; set; }
        public DateTime WeekEnd { get; set; }
        public List<DateTime> Days { get; set; } = new();          // 7 dates
        public List<string> Shifts { get; set; } = new() { "Morning", "Afternoon", "Evening" };

        public List<RouteOption> Routes { get; set; } = new();
        public List<VehicleOption> Vehicles { get; set; } = new();
        public List<DriverOption> Drivers { get; set; } = new();

        // Keyed by route, shift and date, holding one or more trips, since several buses
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
        // [Required] on a list only checks it is present, and nothing walks into the items
        // unless the property itself carries a validator, so the cap below is also what
        // makes each cell get checked at all.
        [Required, MaxLength(500, ErrorMessage = "Too many cells in one save.")]
        public List<ScheduleCellInput> Cells { get; set; } = new();

        [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Week start must be yyyy-MM-dd.")]
        public string WeekStart { get; set; }

        [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Week end must be yyyy-MM-dd.")]
        public string WeekEnd { get; set; }
        // Dispatcher acknowledged the conflict modal and chose to save anyway.
        public bool Override { get; set; }
    }

    public class ScheduleCellInput
    {
        // Empty means "new trip", so this one allows blank but still bounds the shape.
        [RegularExpression(@"^([A-Za-z0-9_-]{1,64})?$", ErrorMessage = "That is not a trip ID.")]
        public string TripId { get; set; }     // null/empty = new trip to insert

        [Range(1, int.MaxValue, ErrorMessage = "A route is required.")]
        public int RouteId { get; set; }

        [Required, RegularExpression("^(Morning|Afternoon|Evening)$",
            ErrorMessage = "Shift must be Morning, Afternoon or Evening.")]
        public string Shift { get; set; }

        [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Date must be yyyy-MM-dd.")]
        public string Date { get; set; }       // yyyy-MM-dd

        // Blank clears the slot.
        [RegularExpression(@"^([A-Za-z0-9-]{1,20})?$", ErrorMessage = "That is not a vehicle ID.")]
        public string VehicleId { get; set; }

        // 0 clears the slot, so no lower bound of 1 here.
        [Range(0, int.MaxValue, ErrorMessage = "That is not a driver.")]
        public int DriverId { get; set; }
    }
}
