namespace FleetWise.ViewModels
{
    public class DispatchViewModel
    {
        public DateTime ScheduleDate { get; set; }
        public string PrevDate { get; set; }
        public string NextDate { get; set; }
        public bool IsToday { get; set; }
        public int ActiveTrips { get; set; }
        public int TripsNotStarted { get; set; }
        public int UnassignedTrips { get; set; }
        public int FlaggedVehicles { get; set; }
        public int UnavailableDrivers { get; set; }

        public List<RouteDispatchGroup> Routes { get; set; } = new();
    }

    public class RouteDispatchGroup
    {
        public int RouteId { get; set; }
        public string RouteName { get; set; }
        public bool NeedsAssignment { get; set; }
        public List<ShiftGroup> Shifts { get; set; } = new();
    }

    public class ShiftGroup
    {
        public string ShiftType { get; set; }
        public string ShiftStartTime { get; set; }
        public string ShiftEndTime { get; set; }
        public List<TripRow> Trips { get; set; } = new();
    }

    public class TripRow
    {
        public string TripId { get; set; }
        public string VehicleId { get; set; }
        public string PlateNumber { get; set; }
        public string VehicleStatus { get; set; }
        public string DriverName { get; set; }
        public string DriverStatus { get; set; }
        public string TripStatus { get; set; }
    }
}
